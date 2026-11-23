"""Persistent, offline model IPC child; the host owns its container lifetime.

No HTTP server, background queue, authentication, network provisioner or idle
timer belongs here. The fixed mounts are operator-owned, not request parameters.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import io
import json
import math
import os
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))  # -I excludes the script directory from sys.path.
from contracts import (CONTROL_SCHEMA, EVENT_SCHEMA, MAX_ARTIFACT, MAX_IMAGE, MAX_LINE, MAX_MESH, MAX_PIXELS,
                       MODEL_REVISIONS, SOURCE_REVISION, RuntimeFailure, confined, decode_json,
                       control_contract, file_sha, identifier, input_bytes, json_bytes, object_only, ordinary, relative,
                       request_contract, require, write_new)

JOBS = Path("/data/jobs")
RESULTS = Path("/data/results")
MODELS = Path("/models")
CONFIGS = Path("/tmp/trellis-runtime-configs")
SOURCE = Path("/opt/trellis/source")


def utc():
    return datetime.now(timezone.utc).isoformat()


def sampler(value):
    return {"steps": value["steps"], "guidance_strength": value["guidanceStrength"],
            "guidance_rescale": value["guidanceRescale"], "guidance_interval": tuple(value["guidanceInterval"]),
            "rescale_t": value["rescaleT"]}


class Emitter:
    def __init__(self, output, identity=None):
        self.output, self.identity, self.sequence = output, identity or {}, 0

    def send(self, event, **fields):
        value = {"schema": EVENT_SCHEMA, "event": event, "sequence": self.sequence,
                 "utc": utc(), **self.identity, **fields}
        self.sequence += 1
        data = json_bytes(value)
        require(len(data) <= MAX_LINE, "event_budget", "Runtime event exceeded 128 KiB.")
        remaining = memoryview(data)
        while remaining:
            written = self.output.write(remaining)
            require(type(written) is int and written > 0, "event_io", "Protocol pipe did not accept the complete event.")
            remaining = remaining[written:]
        self.output.flush()


class ModelBackend:
    """Lazy imports and cached pipelines keep validation entirely CUDA-free."""
    def __init__(self, model_root=MODELS, config_root=CONFIGS, source_root=SOURCE):
        self.model_root, self.config_root, self.source_root = map(ordinary, (model_root, config_root, source_root))
        self.models_verified = False
        self.pipelines = {}
        self.model_lock_sha = None

    def verify_models(self):
        if self.models_verified:
            return
        require((self.source_root / ".source-revision").read_text(encoding="ascii").strip() == SOURCE_REVISION,
                "source_revision", "Image does not attest the pinned Microsoft source revision.")
        path = confined(self.model_root, self.model_root / "model-lock.json")
        lock = decode_json(path.read_bytes(), 2 * 1024 * 1024)
        object_only(lock, ("schema", "sourceRevision", "models"), "model lock", ("schema", "sourceRevision", "models"))
        require(lock["schema"] == "pegasus.trellis2.model-lock.v1" and lock["sourceRevision"] == SOURCE_REVISION
                and type(lock["models"]) is dict and set(lock["models"]) == set(MODEL_REVISIONS),
                "model_lock", "The fixed local model lock is missing, unsupported or incomplete.")
        for name, (repository, revision) in MODEL_REVISIONS.items():
            model = object_only(lock["models"][name], ("repository", "revision", "files"), "model", ("repository", "revision", "files"))
            require(model["repository"] == repository and model["revision"] == revision
                    and type(model["files"]) is dict and 0 < len(model["files"]) <= 512,
                    "model_revision", "Model repository/revision or file inventory differs from the qualified pins.")
            directory = confined(self.model_root, self.model_root / name)
            expected = set()
            for member, pin in model["files"].items():
                rel = relative(member)
                require(rel.suffix.lower() in (".json", ".safetensors", ".txt"), "model_code", "Models may contain only admitted data, never Python/pickle/native code.")
                item = confined(directory, directory.joinpath(*rel.parts))
                object_only(pin, ("bytes", "sha256"), "file pin", ("bytes", "sha256"))
                require(type(pin["bytes"]) is int and pin["bytes"] > 0 and type(pin["sha256"]) is str
                        and len(pin["sha256"]) == 64 and item.is_file() and item.stat().st_size == pin["bytes"]
                        and file_sha(item) == pin["sha256"], "model_integrity", "A pinned local model file is missing or changed.")
                expected.add(item.relative_to(directory).as_posix())
            actual = set()
            for item in directory.rglob("*"):
                ordinary(item)
                if item.is_file():
                    actual.add(item.relative_to(directory).as_posix())
            require(actual == expected, "model_inventory", "Local model data contains unadmitted files.")
        self.model_lock_sha = file_sha(path)
        self.models_verified = True

    def _config(self, kind):
        # Project the pinned public config to fixed local data roots. Nothing is
        # downloaded and no operator-supplied class/code selection is accepted.
        name = "pipeline.json" if kind == "image_to_3d" else "texturing_pipeline.json"
        original = confined(self.model_root, self.model_root / "trellis2" / name)
        config = decode_json(original.read_bytes(), 128 * 1024)
        expected_class = "Trellis2ImageTo3DPipeline" if kind == "image_to_3d" else "Trellis2TexturingPipeline"
        require(config.get("name") == expected_class, "model_config", "Unexpected pipeline class.")
        args = config["args"]
        for key, value in args["models"].items():
            require(type(value) is str, "model_config", "Invalid checkpoint declaration.")
            if key == "sparse_structure_decoder":
                require(value == "microsoft/TRELLIS-image-large/ckpts/ss_dec_conv3d_16l8_fp16",
                        "model_config", "Unexpected sparse decoder declaration.")
                args["models"][key] = "../shape_decoder/ckpts/ss_dec_conv3d_16l8_fp16"
            else:
                require(value.startswith("ckpts/") and relative(value).suffix == "", "model_config", "Only local checkpoint stems are admitted.")
        require(args["image_cond_model"]["name"] == "DinoV3FeatureExtractor" and args["rembg_model"]["name"] == "BiRefNet",
                "model_config", "Unexpected conditioning/background implementation.")
        args["image_cond_model"]["args"]["model_name"] = str(self.model_root / "dinov3")
        args["rembg_model"]["args"]["model_name"] = str(self.model_root / "rmbg")
        self.config_root.mkdir(parents=True, exist_ok=True)
        path = confined(self.config_root, self.config_root / name)
        data = json_bytes(config)
        if path.exists():
            require(path.read_bytes() == data, "model_config", "An existing projected config differs; replace the owned runtime process instead of overwriting it.")
        else:
            write_new(path, config)
        return path

    def _pipeline(self, request, progress):
        progress("verifying_models", cached=self.models_verified)
        self.verify_models()
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_DATASETS_OFFLINE="1",
                          HF_HUB_DISABLE_TELEMETRY="1", TORCH_FORCE_WEIGHTS_ONLY_LOAD="1")
        if str(self.source_root) not in sys.path:
            sys.path.insert(0, str(self.source_root))
        import torch
        require(torch.cuda.is_available(), "gpu_unavailable", "The qualified GPU runtime is unavailable.")
        kind = request["kind"]
        if kind not in self.pipelines:
            progress("preparing-pipeline", kind=kind, pipelineCacheMax=1)
            # Only one operation's model stack is retained in the initial
            # profile. This is component selection, not a whole-runtime unload.
            # Allocator cleanup alone is never reported as released residency.
            if self.pipelines:
                import gc
                self.pipelines.clear()
                gc.collect()
                torch.cuda.empty_cache()
            progress("loading_model", kind=kind)
            from trellis2.pipelines import Trellis2ImageTo3DPipeline, Trellis2TexturingPipeline
            factory = Trellis2ImageTo3DPipeline if kind == "image_to_3d" else Trellis2TexturingPipeline
            pipeline = factory.from_pretrained(str(self.model_root / "trellis2"), config_file=str(self._config(kind)))
            self.pipelines[kind] = pipeline
        pipeline = self.pipelines[kind]
        # A previous fully resident setting must first move its models to CPU.
        # There is one serial inference request; the host owns total capacity.
        if pipeline.low_vram != request["parameters"]["lowVram"]:
            pipeline.cpu()
            pipeline.low_vram = request["parameters"]["lowVram"]
        pipeline.cuda()
        return pipeline, torch

    @staticmethod
    def _image(data):
        import warnings
        from PIL import Image
        Image.MAX_IMAGE_PIXELS = MAX_PIXELS
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            image = Image.open(io.BytesIO(data))
            require(image.format in ("PNG", "JPEG", "WEBP") and getattr(image, "n_frames", 1) == 1
                    and 0 < image.width * image.height <= MAX_PIXELS, "image", "Use one bounded PNG/JPEG/WebP image.")
            image.load()
            return image.convert("RGBA" if "A" in image.getbands() or "transparency" in image.info else "RGB")

    def load(self, kind, progress):
        pipeline, torch = self._pipeline({"kind": kind, "parameters": {"lowVram": True}}, progress)
        # Materialization must construct the actual pinned pipeline and establish
        # usable CUDA execution, not equate a ready pipe with loaded models.
        with torch.inference_mode():
            probe = torch.zeros(1, device="cuda")
            torch.cuda.synchronize()
            del probe
        return {"modelMaterialized": True, "kind": kind, "sourceRevision": SOURCE_REVISION,
                "modelLockSha256": self.model_lock_sha, "pipelineCacheMax": 1,
                "mode": "low-vram-staged", "device": torch.cuda.get_device_name(0)}

    @staticmethod
    def _mesh(data, suffix):
        import numpy as np
        import trimesh
        require(suffix in (".glb", ".ply", ".obj"), "mesh", "Retexture accepts self-contained GLB, PLY or OBJ geometry.")
        # No directory/web resolver: inputs cannot fetch external buffers,
        # textures or materials. The model generates replacement materials.
        mesh = trimesh.load(io.BytesIO(data), file_type=suffix[1:], process=False,
                            resolver=trimesh.resolvers.DictResolver({}), skip_materials=True)
        merged = isinstance(mesh, trimesh.Scene)
        if merged:
            mesh = mesh.to_mesh()
        require(isinstance(mesh, trimesh.Trimesh) and 0 < len(mesh.vertices) <= 2000000
                and 0 < len(mesh.faces) <= 4000000 and np.isfinite(mesh.vertices).all(),
                "mesh", "Mesh is empty, non-finite or exceeds geometry limits.")
        bounds = np.asarray(mesh.bounds)
        extent = float((bounds[1] - bounds[0]).max())
        require(math.isfinite(extent) and extent > 0, "mesh", "Mesh has degenerate bounds.")
        scale = 0.99999 / extent
        require(math.isfinite(scale) and scale > 0 and mesh.faces.ndim == 2 and mesh.faces.shape[1] == 3
                and mesh.faces.min() >= 0 and mesh.faces.max() < len(mesh.vertices),
                "mesh", "Mesh has invalid triangle indices or an unrepresentable normalization scale.")
        normalization = {"profile": "upstream-centered-uniform-normalization-and-new-uvs-v1",
                         "inputCenter": (bounds[0] / 2 + bounds[1] / 2).tolist(), "scale": scale,
                         "sourceSceneMerged": merged, "inputVertices": len(mesh.vertices), "inputTriangles": len(mesh.faces),
                         "preservesWorldTransform": False, "preservesUvLayout": False,
                         "preservesVertexOrIndexIdentity": False,
                         "description": "Input surface is normalized; voxelization conditions texture. UV unwrap may split/reindex vertices; source placement/scale is not restored."}
        return mesh, normalization

    def execute(self, request, image_bytes, mesh_bytes, stage, progress):
        # Decode/sanity-check user data before importing or allocating the model.
        image = self._image(image_bytes)
        mesh = normalization = None
        if request["kind"] == "mesh_retexture":
            mesh, normalization = self._mesh(mesh_bytes, Path(request["mesh"]["path"]).suffix.lower())
        pipeline, torch = self._pipeline(request, progress)
        params = request["parameters"]
        progress("inference", kind=request["kind"])
        artifacts = []
        if request["kind"] == "image_to_3d":
            import o_voxel
            meshes, latents = pipeline.run(image, num_samples=params["numSamples"], seed=params["seed"],
                sparse_structure_sampler_params=sampler(params["sparseStructure"]), shape_slat_sampler_params=sampler(params["shape"]),
                tex_slat_sampler_params=sampler(params["texture"]), preprocess_image=params["preprocessImage"],
                return_latent=True, pipeline_type=params["pipelineType"], max_num_tokens=params["maxNumTokens"])
            require(len(meshes) == params["numSamples"], "output_count", "Model output count differs from the admitted request.")
            actual_resolution = int(latents[2])
            normalization = {"profile": "upstream-canonical-glTF-v1", "canonicalBounds": [[-0.5] * 3, [0.5] * 3],
                             "preservesInputGeometry": False, "description": "Generated shape; upstream exporter applies its coordinate and UV conversion."}
            for index, generated in enumerate(meshes, 1):
                progress("export", sample=index, samples=len(meshes))
                generated.simplify(16777216)  # Official rasterizer vertex limit.
                settings = params["export"]
                output = o_voxel.postprocess.to_glb(vertices=generated.vertices, faces=generated.faces, attr_volume=generated.attrs,
                    coords=generated.coords, attr_layout=generated.layout, voxel_size=generated.voxel_size,
                    aabb=[[-0.5] * 3, [0.5] * 3], decimation_target=settings["decimationTarget"], texture_size=settings["textureSize"],
                    remesh=settings["remesh"], remesh_band=settings["remeshBand"], remesh_project=settings["remeshProject"],
                    mesh_cluster_threshold_cone_half_angle_rad=math.radians(settings["clusterAngleDegrees"]),
                    mesh_cluster_refine_iterations=settings["clusterRefineIterations"], mesh_cluster_global_iterations=settings["clusterGlobalIterations"],
                    mesh_cluster_smooth_strength=settings["clusterSmoothStrength"], verbose=False, use_tqdm=False)
                name = f"model-{index:03d}.glb"
                output.export(str(stage / name), file_type="glb", extension_webp=settings["webp"])
                artifacts.append({"file": name, "role": "mesh", "mediaType": "model/gltf-binary",
                                  "vertices": len(output.vertices), "triangles": len(output.faces)})
        else:
            actual_resolution = params["resolution"]
            output = pipeline.run(mesh, image, seed=params["seed"], tex_slat_sampler_params=sampler(params["texture"]),
                                  preprocess_image=params["preprocessImage"], resolution=actual_resolution,
                                  texture_size=params["export"]["textureSize"])
            progress("export", sample=1, samples=1)
            output.export(str(stage / "model-001.glb"), file_type="glb", extension_webp=params["export"]["webp"])
            artifacts.append({"file": "model-001.glb", "role": "mesh", "mediaType": "model/gltf-binary",
                              "vertices": len(output.vertices), "triangles": len(output.faces)})
        return {"artifacts": artifacts, "normalization": normalization, "actualResolution": actual_resolution,
                "provenance": {"sourceRevision": SOURCE_REVISION, "modelLockSha256": self.model_lock_sha,
                               "models": {name: {"repository": pin[0], "revision": pin[1]} for name, pin in MODEL_REVISIONS.items()},
                               "torchVersion": torch.__version__, "cudaVersion": torch.version.cuda,
                               "device": torch.cuda.get_device_name(0), "deviceCapability": list(torch.cuda.get_device_capability(0)),
                               "pipelineCacheMax": 1, "residentPipelineKinds": [request["kind"]],
                               "cudaPeakAllocatedBytes": torch.cuda.max_memory_allocated(0),
                               "cudaPeakReservedBytes": torch.cuda.max_memory_reserved(0),
                               "textureLimit": 4096, "alphaMode": "OPAQUE", "materials": "upstream base-color-alpha and metallic-roughness PBR"}}


class Runtime:
    def __init__(self, jobs_root=JOBS, results_root=RESULTS, backend=None):
        self.jobs_root, self.results_root = ordinary(jobs_root), ordinary(results_root)
        self.backend = backend or ModelBackend()

    def load(self, control, emitter):
        return self.backend.load(control["kind"], lambda name, **fields: emitter.send("progress", stage=name, **fields))

    def execute(self, request, emitter):
        inputs = confined(self.jobs_root, self.jobs_root / request["jobId"])
        require(inputs.is_dir(), "job_missing", "Host must create the exact owned job and input files first.")
        image = input_bytes(inputs, request["image"], MAX_IMAGE)
        mesh = input_bytes(inputs, request["mesh"], MAX_MESH) if "mesh" in request else None
        job = confined(self.results_root, self.results_root / request["jobId"])
        try:
            job.mkdir()
            write_new(job / ".runtime-claimed.json", {"schema": "pegasus.trellis2.runtime-claim.v1", "jobId": request["jobId"],
                "effectId": request["effectId"], "requestSha256": hashlib.sha256(json_bytes(request)).hexdigest(), "utc": utc()})
        except FileExistsError as error:
            raise RuntimeFailure("job_already_claimed", "This job already reached the runtime. Reconcile its original result; do not replay.") from error
        stage, final = confined(job, job / "partial"), confined(job, job / "outputs")
        require(not final.exists() and not stage.exists(), "output_exists", "A prior output/partial directory exists; reconcile, do not overwrite.")
        stage.mkdir()
        started = utc()
        details = self.backend.execute(request, image, mesh, stage, lambda name, **fields: emitter.send("progress", stage=name, **fields))
        declared = details.pop("artifacts")
        require(type(declared) is list and 1 <= len(declared) <= 4, "artifact", "Missing or excessive model artifacts.")
        artifacts, seen = [], set()
        for item in declared:
            name = item.pop("file")
            require(type(name) is str and len(relative(name).parts) == 1 and name not in seen, "artifact", "Invalid or repeated output name.")
            require(Path(name).suffix == ".glb" and item.get("role") == "mesh" and item.get("mediaType") == "model/gltf-binary",
                    "artifact", "Only GLB meshes are admitted in the initial model artifact profile.")
            seen.add(name)
            path = confined(stage, stage / name)
            require(path.is_file() and 0 < path.stat().st_size <= MAX_ARTIFACT, "artifact", "Output is missing, empty or exceeds its byte budget.")
            with path.open("r+b") as durable:
                durable.flush()
                os.fsync(durable.fileno())
            if path.suffix == ".glb":
                with path.open("rb") as stream:
                    header = stream.read(12)
                require(len(header) == 12 and header[:8] == b"glTF\x02\x00\x00\x00"
                        and int.from_bytes(header[8:12], "little") == path.stat().st_size, "artifact", "Output is not a complete GLB2 file.")
            artifacts.append({**item, "path": "outputs/" + name, "bytes": path.stat().st_size, "sha256": file_sha(path)})
        require({item.name for item in stage.iterdir()} == seen, "artifact", "Runtime stage contains unreported files.")
        # Detect source changes before artifact admission; the model used the
        # separately hashed byte snapshot, not a mutable filename.
        input_bytes(inputs, request["image"], MAX_IMAGE)
        if "mesh" in request:
            input_bytes(inputs, request["mesh"], MAX_MESH)
        manifest = {"schema": "pegasus.trellis2.artifact-manifest.v1", "jobId": request["jobId"], "effectId": request["effectId"],
                    "kind": request["kind"], "inputs": {key: request[key] for key in ("image", "mesh") if key in request},
                    "parameters": request["parameters"], "startedUtc": started, "finishedUtc": utc(), "artifacts": artifacts, **details}
        write_new(stage / "manifest.json", manifest)
        manifest_artifact = {"role": "manifest", "path": "outputs/manifest.json", "bytes": (stage / "manifest.json").stat().st_size,
                             "sha256": file_sha(stage / "manifest.json"), "mediaType": "application/json"}
        require(not final.exists(), "output_exists", "Final output appeared; no overwrite is permitted.")
        publish_directory(stage, final)
        return {**manifest, "manifestArtifact": manifest_artifact}


def publish_directory(stage, final):
    """Atomically publish without replacing an existing directory, even empty."""
    if os.name == "nt":
        os.rename(stage, final)  # Windows rename refuses an existing destination.
    elif sys.platform.startswith("linux"):
        import ctypes
        import errno
        libc = ctypes.CDLL(None, use_errno=True)
        rename = getattr(libc, "renameat2", None)
        require(rename is not None, "publication_unsupported", "Atomic non-replacing publication is unavailable; preserve partial output.")
        rename.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint)
        rename.restype = ctypes.c_int
        result = rename(-100, os.fsencode(stage), -100, os.fsencode(final), 1)  # AT_FDCWD / RENAME_NOREPLACE
        if result != 0:
            code = ctypes.get_errno()
            if code == errno.EEXIST:
                raise RuntimeFailure("output_exists", "Final output already exists; no replacement is permitted.")
            raise OSError(code, "Atomic non-replacing publication failed")
        for directory in (final, final.parent):
            handle = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(handle)
            finally:
                os.close(handle)
    else:
        raise RuntimeFailure("publication_unsupported", "This runtime supports Linux ARM64; no unsafe publication fallback is enabled.")


def serve(input_stream, output_stream, *, validate_only=False, runtime=None):
    runtime = runtime if runtime is not None else (None if validate_only else Runtime())
    Emitter(output_stream).send("ready", sourceRevision=SOURCE_REVISION, validationOnly=validate_only,
                                kinds=["image_to_3d", "mesh_retexture"], maxTextureSize=4096,
                                maxNumTokens=49152, lowVramModes=[True], latentArtifacts=False,
                                pipelineCacheMax=1,
                                meaning="Protocol ready only; models load lazily and hardware qualification is separate.")
    validation_failed = False
    while True:
        raw = input_stream.readline(MAX_LINE + 1)
        if not raw:
            return 2 if validation_failed else 0
        emitter = Emitter(output_stream)
        request = None
        try:
            require(raw.endswith(b"\n") and len(raw) <= MAX_LINE, "framing", "Each request must be one bounded newline-terminated JSON object.")
            decoded = decode_json(raw)
            if type(decoded) is dict and decoded.get("schema") == CONTROL_SCHEMA:
                if "controlId" in decoded:
                    emitter.identity = {"controlId": identifier(decoded["controlId"])}
                control = control_contract(decoded)
                if validate_only:
                    emitter.send("validated", control=control, executed=False)
                else:
                    loaded = runtime.load(control, emitter)
                    require(loaded.get("modelMaterialized") is True, "load_unconfirmed", "Backend did not confirm actual model materialization.")
                    emitter.send("loaded", result=loaded)
                continue
            if type(decoded) is dict and "jobId" in decoded and "effectId" in decoded:
                emitter.identity = {"jobId": identifier(decoded["jobId"]), "effectId": identifier(decoded["effectId"])}
            request = request_contract(decoded)
            emitter.identity = {"jobId": request["jobId"], "effectId": request["effectId"]}
            if validate_only:
                emitter.send("validated", request=request, executed=False)
            else:
                result = runtime.execute(request, emitter)
                emitter.send("result", outcome="succeeded", result=result)
        except Exception as error:
            validation_failed = validation_failed or validate_only
            # Model/driver exception strings can contain paths or signed URLs.
            # Detailed diagnostics go only to the bounded host-owned stderr log.
            if isinstance(error, RuntimeFailure):
                code, message = error.code, str(error)
            else:
                code, message = "runtime_failure", "Model runtime failed; inspect the owned stderr log and original job before any retry."
                import traceback
                traceback.print_exc(file=sys.stderr)
            published = bool(request and not validate_only and (runtime.results_root / request["jobId"] / "outputs").exists())
            partial = bool(request and not validate_only and ((runtime.results_root / request["jobId"] / "partial").exists() or published))
            emitter.send("error", outcome="unknown" if published else "failed", error={"code": code, "message": message[:2048]}, partial=partial,
                         retryable=False, reconciliationRequired=partial or code in ("runtime_failure", "job_already_claimed"))
            if code == "framing" or partial or code == "runtime_failure":
                # Broken framing or a model/partial-output failure ends this
                # child. A fresh owned process requires host reconciliation.
                return 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stdio", action="store_true", help="Persistent single-request-at-a-time JSON-lines IPC.")
    parser.add_argument("--validate", action="store_true", help="Validate IPC contracts only; no files, model imports or GPU access.")
    args = parser.parse_args()
    if not args.stdio:
        parser.error("--stdio is required; mount paths and model implementations are not command-line options")
    # Reserve a private copy of the actual stdout pipe, then redirect fd1 too.
    # This contains Python AND native-library prints without corrupting IPC.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "wb", buffering=0)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    return serve(sys.stdin.buffer, protocol, validate_only=args.validate)


if __name__ == "__main__":
    raise SystemExit(main())
