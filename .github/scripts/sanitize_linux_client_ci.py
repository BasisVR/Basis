#!/usr/bin/env python3
from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path


PACKAGE_DIRS_TO_REMOVE = (
    "Packages/com.basis.openxr",
    "Packages/com.basis.webxr",
    "Packages/com.meta.xr.sdk.core",
)

PACKAGE_DEPENDENCIES_TO_REMOVE = (
    "com.unity.xr.openxr",
    "com.valvesoftware.openxr.utils",
)

PACKAGE_LOCK_ENTRIES_TO_REMOVE = PACKAGE_DEPENDENCIES_TO_REMOVE + (
    "com.basis.openxr",
    "com.basis.webxr",
)


def remove_manifest_dependencies(project_root: Path) -> list[str]:
    manifest_path = project_root / "Packages/manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    dependencies = manifest.get("dependencies", {})
    removed: list[str] = []

    for dependency in PACKAGE_DEPENDENCIES_TO_REMOVE:
        if dependencies.pop(dependency, None) is not None:
            removed.append(dependency)

    if removed:
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    return removed


def remove_package_lock_entries(project_root: Path) -> list[str]:
    lock_path = project_root / "Packages/packages-lock.json"
    if not lock_path.exists():
        return []

    lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
    dependencies = lock_data.get("dependencies", {})
    removed: list[str] = []

    for dependency in PACKAGE_LOCK_ENTRIES_TO_REMOVE:
        if dependencies.pop(dependency, None) is not None:
            removed.append(dependency)

    for package_info in dependencies.values():
        package_dependencies = package_info.get("dependencies")
        if not isinstance(package_dependencies, dict):
            continue

        for dependency in PACKAGE_LOCK_ENTRIES_TO_REMOVE:
            package_dependencies.pop(dependency, None)

    if removed:
        lock_path.write_text(json.dumps(lock_data, indent=2) + "\n", encoding="utf-8")

    return removed


def remove_package_dirs(project_root: Path) -> list[Path]:
    removed: list[Path] = []

    for relative_dir in PACKAGE_DIRS_TO_REMOVE:
        package_dir = project_root / relative_dir
        if package_dir.exists():
            shutil.rmtree(package_dir)
            removed.append(package_dir)
        else:
            print(f"Package directory already absent: {package_dir}")

    return removed


def main() -> int:
    project_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Basis")
    project_root = project_root.resolve()

    if not project_root.exists():
        print(f"Project root not found: {project_root}", file=sys.stderr)
        return 1

    removed_manifest_dependencies = remove_manifest_dependencies(project_root)
    for dependency in removed_manifest_dependencies:
        print(f"Removed manifest dependency: {dependency}")

    removed_lock_entries = remove_package_lock_entries(project_root)
    for dependency in removed_lock_entries:
        print(f"Removed package lock entry: {dependency}")

    removed_package_dirs = remove_package_dirs(project_root)
    for package_dir in removed_package_dirs:
        print(f"Removed package directory: {package_dir}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
