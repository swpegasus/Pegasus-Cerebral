"""Closed, dependency-free boundary for the standalone TRELLIS runtime.

The LAN service owns authentication, queueing, capacity and process lifetime.
These checks do not create any of those authorities.
"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import re

REQUEST_SCHEMA = "pegasus.trellis2.runtime-request.v1"
EVENT_SCHEMA = "pegasus.trellis2.runtime-event.v1"
CONTROL_SCHEMA = "pegasus.trellis2.runtime-control.v1"
SOURCE_REVISION = "75fbf0183001ed9876c8dbb35de6b68552ee08bd"
MODEL_REVISIONS = {
    "trellis2": ("microsoft/TRELLIS.2-4B", "af44b45f2e35a493886929c6d786e563ec68364d"),
    "shape_decoder": ("microsoft/TRELLIS-image-large", "25e0d31ffbebe4b5a97464dd851910efc3002d96"),
    "dinov3": ("facebook/dinov3-vitl16-pretrain-lvd1689m", "ea8dc2863c51be0a264bab82070e3e8836b02d51"),
    "rmbg": ("briaai/RMBG-2.0", "2fd7b4b0f2393ddb39a267fb62df6c010928a6c6"),
}
MAX_LINE = 128 * 1024
MAX_IMAGE = 32 * 1024 * 1024
MAX_MESH = 128 * 1024 * 1024
MAX_PIXELS = 64 * 1024 * 1024
MAX_ARTIFACT = 512 * 1024 * 1024


class RuntimeFailure(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def require(condition, code, message):
    if not condition:
        raise RuntimeFailure(code, message)


def object_only(value, allowed, label, required=()):
    require(type(value) is dict and set(value) <= set(allowed) and set(required) <= set(value),
            "contract", f"{label} has missing or unsupported fields.")
    return value


def identifier(value):
    require(type(value) is str and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,95}", value),
            "identity", "Job/effect IDs must be 1..96 safe letters, digits, underscores or hyphens.")
    return value


def integer(value, low, high, label):
    require(type(value) is int and low <= value <= high, "parameter", f"{label} must be an integer in {low}..{high}.")
    return value


def number(value, low, high, label):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            "parameter", f"{label} must be finite and in {low}..{high}.")
    return value


def boolean(value, label):
    require(type(value) is bool, "parameter", f"{label} must be boolean.")
    return value


def relative(value):
    require(type(value) is str and 0 < len(value.encode("utf-8")) <= 512
            and not any(ord(c) < 32 for c in value) and not any(c in value for c in "\\:\x00"),
            "path", "Input paths must be bounded ordinary relative POSIX paths.")
    path = PurePosixPath(value)
    require(not path.is_absolute() and all(part not in ("", ".", "..") for part in value.split("/")),
            "path", "Absolute, empty and parent path components are forbidden.")
    require(path.parts[0] not in ("outputs", "partial", ".runtime-claimed.json"), "path", "Input cannot refer to runtime output.")
    return path


def ordinary(path: Path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        require(not item.is_symlink(), "path", "Linked paths are forbidden.")
        if item.exists():
            info = item.lstat()
            require(not getattr(info, "st_file_attributes", 0) & 0x400, "path", "Reparse paths are forbidden.")
    return path.resolve()


def confined(root: Path, path: Path):
    root, result = ordinary(root), ordinary(path)
    require(result != root and result.is_relative_to(root), "path", "Path leaves its fixed owned root.")
    return result


def input_reference(value):
    value = object_only(value, ("path", "sha256"), "input", ("path", "sha256"))
    relative(value["path"])
    require(type(value["sha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", value["sha256"]),
            "integrity", "Input SHA-256 must be lowercase hexadecimal.")
    return dict(value)


def stage_parameters(value, *, strength, rescale, interval, time):
    value = object_only(value, ("steps", "guidanceStrength", "guidanceRescale", "guidanceInterval", "rescaleT"), "sampling stage")
    bounds = value.get("guidanceInterval", interval)
    require(type(bounds) is list and len(bounds) == 2, "parameter", "guidanceInterval needs two endpoints.")
    bounds = [number(x, 0, 1, "guidanceInterval") for x in bounds]
    require(bounds[0] <= bounds[1], "parameter", "guidanceInterval must be ordered.")
    return {"steps": integer(value.get("steps", 12), 1, 50, "steps"),
            "guidanceStrength": number(value.get("guidanceStrength", strength), 0, 10, "guidanceStrength"),
            "guidanceRescale": number(value.get("guidanceRescale", rescale), 0, 1, "guidanceRescale"),
            "guidanceInterval": bounds, "rescaleT": number(value.get("rescaleT", time), 1, 6, "rescaleT")}


def export_parameters(value, kind):
    fields = {"textureSize", "webp"}
    if kind == "image_to_3d":
        fields |= {"decimationTarget", "remesh", "remeshBand", "remeshProject", "clusterAngleDegrees",
                   "clusterRefineIterations", "clusterGlobalIterations", "clusterSmoothStrength"}
    value = object_only(value, fields, "export")
    texture = integer(value.get("textureSize", 2048), 1024, 4096, "textureSize")
    require(texture in (1024, 2048, 3072, 4096), "parameter", "textureSize must be 1024, 2048, 3072 or 4096; 8K is not admitted.")
    result = {"textureSize": texture, "webp": boolean(value.get("webp", True), "webp")}
    if kind == "image_to_3d":
        result.update(decimationTarget=integer(value.get("decimationTarget", 500000), 100000, 1000000, "decimationTarget"),
                      remesh=boolean(value.get("remesh", True), "remesh"),
                      remeshBand=number(value.get("remeshBand", 1), 0.1, 8, "remeshBand"),
                      remeshProject=number(value.get("remeshProject", 0), 0, 1, "remeshProject"),
                      clusterAngleDegrees=number(value.get("clusterAngleDegrees", 90), 1, 180, "clusterAngleDegrees"),
                      clusterRefineIterations=integer(value.get("clusterRefineIterations", 0), 0, 16, "clusterRefineIterations"),
                      clusterGlobalIterations=integer(value.get("clusterGlobalIterations", 1), 1, 16, "clusterGlobalIterations"),
                      clusterSmoothStrength=number(value.get("clusterSmoothStrength", 1), 0, 10, "clusterSmoothStrength"))
    return result


def parameters(value, kind):
    fields = {"seed", "preprocessImage", "lowVram", "texture", "export"}
    fields |= {"pipelineType", "numSamples", "maxNumTokens", "sparseStructure", "shape", "returnLatent"} if kind == "image_to_3d" else {"resolution"}
    value = object_only(value, fields, "parameters")
    result = {"seed": integer(value.get("seed", 42), 0, 2147483647, "seed"),
              "preprocessImage": boolean(value.get("preprocessImage", True), "preprocessImage"),
              "lowVram": boolean(value.get("lowVram", True), "lowVram"),
              "texture": stage_parameters(value.get("texture", {}), strength=1, rescale=0, interval=[0.6, 0.9], time=3),
              "export": export_parameters(value.get("export", {}), kind)}
    require(result["lowVram"] is True, "unsupported_profile", "The initial runtime profile admits lowVram=true only; false requires a separately qualified successor profile.")
    if kind == "image_to_3d":
        pipeline = value.get("pipelineType", "1024_cascade")
        require(type(pipeline) is str and pipeline in ("512", "1024", "1024_cascade", "1536_cascade"), "parameter", "Unsupported pipelineType.")
        result.update(pipelineType=pipeline, numSamples=integer(value.get("numSamples", 1), 1, 4, "numSamples"),
                      maxNumTokens=integer(value.get("maxNumTokens", 49152), 1, 49152, "maxNumTokens"),
                      returnLatent=boolean(value.get("returnLatent", False), "returnLatent"),
                      sparseStructure=stage_parameters(value.get("sparseStructure", {}), strength=7.5, rescale=0.7, interval=[0.6, 1], time=5),
                      shape=stage_parameters(value.get("shape", {}), strength=7.5, rescale=0.5, interval=[0.6, 1], time=3))
        require(result["returnLatent"] is False, "unsupported_profile", "Latent artifacts are not admitted by the initial public artifact profile.")
    else:
        resolution = value.get("resolution", 1024)
        require(type(resolution) is int and resolution in (512, 1024, 1536), "parameter", "Retexture resolution must be 512, 1024 or 1536.")
        result["resolution"] = resolution
    return result


def request_contract(value):
    object_only(value, ("schema", "jobId", "effectId", "kind", "image", "mesh", "parameters"), "request",
                ("schema", "jobId", "effectId", "kind", "image"))
    require(value["schema"] == REQUEST_SCHEMA, "schema", "Unsupported runtime request version.")
    kind = value["kind"]
    require(type(kind) is str and kind in ("image_to_3d", "mesh_retexture"), "kind", "Unsupported model operation.")
    require(("mesh" in value) == (kind == "mesh_retexture"), "contract", "Exactly mesh_retexture requires a mesh input.")
    result = {"schema": REQUEST_SCHEMA, "jobId": identifier(value["jobId"]), "effectId": identifier(value["effectId"]),
              "kind": kind, "image": input_reference(value["image"]), "parameters": parameters(value.get("parameters", {}), kind)}
    if kind == "mesh_retexture":
        result["mesh"] = input_reference(value["mesh"])
    return result


def control_contract(value):
    """Host-only materialization control: no job, image, mount or model selection."""
    object_only(value, ("schema", "action", "controlId", "kind", "lowVram"), "load control",
                ("schema", "action", "controlId", "kind", "lowVram"))
    require(value["schema"] == CONTROL_SCHEMA and value["action"] == "load", "schema", "Unsupported runtime control.")
    kind = value["kind"]
    require(type(kind) is str and kind in ("image_to_3d", "mesh_retexture"), "kind", "Unsupported materialization operation.")
    require(boolean(value["lowVram"], "lowVram") is True, "unsupported_profile", "Initial load control requires lowVram=true.")
    return {"schema": CONTROL_SCHEMA, "action": "load", "controlId": identifier(value["controlId"]), "kind": kind, "lowVram": True}


def _unique(pairs):
    value = {}
    for key, child in pairs:
        require(key not in value, "json", "Duplicate JSON properties are forbidden.")
        value[key] = child
    return value


def decode_json(data, maximum=MAX_LINE):
    require(type(data) is bytes and len(data) <= maximum, "budget", "JSON exceeds the protocol byte budget.")
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=_unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(RuntimeFailure("json", "Non-finite JSON is forbidden.")))
    except (UnicodeError, ValueError, RecursionError) as error:
        raise RuntimeFailure("json", "Invalid bounded UTF-8 JSON.") from error


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n").encode("utf-8")


def write_new(path, value):
    with Path(path).open("xb") as stream:
        stream.write(json_bytes(value))
        stream.flush()
        import os
        os.fsync(stream.fileno())


def file_sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def input_bytes(root, reference, maximum):
    path = confined(root, Path(root).joinpath(*relative(reference["path"]).parts))
    require(path.is_file() and path.stat().st_size <= maximum, "input", "Input is missing or exceeds its byte budget.")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    require(len(data) <= maximum and hashlib.sha256(data).hexdigest() == reference["sha256"], "integrity", "Input byte snapshot differs from its SHA-256.")
    return data
