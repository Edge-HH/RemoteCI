#!/usr/bin/env python3
"""Create a minimal docker-save-compatible archive for FPK packaging tests."""

from __future__ import annotations

import gzip
import hashlib
import io
import json
import pathlib
import sys
import tarfile


def json_bytes(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":"), sort_keys=True).encode("utf-8")


def add_bytes(archive: tarfile.TarFile, name: str, content: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(content)
    info.mode = 0o644
    info.mtime = 0
    archive.addfile(info, io.BytesIO(content))


def main() -> None:
    if len(sys.argv) != 4:
        raise SystemExit("usage: create-fixture-image.py <output.tar.gz> <tag> <amd64|arm64>")
    output = pathlib.Path(sys.argv[1])
    tag = sys.argv[2]
    architecture = sys.argv[3]
    if architecture not in {"amd64", "arm64"}:
        raise SystemExit(f"unsupported fixture architecture: {architecture}")

    config = json_bytes(
        {
            "architecture": architecture,
            "config": {},
            "created": "1970-01-01T00:00:00Z",
            "os": "linux",
            "rootfs": {"diff_ids": [], "type": "layers"},
        }
    )
    config_digest = hashlib.sha256(config).hexdigest()
    manifest = json_bytes([{"Config": f"{config_digest}.json", "Layers": [], "RepoTags": [tag]}])

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=1, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w|") as archive:
                add_bytes(archive, f"{config_digest}.json", config)
                add_bytes(archive, "manifest.json", manifest)


if __name__ == "__main__":
    main()
