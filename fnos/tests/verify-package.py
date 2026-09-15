#!/usr/bin/env python3
"""Verify online/offline RemoteCI FPK structure and embedded docker image metadata."""

from __future__ import annotations

import hashlib
import io
import json
import pathlib
import sys
import tarfile


def read_member(archive: tarfile.TarFile, name: str) -> bytes:
    member = archive.extractfile(name)
    if member is None:
        raise AssertionError(f"missing archive member: {name}")
    return member.read()


def parse_metadata(content: bytes) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in content.decode("utf-8").splitlines():
        key, separator, value = line.partition("=")
        if not separator or not key or key in values:
            raise AssertionError(f"invalid image metadata line: {line!r}")
        values[key] = value
    return values


def main() -> None:
    if len(sys.argv) != 5:
        raise SystemExit("usage: verify-package.py <package.fpk> <version> <online|offline> <all|amd64|arm64>")
    package_path = pathlib.Path(sys.argv[1])
    version, mode, expected_arch = sys.argv[2:]
    expected_platform = {"all": "all", "amd64": "x86", "arm64": "arm"}[expected_arch]
    expected_tag = f"ghcr.io/edge-hh/remoteci:{version}"

    with tarfile.open(package_path, "r:*") as package:
        package_names = package.getnames()
        manifest_text = read_member(package, "manifest").decode("utf-8")
        manifest = {
            key.strip(): value.strip()
            for line in manifest_text.splitlines()
            if (separator := line.partition("="))[1]
            for key, _, value in [separator]
        }
        assert manifest["appname"] == "remoteci"
        assert manifest["version"] == version
        assert manifest["platform"] == expected_platform
        assert "cmd/arch_compat" in package_names
        privilege = json.loads(read_member(package, "config/privilege"))
        assert privilege["defaults"]["run-as"] == "root"
        app_tgz = read_member(package, "app.tgz")
        outer_metadata = read_member(package, "cmd/offline-image.env") if mode == "offline" else None

    with tarfile.open(fileobj=io.BytesIO(app_tgz), mode="r:gz") as app:
        compose = read_member(app, "docker/docker-compose.yaml").decode("utf-8")
        assert f"image: {expected_tag}" in compose
        if mode == "online":
            assert "pull_policy:" not in compose
            assert "docker/remoteci-image.tar.gz" not in app.getnames()
            assert "docker/remoteci-image.env" not in app.getnames()
            assert "cmd/offline-image.env" not in package_names
            assert outer_metadata is None
            return

        assert "pull_policy: never" in compose
        archive_bytes = read_member(app, "docker/remoteci-image.tar.gz")
        app_metadata = read_member(app, "docker/remoteci-image.env")
        assert app_metadata == outer_metadata

    metadata = parse_metadata(app_metadata)
    assert metadata == {
        "IMAGE_TAG": expected_tag,
        "IMAGE_ARCH": expected_arch,
        "IMAGE_ID": metadata["IMAGE_ID"],
        "IMAGE_ARCHIVE_SHA256": metadata["IMAGE_ARCHIVE_SHA256"],
    }
    assert hashlib.sha256(archive_bytes).hexdigest() == metadata["IMAGE_ARCHIVE_SHA256"]

    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:*") as image:
        docker_manifest = json.loads(read_member(image, "manifest.json"))
        matches = [entry for entry in docker_manifest if expected_tag in (entry.get("RepoTags") or [])]
        assert len(matches) == 1
        config_path = matches[0]["Config"]
        config = json.loads(read_member(image, config_path))
        assert config["architecture"] == expected_arch
        config_digest = pathlib.PurePosixPath(config_path).name.removesuffix(".json")
        assert metadata["IMAGE_ID"] == f"sha256:{config_digest}"


if __name__ == "__main__":
    main()
