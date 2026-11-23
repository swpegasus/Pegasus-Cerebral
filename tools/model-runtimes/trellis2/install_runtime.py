"""Image-build helper only. Never invoked by the model IPC runner.

Refuses placeholder profiles. Public source fetches occur only at image build;
Python packages come from an exact hashed offline wheelhouse, models separately.
"""
from pathlib import Path
import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from contracts import SOURCE_REVISION, file_sha, ordinary, require


def validate_wheel_lock(path):
    """A platform-resolved lock, never a pip configuration or direct-reference file."""
    require(path.stat().st_size <= 2 * 1024 * 1024, "wheel_lock", "Wheel lock exceeds its byte budget.")
    logical, pending = [], ""
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        continued = line.endswith("\\")
        pending += (line[:-1].rstrip() if continued else line) + " "
        if not continued:
            logical.append(pending.strip())
            pending = ""
    require(not pending and logical, "wheel_lock", "Wheel lock is empty or has an unfinished continuation.")
    pattern = re.compile(r"([A-Za-z0-9][A-Za-z0-9_.-]*)==([0-9][A-Za-z0-9_.+!-]*)(?:\s+--hash=sha256:[0-9a-f]{64})+")
    names = set()
    for line in logical:
        match = pattern.fullmatch(line)
        require(match is not None, "wheel_lock", "Only exact name==version requirements plus SHA-256 hashes are admitted; URLs, options, includes and direct references are forbidden.")
        name = re.sub(r"[-_.]+", "-", match[1].lower())
        require(name not in names, "wheel_lock", "Duplicate package entries are forbidden.")
        names.add(name)


def validate_profile(profile, *, base_image, target_arch, lock_path, wheel_lock):
    require(type(profile) is dict and profile.get("schema") == "pegasus.trellis2.build-profile.v1"
            and profile.get("targetPlatform") == "linux/arm64" and profile.get("cudaArchitecture") == "12.1",
            "build_profile", "Expected an explicit linux/arm64 sm121 profile.")
    require(type(base_image) is str and re.fullmatch(r"[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}", base_image)
            and profile.get("baseImage") == base_image, "build_profile", "Base image must be the exact probed NVIDIA/PyTorch digest, never a tag or placeholder.")
    require(target_arch == "arm64" and platform.machine().lower() in ("aarch64", "arm64"),
            "architecture", "This build profile must execute natively on ARM64.")
    require(profile.get("sourceLockSha256") == file_sha(lock_path)
            and profile.get("pythonWheelLockSha256") == file_sha(wheel_lock), "build_integrity", "Source or wheel lock differs from the admitted build profile.")
    require(type(profile.get("pythonVersion")) is str and profile["pythonVersion"] == platform.python_version()
            and type(profile.get("torchVersion")) is str and profile["torchVersion"], "build_profile", "Python/Torch identities must come from the real base probe.")
    require(profile.get("qualificationRequired") is True and profile.get("qualified") is False,
            "build_profile", "A build is not hardware qualification; preserve the explicit pending state.")
    validate_wheel_lock(wheel_lock)


def run(argv, **kwargs):
    subprocess.run(argv, check=True, stdin=subprocess.DEVNULL, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-image", required=True)
    parser.add_argument("--target-arch", required=True)
    args = parser.parse_args()
    inputs = HERE / "build-inputs"
    profile = json.loads((inputs / "profile.json").read_text(encoding="utf-8"))
    lock_path, wheel_lock = HERE / "sources.lock.json", inputs / "python-wheels.lock"
    validate_profile(profile, base_image=args.base_image, target_arch=args.target_arch, lock_path=lock_path, wheel_lock=wheel_lock)
    for executable in ("git", "cmake", "ninja", "c++", "nvcc"):
        require(shutil.which(executable), "build_tool", f"Pinned base lacks required build tool: {executable}")
    import torch
    require(torch.__version__ == profile["torchVersion"] and torch.version.cuda is not None
            and int(torch.version.cuda.split(".")[0]) >= 13, "build_torch", "Probed Torch/CUDA identity differs or predates the sm121 profile.")
    wheelhouse = ordinary(inputs / "wheelhouse")
    require(wheelhouse.is_dir() and wheelhouse == (inputs / "wheelhouse").absolute(), "wheelhouse", "Expected the fixed, ordinary installer wheelhouse directory.")
    wheels = list(wheelhouse.iterdir())
    require(0 < len(wheels) <= 512 and all(ordinary(item) == item.absolute() and item.is_file() and item.suffix == ".whl" for item in wheels),
            "wheelhouse", "Wheelhouse must contain only bounded ordinary wheel files.")
    run([sys.executable, "-m", "pip", "install", "--no-index", "--no-deps", "--require-hashes", "--only-binary=:all:",
         "--find-links", str(wheelhouse), "-r", str(wheel_lock)])
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    require(lock["schema"] == "pegasus.trellis2.source-lock.v1" and len(lock["sources"]) == 7,
            "source_lock", "Unexpected native source inventory.")
    environment = dict(os.environ, GIT_TERMINAL_PROMPT="0", GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null",
                       TORCH_CUDA_ARCH_LIST="12.1", MAX_JOBS="2", PIP_NO_INDEX="1", FLASH_ATTENTION_FORCE_BUILD="TRUE")
    destinations = {}
    for item in lock["sources"]:
        require(re.fullmatch(r"[a-z0-9_]+", item["name"]) and re.fullmatch(r"[0-9a-f]{40}", item["revision"])
                and re.fullmatch(r"https://github.com/[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\.git", item["repository"]),
                "source_lock", "Source lock requires fixed public GitHub repository/commit coordinates.")
        destination = Path("/opt/trellis/source") if item["name"] == "trellis2" else Path("/opt/trellis/native") / item["name"]
        destination.mkdir(parents=True, exist_ok=False)
        git = ["git", "-c", "protocol.file.allow=never", "-C", str(destination)]
        run([*git, "init"], env=environment)
        run([*git, "remote", "add", "origin", item["repository"]], env=environment)
        run([*git, "fetch", "--depth", "1", "origin", item["revision"]], env=environment)
        run([*git, "checkout", "--detach", "FETCH_HEAD"], env=environment)
        actual = subprocess.check_output([*git, "rev-parse", "HEAD"], env=environment, text=True).strip()
        require(actual == item["revision"], "source_integrity", "Fetched source commit differs.")
        run([*git, "submodule", "update", "--init", "--recursive", "--depth", "1"], env=environment)
        destinations[item["name"]] = destination
    require(lock["sources"][0]["revision"] == SOURCE_REVISION, "source_revision", "Runtime/source pin mismatch.")
    for name in ("utils3d", "flash_attention", "nvdiffrast", "nvdiffrec", "cumesh", "flexgemm"):
        run([sys.executable, "-m", "pip", "install", "--no-index", "--no-deps", "--no-build-isolation", str(destinations[name])], env=environment)
    run([sys.executable, "-m", "pip", "install", "--no-index", "--no-deps", "--no-build-isolation", str(destinations["trellis2"] / "o-voxel")], env=environment)
    run([sys.executable, "-m", "pip", "check"])
    (destinations["trellis2"] / ".source-revision").write_text(SOURCE_REVISION + "\n", encoding="ascii")
    Path("/opt/trellis/build-profile.json").write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"state": "built-unqualified", "hardwareQualificationRequired": True, "modelProvisioned": False}))


if __name__ == "__main__":
    main()
