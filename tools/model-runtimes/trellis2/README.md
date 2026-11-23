# Standalone TRELLIS.2 model runtime

This is the model-execution child for the private LAN API. It contains no HTTP
server, credentials, queue, idle timer or Cerebral engine/plugin registration.
The owning host controls the exact container identity, capacity, process lifetime,
authentication, durable job state and cancellation. Codex/Cerebral can be clients
of that standalone service without loading this model locally.

Current evidence is **26 stdlib unit/CLI fixtures with mocked upstream APIs**.
No model, GPU, native dependency build, Docker image, remote worker or inference
was exercised. The ARM64 build profile remains **qualification required**.

## IPC and mounts

The persistent entrypoint is
`python3 -I -B /opt/trellis/runtime/runner.py --stdio`. Each stdin line is one
UTF-8 JSON object, at most 128 KiB including its newline. Requests execute
serially. A host batch is a sequence of distinct job/effect requests; a request's
`numSamples` is output count for one image, not a queue of different images.

Fixed mounts, supplied by the host for this runtime instance:

| Container path | Access | Contract |
|---|---|---|
| `/data/jobs` | read-only | `<jobId>/<input-relative-path>`; never written by the runner |
| `/data/results` | read/write | Runner exclusively creates `<jobId>` with claim, partial and final output |
| `/models` | read-only | Verified model data and `model-lock.json` |
| `/cache` | read/write | Instance-owned compiled Torch/Triton caches |
| `/tmp` | writable tmpfs | Projected local model configs and temporary library work |

The input/result parents are instance-specific. Do not expose the global Worker
data directory or mount other applications' model caches writable. The image
source/runtime are immutable. Use the unprivileged image user `65532:65532`;
prepare owned output/cache/tmpfs permissions in the host. No Docker socket,
host network, privileged flag or host credential file is required.

Request schema: `pegasus.trellis2.runtime-request.v1`. Exact top-level fields:

- `schema`, `jobId`, `effectId`, `kind`, `image`; optional `parameters`.
- Exactly `mesh_retexture` also requires `mesh`.
- `kind`: `image_to_3d` or `mesh_retexture`.
- IDs: 1..96 ASCII letters/digits/underscores/hyphens, first character alphanumeric.
- `image` and `mesh`: exactly `{ "path": "relative/name", "sha256": "lowercase SHA-256" }`.
  Paths are relative to the exact input job. Absolute/traversing/linked paths
  are rejected. Input images: PNG/JPEG/WebP, 32 MiB, one frame, at most 64 Mi pixels.
  Retexture geometry: self-contained GLB/PLY/OBJ, 128 MiB, at most 2 million
  vertices/4 million triangles. No external resource resolver is provided.

`request.example.jsonl` is a validation example with a deliberately nonfunctional
zero digest; replace it with the actual staged image digest before execution.

## Parameter mapping

Unknown fields and non-finite values fail. Booleans cannot substitute for integers.
The service's public boundary must validate independently; model availability or
free GPU memory never creates job/capacity authority.

| Common parameter | Default | Initial runtime bound |
|---|---|---|
| `seed` | 42 | integer 0..2147483647; host resolves any randomized seed before IPC |
| `preprocessImage` | true | boolean |
| `lowVram` | true | **true only**; false needs a separately qualified successor profile |
| `texture` | model texture defaults | typed sampler object below |
| `export.textureSize` | 2048 | 1024, 2048, 3072 or 4096; **8K rejects** |
| `export.webp` | true | boolean; upstream GLB WebP extension selection |

Image-to-3D-only parameters:

| Parameter | Default | Bound |
|---|---|---|
| `pipelineType` | `1024_cascade` | `512`, `1024`, `1024_cascade`, `1536_cascade` |
| `numSamples` | 1 | integer 1..4; every returned model is exported |
| `maxNumTokens` | 49152 | integer 1..49152 |
| `sparseStructure`, `shape` | model defaults | typed sampler objects |
| `returnLatent` | false | **false only**; public latent artifacts are not admitted |

Sampler objects contain only `steps` (1..50), `guidanceStrength` (0..10),
`guidanceRescale` (0..1), `guidanceInterval` (two ordered endpoints in 0..1),
and `rescaleT` (1..6). Default values:

| Stage | Steps | Strength | Rescale | Interval | Rescale T |
|---|---:|---:|---:|---|---:|
| sparse structure | 12 | 7.5 | 0.7 | [0.6, 1] | 5 |
| shape | 12 | 7.5 | 0.5 | [0.6, 1] | 3 |
| texture | 12 | 1 | 0 | [0.6, 0.9] | 3 |

Image export also accepts `decimationTarget` (default500000, 100000..1000000),
`remesh` (true), `remeshBand` (1, 0.1..8), `remeshProject` (0, 0..1),
`clusterAngleDegrees` (90, 1..180), `clusterRefineIterations` (0, 0..16),
`clusterGlobalIterations` (1, 1..16), `clusterSmoothStrength` (1, 0..10).
These are explicit service bounds, not a claim that every combination has passed
GB10 qualification. Geometry resolution and texture resolution are separate.

Retexture accepts `resolution` (default1024; 512/1024/1536), common controls,
and texture sampling. Image-generation sampling/mesh-remeshing export controls
are rejected, not silently ignored. Upstream retexturing normalizes the input
surface, discards old UVs/materials and creates new UVs/PBR textures. Voxelization
conditions texture; it does not regenerate the input shape. Original scale,
translation, UV layout, vertex/index identity and normals are **not preserved**.
The manifest records input center, scale and this limitation.

## Events, results and failure handling

Stdout is reserved for `pegasus.trellis2.runtime-event.v1` NDJSON. Native/Python
library stdout is redirected at the file-descriptor level to stderr, which the
host must bound and retain as a private job log. Each event has `event`,
`sequence` (starts at0 per request), UTC and valid job/effect IDs when available.

- `ready`: protocol only, **not** model-loaded or hardware-qualified evidence.
- `loaded`: host-only load control has constructed the pinned pipeline, verified
  model data and executed a tiny CUDA allocation/synchronization. This is model
  materialization, not full inference or native-export qualification.
- `progress`: coarse `stage` such as verifying/loading/inference/export, with
  sample counts for export. No invented sampler percentage is reported.
- `validated`: `--stdio --validate` only; contains normalized request and
  `executed=false`. It imports no model/Torch and performs no filesystem work.
- `result`: `outcome=succeeded`, `result` contains manifest/provenance and artifacts.
- `error`: code/message, `partial`, `retryable=false`, `reconciliationRequired`.
  If publication happened but confirmation/durability failed, outcome is `unknown`;
  do not turn it into a clean failure followed by automatic replay.

The runner reads and hashes actual input byte snapshots. Its result-job directory
and claim are create-only. A prior claim refuses replay without executing again.
Complete files are staged in `partial`, checked, hashed and published by renaming
to `outputs` only after input identity is rechecked. Unexpected or missing files
fail. A partial/model error emits failure and ends this child; the host reconciles
the original job and exact CID before starting another owned process.

Artifacts are job-result-relative, for example `outputs/model-001.glb`, each with
`role`, `mediaType`, `bytes`, `sha256` and mesh counts. At most four GLB models are
returned; no implicit `outputs[0]` truncation occurs. An additional JSON manifest
is described separately as `manifestArtifact`, avoiding a self-hash cycle.
GLB framing/length is checked here; complete material/geometry admission and
visual acceptance remain separate client/product responsibilities.

The manifest records both normalized parameters and actual cascade resolution,
source/model pins, model-lock hash, runtime Torch/CUDA/device identity and
normalization semantics. The upstream exporter initially uses opaque materials
with base-color/alpha and metallic–roughness maps. A successful texture export is
not gameplay/content admission or proof of reconstruction fidelity.

The initial profile retains at most **one pipeline kind**. Pipelines load lazily;
switching kinds drops previous pipeline references before loading the next stack.
`preparing-pipeline` reports that transition. Manifest CUDA allocator high-water
values describe the owned process, not total physical/system memory. Alternating
kinds and actual resident high-water require GB10 qualification. This module has no idle or
unload API. The host must stop and verify exit of its **exact owned container** to
unload, then observe resource state. Torch `empty_cache()` is not model unload.

The trusted host can materialize before accepting jobs by sending exactly
`{"schema":"pegasus.trellis2.runtime-control.v1","action":"load","controlId":"host-issued-id","kind":"image_to_3d","lowVram":true}`.
Responses carry `controlId` and per-control sequence, without job/effect IDs.
No image, filesystem root, model location, credentials or arbitrary operation is
accepted by this closed control. `--validate` reports `validated`, never `loaded`.

## Image build and model provisioning

`sources.lock.json` pins Microsoft and every native-source repository to exact
commits. Its default candidate is **unqualified**, not a tested compatible stack.
`requirements.candidate.txt` pins direct packages but is not a complete wheel lock.
`Dockerfile` intentionally cannot build from the source tree alone. After the
actual GB10/ARM64/driver probe, the installer supplies `build-inputs/` containing:

1. `profile.json`, filled from `build-profile.example.json`: exact NVIDIA/PyTorch
   base **digest**, Python/Torch versions, source-lock hash and wheel-lock hash.
2. `python-wheels.lock`: complete transitive lock with SHA-256 hashes for the
   chosen Python/ARM64/base combination, plus its offline `wheelhouse/`.
3. A base containing CUDA13+ devel/Torch, compilers, Git, CMake, Ninja and required
   native image/EGL development libraries. No global host install is performed.

BuildKit fetches exact public Git commits/submodules only at image build.
Native builds force sm121, no nightly Torch, no unpinned Python dependency
resolution and no opportunistic FlashAttention prebuilt-wheel download.
The completed image still says `built-unqualified`; native imports/kernel probes,
real image/retexture/export, cancellation, restart recovery and unload must pass
on the actual GB10 before host package admission. No Docker build was run here.

Provision models separately into fixed `trellis2`, `shape_decoder`, `dinov3`, and
`rmbg` subdirectories below `/models`. The example `model-lock` must be completed
with every file's actual bytes/SHA-256; empty inventories refuse. Only admitted
JSON/text/safetensors data are permitted, no pickle/Python/native model files.
Resolve dependencies at the pinned revisions and review their access/license
requirements. Runtime validates each inventory/hash once per process and projects
configs to local data roots. HF/Transformers offline mode is set before imports;
the host must also supply container network isolation. No model download occurs.

`runtime-package.example.json` describes this lane's required mounts/entrypoint;
it is **not** an admitted host-package schema or deployment receipt.

Run focused fixtures with `python -I -B -m unittest discover -s tests -p test_runner.py`.
Run contract-only CLI checks by piping request NDJSON to
`python -I -B runner.py --stdio --validate` (invalid controls exit2).
