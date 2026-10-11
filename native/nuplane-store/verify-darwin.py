#!/usr/bin/env python3
"""Write or verify provenance for Nuplane's universal Darwin shim."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import struct
import sys
import tempfile


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def architectures(path: pathlib.Path) -> list[str]:
    data = path.read_bytes()
    if len(data) < 8:
        raise ValueError("the native library is too short to be a universal Mach-O image")

    magic_be = struct.unpack_from(">I", data, 0)[0]
    if magic_be in (0xCAFEBABE, 0xCAFEBABF):
        endian = ">"
        is_64 = magic_be == 0xCAFEBABF
    elif magic_be in (0xBEBAFECA, 0xBFBAFECA):
        endian = "<"
        is_64 = magic_be == 0xBFBAFECA
    else:
        raise ValueError("the native library is not a universal Mach-O image")

    count = struct.unpack_from(endian + "I", data, 4)[0]
    entry_format = endian + ("iiQQII" if is_64 else "iiIII")
    entry_size = struct.calcsize(entry_format)
    if count > 32 or 8 + count * entry_size > len(data):
        raise ValueError("the universal Mach-O architecture table is invalid")

    names = set()
    for index in range(count):
        cpu_type = struct.unpack_from(entry_format, data, 8 + index * entry_size)[0]
        if cpu_type == 0x0100000C:
            names.add("arm64")
        elif cpu_type == 0x01000007:
            names.add("x86_64")
        else:
            names.add(f"cpu-{cpu_type}")
    return sorted(names)


def fingerprint(source_path: pathlib.Path, script_path: pathlib.Path) -> None:
    print(f"{sha256(source_path)} {sha256(script_path)}")


def write_manifest(
    source_path: pathlib.Path,
    script_path: pathlib.Path,
    verifier_path: pathlib.Path,
    library_path: pathlib.Path,
    manifest_path: pathlib.Path,
    expected_source_hash: str,
    expected_script_hash: str,
) -> None:
    source_hash = sha256(source_path)
    script_hash = sha256(script_path)
    verifier_hash = sha256(verifier_path)
    if source_hash != expected_source_hash or script_hash != expected_script_hash:
        raise ValueError("native source or build script changed while the library was being compiled")

    archs = architectures(library_path)
    if archs != ["arm64", "x86_64"]:
        raise ValueError(f"built universal library has unexpected architectures: {archs!r}")

    manifest = {
        "schemaVersion": 1,
        "libraryFile": library_path.name,
        "architectures": archs,
        "sourceFile": source_path.name,
        "sourceSha256": source_hash,
        "buildScriptFile": script_path.name,
        "buildScriptSha256": script_hash,
        "verifierFile": verifier_path.name,
        "verifierSha256": verifier_hash,
        "binarySha256": sha256(library_path),
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{manifest_path.name}.", dir=manifest_path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(manifest, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, manifest_path)
    finally:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass


def verify_manifest(
    source_path: pathlib.Path,
    script_path: pathlib.Path,
    verifier_path: pathlib.Path,
    library_path: pathlib.Path,
    manifest_path: pathlib.Path,
) -> None:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected = {
        "schemaVersion": 1,
        "libraryFile": "libnuplane_store_native.dylib",
        "architectures": ["arm64", "x86_64"],
        "sourceFile": "openat-create.c",
        "buildScriptFile": "build-darwin.sh",
        "verifierFile": "verify-darwin.py",
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise ValueError(f"manifest field {key!r} does not match the expected value")

    hashes = {
        "sourceSha256": sha256(source_path),
        "buildScriptSha256": sha256(script_path),
        "verifierSha256": sha256(verifier_path),
        "binarySha256": sha256(library_path),
    }
    for key, value in hashes.items():
        if manifest.get(key) != value:
            raise ValueError(f"manifest field {key!r} does not match the current file bytes")

    actual_architectures = architectures(library_path)
    if actual_architectures != expected["architectures"]:
        raise ValueError(f"universal library architectures are {actual_architectures!r}")

    print(f"Verified {library_path.name}: arm64,x86_64; source, script, verifier, and binary SHA-256 match.")


def main(arguments: list[str]) -> int:
    if not arguments:
        raise ValueError("expected fingerprint, write, or verify mode")
    mode, *values = arguments
    if mode == "fingerprint" and len(values) == 2:
        fingerprint(pathlib.Path(values[0]), pathlib.Path(values[1]))
    elif mode == "write" and len(values) == 7:
        write_manifest(
            *(pathlib.Path(value) for value in values[:5]),
            values[5],
            values[6],
        )
    elif mode == "verify" and len(values) == 5:
        verify_manifest(*(pathlib.Path(value) for value in values))
    else:
        raise ValueError("invalid arguments")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (OSError, ValueError, json.JSONDecodeError) as exception:
        print(f"Native shim provenance check failed: {exception}", file=sys.stderr)
        raise SystemExit(1)
