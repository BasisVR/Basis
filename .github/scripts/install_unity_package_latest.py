#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path


UNITY_REGISTRY_URL = "https://packages.unity.com"


def fetch_latest_version(package_name: str) -> str:
    url = f"{UNITY_REGISTRY_URL}/{package_name}"
    with urllib.request.urlopen(url, timeout=30) as response:
        package_info = json.load(response)

    latest = package_info.get("dist-tags", {}).get("latest")
    if not latest:
        raise RuntimeError(f"Package {package_name} did not expose dist-tags.latest at {url}")

    return latest


def update_manifest_dependency(manifest_path: Path, package_name: str, version: str) -> None:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    dependencies = manifest.setdefault("dependencies", {})
    previous_version = dependencies.get(package_name)
    dependencies[package_name] = version
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    if previous_version is None:
        print(f"Added {package_name}@{version} to {manifest_path}")
    else:
        print(f"Updated {package_name}: {previous_version} -> {version} in {manifest_path}")


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: install_unity_package_latest.py <project_root> <package_name>", file=sys.stderr)
        return 1

    project_root = Path(sys.argv[1]).resolve()
    package_name = sys.argv[2].strip()
    manifest_path = project_root / "Packages" / "manifest.json"

    if not manifest_path.exists():
        print(f"Manifest not found: {manifest_path}", file=sys.stderr)
        return 1

    latest_version = fetch_latest_version(package_name)
    print(f"Resolved latest {package_name} version: {latest_version}")
    update_manifest_dependency(manifest_path, package_name, latest_version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
