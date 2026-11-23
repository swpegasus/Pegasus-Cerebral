"""Stdlib unit/CLI fixtures only. No model, dependency install, renderer or GPU.

Fake mesh bytes and mocked upstream methods prove contracts and file lifecycle;
they are never reported as live TRELLIS/GLB/provider qualification.
"""
from pathlib import Path
import copy
import hashlib
import io
import json
import struct
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

RUNTIME = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RUNTIME))
from contracts import CONTROL_SCHEMA, MODEL_REVISIONS, REQUEST_SCHEMA, SOURCE_REVISION, RuntimeFailure, control_contract, decode_json, file_sha, json_bytes, request_contract
from runner import Emitter, ModelBackend, Runtime, publish_directory, serve
from install_runtime import validate_profile


def fixture_glb():
    # Minimal framing fixture, not a model-generated semantic mesh.
    chunks = struct.pack("<I4s", 4, b"JSON") + b"{}  " + struct.pack("<I4s", 0, b"BIN\0")
    return struct.pack("<4sII", b"glTF", 2, 12 + len(chunks)) + chunks


class FakeBackend:
    def __init__(self):
        self.calls = []

    def execute(self, request, image, mesh, stage, progress):
        self.calls.append(request)
        progress("unit_mock_only")
        artifacts = []
        for index in range(request["parameters"].get("numSamples", 1)):
            name = f"model-{index+1:03d}.glb"
            (stage / name).write_bytes(fixture_glb())
            artifacts.append({"file": name, "role": "mesh", "mediaType": "model/gltf-binary"})
        return {"artifacts": artifacts, "actualResolution": 1024,
                "normalization": {"unitFixtureOnly": True}, "provenance": {"unitMockOnly": True}}

    def load(self, kind, progress):
        progress("unit_mock_only")
        return {"modelMaterialized": True, "kind": kind, "unitMockOnly": True}


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.inputs, self.outputs = self.root / "input-mount", self.root / "output-mount"
        self.inputs.mkdir()
        self.outputs.mkdir()
        self.input = b"unit image byte snapshot; not decoded by fake backend"
        self.backend = FakeBackend()
        self.runtime = Runtime(self.inputs, self.outputs, self.backend)
        self.events = io.BytesIO()

    def request(self, job="unit-job", kind="image_to_3d", parameters=None):
        jobdir = self.inputs / job
        jobdir.mkdir(exist_ok=True)
        (jobdir / "image.png").write_bytes(self.input)
        value = {"schema": REQUEST_SCHEMA, "jobId": job, "effectId": "effect-" + job, "kind": kind,
                 "image": {"path": "image.png", "sha256": hashlib.sha256(self.input).hexdigest()}, "parameters": parameters or {}}
        if kind == "mesh_retexture":
            (jobdir / "source.glb").write_bytes(fixture_glb())
            value["mesh"] = {"path": "source.glb", "sha256": hashlib.sha256(fixture_glb()).hexdigest()}
        return value

    def execute(self, request):
        admitted = request_contract(request)
        return self.runtime.execute(admitted, Emitter(self.events, {"jobId": admitted["jobId"], "effectId": admitted["effectId"]}))

    def test_typed_defaults_and_all_pipeline_modes(self):
        for mode in ("512", "1024", "1024_cascade", "1536_cascade"):
            value = request_contract(self.request(parameters={"pipelineType": mode}))
            self.assertEqual(value["parameters"]["pipelineType"], mode)
            self.assertEqual(value["parameters"]["shape"]["guidanceInterval"], [0.6, 1])
            self.assertEqual(value["parameters"]["export"]["textureSize"], 2048)

    def test_8k_nonfinite_bool_integer_unknown_and_batch_limits_rejected(self):
        bad = [{"export": {"textureSize": 8192}}, {"numSamples": 5}, {"numSamples": True},
               {"shape": {"guidanceStrength": float("nan")}}, {"seed": -1},
               {"maxNumTokens": 49153}, {"lowVram": False}, {"returnLatent": True},
               {"shape": {"guidanceInterval": [0.9, 0.1]}}, {"shell": "not allowed"}]
        for params in bad:
            with self.subTest(params=params), self.assertRaises(RuntimeFailure):
                request_contract(self.request(parameters=params))

    def test_retexture_rejects_generation_only_settings(self):
        for parameters in ({"numSamples": 2}, {"pipelineType": "512"}, {"export": {"remesh": False}}):
            with self.subTest(parameters=parameters), self.assertRaises(RuntimeFailure):
                request_contract(self.request(kind="mesh_retexture", parameters=parameters))

    def test_paths_ids_mesh_requirement_and_unknown_fields(self):
        for path in ("../other.png", "/absolute.png", "C:/file.png", "a\\b.png", "a//b.png", "a/./b.png"):
            value = self.request()
            value["image"]["path"] = path
            with self.subTest(path=path), self.assertRaises(RuntimeFailure):
                request_contract(value)
        for change in ({"jobId": "../job"}, {"mesh": {}}, {"executable": "forbidden"}):
            value = self.request()
            value.update(change)
            with self.assertRaises(RuntimeFailure):
                request_contract(value)

    def test_json_duplicate_nonfinite_and_budget_refuse(self):
        for data in (b'{"x":1,"x":2}', b'{"x":NaN}', b'\xff'):
            with self.assertRaises(RuntimeFailure):
                decode_json(data)
        with self.assertRaises(RuntimeFailure):
            decode_json(b"{}", 1)

    def test_three_outputs_hashed_manifest_and_readonly_input_mount(self):
        value = self.request(parameters={"numSamples": 3})
        before = {p.relative_to(self.inputs): p.read_bytes() for p in self.inputs.rglob("*") if p.is_file()}
        result = self.execute(value)
        self.assertEqual(len(result["artifacts"]), 3)
        self.assertEqual([x["path"] for x in result["artifacts"]], [f"outputs/model-{i:03d}.glb" for i in range(1, 4)])
        for item in [*result["artifacts"], result["manifestArtifact"]]:
            path = self.outputs / value["jobId"] / item["path"]
            self.assertEqual(item["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(item["bytes"], path.stat().st_size)
        self.assertFalse((self.outputs / value["jobId"] / "partial").exists())
        self.assertEqual(before, {p.relative_to(self.inputs): p.read_bytes() for p in self.inputs.rglob("*") if p.is_file()})

    def test_replay_never_executes_or_overwrites(self):
        value = self.request()
        self.execute(value)
        before = {p.relative_to(self.outputs): p.read_bytes() for p in self.outputs.rglob("*") if p.is_file()}
        with self.assertRaises(RuntimeFailure) as error:
            self.execute(value)
        self.assertEqual(error.exception.code, "job_already_claimed")
        self.assertEqual(len(self.backend.calls), 1)
        self.assertEqual(before, {p.relative_to(self.outputs): p.read_bytes() for p in self.outputs.rglob("*") if p.is_file()})

    def test_publication_cannot_replace_an_existing_even_empty_directory(self):
        stage, final = self.root / "publication-stage", self.root / "publication-final"
        stage.mkdir()
        final.mkdir()
        (stage / "owned").write_text("preserved")
        with self.assertRaises((OSError, RuntimeFailure)):
            publish_directory(stage, final)
        self.assertEqual((stage / "owned").read_text(), "preserved")
        self.assertEqual(list(final.iterdir()), [])

    def test_short_pipe_writes_preserve_complete_event_frame(self):
        class ShortPipe(io.BytesIO):
            def write(self, data):
                return super().write(data[:7])
        pipe = ShortPipe()
        Emitter(pipe).send("unit-test", value="complete event despite short writes")
        self.assertEqual(json.loads(pipe.getvalue())["event"], "unit-test")

    def test_changed_input_rejected_before_claim_or_backend(self):
        value = self.request()
        (self.inputs / value["jobId"] / "image.png").write_bytes(b"changed")
        with self.assertRaises(RuntimeFailure):
            self.execute(value)
        self.assertFalse((self.outputs / value["jobId"]).exists())
        self.assertFalse(self.backend.calls)

    def test_post_inference_input_mutation_preserves_partial_not_success(self):
        value = self.request()
        execute = self.backend.execute
        def mutate(*args):
            details = execute(*args)
            (self.inputs / value["jobId"] / "image.png").write_bytes(b"host changed input")
            return details
        with patch.object(self.backend, "execute", side_effect=mutate), self.assertRaises(RuntimeFailure):
            self.execute(value)
        self.assertTrue((self.outputs / value["jobId"] / "partial").exists())
        self.assertFalse((self.outputs / value["jobId"] / "outputs").exists())

    def test_undeclared_artifact_refused(self):
        value = self.request()
        execute = self.backend.execute
        def extra(request, image, mesh, stage, progress):
            result = execute(request, image, mesh, stage, progress)
            (stage / "unexpected.txt").write_text("not admitted")
            return result
        with patch.object(self.backend, "execute", side_effect=extra), self.assertRaises(RuntimeFailure):
            self.execute(value)
        self.assertFalse((self.outputs / value["jobId"] / "outputs").exists())

    def test_model_failure_ends_stdio_child_with_reconciliation(self):
        value = self.request()
        with patch.object(self.backend, "execute", side_effect=RuntimeError("unit fake failure")), patch("sys.stderr", io.StringIO()):
            code = serve(io.BytesIO(json_bytes(value)), self.events, runtime=self.runtime)
        events = [json.loads(line) for line in self.events.getvalue().splitlines()]
        self.assertEqual(code, 2)
        self.assertEqual(events[-1]["event"], "error")
        self.assertEqual(events[-1]["error"]["code"], "runtime_failure")
        self.assertTrue(events[-1]["partial"])
        self.assertTrue(events[-1]["reconciliationRequired"])

    def test_validate_cli_rejects_8k_and_model_batch_without_cuda_import(self):
        first = self.request(parameters={"export": {"textureSize": 8192}})
        second = self.request(job="other-job", parameters={"numSamples": 99})
        result = subprocess.run([sys.executable, "-I", "-B", str(RUNTIME / "runner.py"), "--stdio", "--validate"],
                                input=json_bytes(first) + json_bytes(second), capture_output=True, timeout=15)
        events = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(result.returncode, 2)
        self.assertEqual([e["event"] for e in events], ["ready", "error", "error"])
        self.assertTrue(events[0]["validationOnly"])
        self.assertTrue(all(e.get("error", {}).get("code") == "parameter" for e in events[1:]))
        self.assertEqual(result.stderr, b"")
        self.assertEqual(events[1]["jobId"], first["jobId"])

    def test_validation_constructs_no_backend(self):
        value = self.request()
        with patch("runner.ModelBackend", side_effect=AssertionError("validation must not construct a model")):
            code = serve(io.BytesIO(json_bytes(value)), self.events, validate_only=True)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(self.events.getvalue().splitlines()[-1])["event"], "validated")

    def test_host_load_control_rejects_input_or_mount_selection(self):
        value = {"schema": CONTROL_SCHEMA, "action": "load", "controlId": "unit-load", "kind": "image_to_3d", "lowVram": True}
        self.assertEqual(control_contract(value)["kind"], "image_to_3d")
        for addition in ({"image": {}}, {"inputRoot": "/elsewhere"}, {"lowVram": False}):
            with self.subTest(addition=addition), self.assertRaises(RuntimeFailure):
                control_contract({**value, **addition})

    def test_load_control_validate_never_materializes_model(self):
        value = {"schema": CONTROL_SCHEMA, "action": "load", "controlId": "unit-load", "kind": "image_to_3d", "lowVram": True}
        with patch("runner.ModelBackend", side_effect=AssertionError("No model in validation")):
            code = serve(io.BytesIO(json_bytes(value)), self.events, validate_only=True)
        events = [json.loads(x) for x in self.events.getvalue().splitlines()]
        self.assertEqual(code, 0)
        self.assertEqual(events[-1]["event"], "validated")
        self.assertEqual(events[-1]["controlId"], "unit-load")
        self.assertNotIn("loaded", [x["event"] for x in events])

    def test_load_control_emits_confirmed_result_unit_mock_only(self):
        value = {"schema": CONTROL_SCHEMA, "action": "load", "controlId": "unit-load", "kind": "image_to_3d", "lowVram": True}
        code = serve(io.BytesIO(json_bytes(value)), self.events, runtime=self.runtime)
        events = [json.loads(x) for x in self.events.getvalue().splitlines()]
        self.assertEqual(code, 0)
        self.assertEqual(events[-1]["event"], "loaded")
        self.assertTrue(events[-1]["result"]["unitMockOnly"])
        self.assertTrue(events[-1]["result"]["modelMaterialized"])

    def test_upstream_mapping_and_each_sample_export_unit_mock_only(self):
        value = request_contract(self.request(parameters={"numSamples": 2, "pipelineType": "1536_cascade"}))
        stage = self.root / "mock-export"
        stage.mkdir()
        generated = SimpleNamespace(vertices=object(), faces=object(), attrs=object(), coords=object(), layout=object(), voxel_size=object(), simplify=lambda _: None)
        calls = []
        class Pipeline:
            def run(self, image, **kwargs):
                calls.append(kwargs)
                return [generated, generated], (None, None, 1408)
        class Output:
            vertices, faces = [None] * 8, [None] * 12
            def export(self, path, **kwargs):
                Path(path).write_bytes(fixture_glb())
        exports = []
        def export(**kwargs):
            exports.append(kwargs)
            return Output()
        torch = SimpleNamespace(__version__="unit-fake", version=SimpleNamespace(cuda="unit-fake"),
                                cuda=SimpleNamespace(get_device_name=lambda _: "unit-fake", get_device_capability=lambda _: (0, 0),
                                                     max_memory_allocated=lambda _: 0, max_memory_reserved=lambda _: 0))
        backend = ModelBackend(self.root, self.root, self.root)
        with patch.object(backend, "_image", return_value=object()), patch.object(backend, "_pipeline", return_value=(Pipeline(), torch)), \
             patch.dict(sys.modules, {"o_voxel": SimpleNamespace(postprocess=SimpleNamespace(to_glb=export))}):
            result = backend.execute(value, b"not decoded", None, stage, lambda *a, **k: None)
        self.assertEqual(len(result["artifacts"]), 2)
        self.assertEqual(result["actualResolution"], 1408)
        self.assertEqual(len(exports), 2)
        self.assertTrue(calls[0]["return_latent"])
        self.assertEqual(calls[0]["num_samples"], 2)
        self.assertEqual(exports[0]["texture_size"], 2048)
        self.assertEqual(exports[0]["remesh_project"], 0)

    def test_model_lock_data_inventory_then_extra_code_refusal_unit_only(self):
        modelroot, source, scratch = self.root / "models", self.root / "source", self.root / "configs"
        modelroot.mkdir()
        source.mkdir()
        (source / ".source-revision").write_text(SOURCE_REVISION)
        lock = {"schema": "pegasus.trellis2.model-lock.v1", "sourceRevision": SOURCE_REVISION, "models": {}}
        for name, pin in MODEL_REVISIONS.items():
            directory = modelroot / name
            directory.mkdir()
            member = directory / "unit-data.json"
            member.write_bytes(b"{}\n")
            lock["models"][name] = {"repository": pin[0], "revision": pin[1],
                                   "files": {member.name: {"bytes": member.stat().st_size, "sha256": file_sha(member)}}}
        (modelroot / "model-lock.json").write_bytes(json_bytes(lock))
        backend = ModelBackend(modelroot, scratch, source)
        backend.verify_models()
        self.assertTrue(backend.models_verified)
        # This proves data-admission behavior only; dummy JSON is not a model.
        (modelroot / "trellis2" / "unexpected.py").write_text("never imported")
        with self.assertRaises(RuntimeFailure) as error:
            ModelBackend(modelroot, scratch, source).verify_models()
        self.assertEqual(error.exception.code, "model_inventory")

    def test_kind_switch_keeps_only_one_cached_pipeline_unit_mock_only(self):
        backend = ModelBackend(self.root, self.root, self.root)
        built, emptied = [], []
        class Pipeline:
            low_vram = True
            def cuda(self):
                return self
        class Factory:
            @staticmethod
            def from_pretrained(*args, **kwargs):
                instance = Pipeline()
                built.append(instance)
                return instance
        fake_torch = SimpleNamespace(cuda=SimpleNamespace(is_available=lambda: True, empty_cache=lambda: emptied.append(True)))
        fake_pipelines = SimpleNamespace(Trellis2ImageTo3DPipeline=Factory, Trellis2TexturingPipeline=Factory)
        first = request_contract(self.request())
        second = request_contract(self.request(job="retexture-job", kind="mesh_retexture"))
        with patch.object(backend, "verify_models"), patch.object(backend, "_config", return_value=self.root / "unit-config.json"), \
             patch.dict(sys.modules, {"torch": fake_torch, "trellis2": SimpleNamespace(__path__=[]), "trellis2.pipelines": fake_pipelines}), \
             patch.dict("os.environ", {}), patch.object(sys, "path", list(sys.path)):
            backend._pipeline(first, lambda *a, **k: None)
            backend._pipeline(first, lambda *a, **k: None)
            backend._pipeline(second, lambda *a, **k: None)
        self.assertEqual(len(built), 2)
        self.assertEqual(list(backend.pipelines), ["mesh_retexture"])
        self.assertEqual(emptied, [True])


class BuildProfileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source, self.wheels = self.root / "sources.json", self.root / "wheels.lock"
        self.source.write_text("unit source-lock identity fixture")
        self.wheels.write_text("unit-fixture==1.0 --hash=sha256:" + "a" * 64 + "\n")
        self.base = "nvcr.io/nvidia/pytorch@sha256:" + "b" * 64
        self.profile = {"schema": "pegasus.trellis2.build-profile.v1", "targetPlatform": "linux/arm64",
                        "cudaArchitecture": "12.1", "baseImage": self.base, "pythonVersion": "unit-python",
                        "torchVersion": "unit-torch", "sourceLockSha256": file_sha(self.source),
                        "pythonWheelLockSha256": file_sha(self.wheels), "qualificationRequired": True, "qualified": False}

    def verify(self):
        with patch("install_runtime.platform.machine", return_value="aarch64"), \
             patch("install_runtime.platform.python_version", return_value="unit-python"), \
             patch("install_runtime.subprocess.run", side_effect=AssertionError("Profile unit check must not build or provision")):
            validate_profile(self.profile, base_image=self.base, target_arch="arm64", lock_path=self.source, wheel_lock=self.wheels)

    def test_complete_fixture_admits_only_build_identity_not_hardware(self):
        self.verify()
        self.assertFalse(self.profile["qualified"])

    def test_example_and_tag_only_base_refuse(self):
        for change in ({"baseImage": None}, {"baseImage": "nvcr.io/nvidia/pytorch:unqualified"},
                       {"sourceLockSha256": "0" * 64}, {"qualified": True}, {"torchVersion": None}):
            original = dict(self.profile)
            self.profile.update(change)
            with self.subTest(change=change), self.assertRaises(RuntimeFailure):
                self.verify()
            self.profile = original

    def test_network_or_include_directive_cannot_enter_offline_wheel_lock(self):
        for directive in ("--extra-index-url https://example.invalid", "-r another.lock", "--editable ./code",
                          "package @ https://example.invalid/package.whl", "package @ file:///tmp/package.whl"):
            self.wheels.write_text(directive + "\nunit==1 --hash=sha256:" + "a" * 64)
            self.profile["pythonWheelLockSha256"] = file_sha(self.wheels)
            with self.subTest(directive=directive), self.assertRaises(RuntimeFailure):
                self.verify()

    def test_hashed_exact_pin_continuations_are_admitted_offline(self):
        lines = ["unit-fixture==1.0 " + chr(92), "    --hash=sha256:" + "a" * 64 + " " + chr(92),
                 "    --hash=sha256:" + "b" * 64]
        self.wheels.write_text("\n".join(lines) + "\n")
        self.profile["pythonWheelLockSha256"] = file_sha(self.wheels)
        self.verify()

    def test_source_model_pins_match_runtime_and_stay_unqualified(self):
        lock = json.loads((RUNTIME / "sources.lock.json").read_text())
        self.assertFalse(lock["qualified"])
        self.assertTrue(lock["qualificationRequired"])
        self.assertEqual(lock["sources"][0]["revision"], SOURCE_REVISION)
        for name, pin in MODEL_REVISIONS.items():
            self.assertEqual((lock["models"][name]["repository"], lock["models"][name]["revision"]), pin)
        for item in lock["sources"]:
            self.assertRegex(item["revision"], r"^[0-9a-f]{40}$")


if __name__ == "__main__":
    unittest.main()
