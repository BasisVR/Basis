#!/usr/bin/env python3
from __future__ import annotations

import shutil
import sys
from pathlib import Path


PACKAGE_DIRS_TO_REMOVE = (
    "Packages/com.valvesoftware.unity.openvr",
    "Packages/com.basis.openvr",
    "Packages/com.basis.openxr",
    "Packages/com.llealloo.audiolink",
    "Packages/com.basis.examples",
    "Packages/com.basis.pooltable",
)

PACKAGE_DEPENDENCIES_TO_REMOVE = (
    "com.llealloo.audiolink",
    "com.unity.xr.openxr",
    "com.valvesoftware.unity.openvr",
)

ADDRESS_PREFIXES_TO_REMOVE = (
    "Packages/com.basis.examples/",
    "Packages/com.basis.pooltable/",
    "Packages/com.basis.tests/",
)

XR_CONFIG_OBJECT_KEYS_TO_REMOVE = (
    "Unity.XR.OpenVR.Settings",
    "com.unity.xr.management.loader_settings",
    "com.unity.xr.openxr.settings4",
)

XR_ASSET_PATHS_TO_REMOVE = (
    "Assets/XR",
)

LINKER_ASSEMBLIES_TO_REMOVE = (
    "AudioLink",
)


def normalize_project_path(path: Path, project_root: Path) -> str:
    return path.relative_to(project_root).as_posix()


def build_guid_index(project_root: Path) -> dict[str, str]:
    guid_to_asset_path: dict[str, str] = {}

    for meta_path in project_root.rglob("*.meta"):
        guid = ""
        with meta_path.open(encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("guid: "):
                    guid = line.removeprefix("guid: ").strip()
                    break

        if not guid:
            continue

        asset_path = meta_path.with_suffix("")
        guid_to_asset_path[guid] = normalize_project_path(asset_path, project_root)

    return guid_to_asset_path


def should_remove_entry(address: str, guid: str, guid_to_asset_path: dict[str, str]) -> bool:
    if address.startswith(ADDRESS_PREFIXES_TO_REMOVE):
        return True

    asset_path = guid_to_asset_path.get(guid, "")
    return asset_path.startswith(PACKAGE_DIRS_TO_REMOVE)


def remove_manifest_dependencies(project_root: Path) -> list[str]:
    manifest_path = project_root / "Packages/manifest.json"
    lines = manifest_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_dependencies: list[str] = []

    for line in lines:
        stripped = line.strip()
        removed_dependency = None
        for dependency in PACKAGE_DEPENDENCIES_TO_REMOVE:
            if stripped.startswith(f'"{dependency}"'):
                removed_dependency = dependency
                break

        if removed_dependency is not None:
            removed_dependencies.append(removed_dependency)
            continue

        output.append(line)

    if removed_dependencies:
        manifest_path.write_text("".join(output), encoding="utf-8")

    return removed_dependencies


def strip_editor_build_settings(project_root: Path) -> list[str]:
    settings_path = project_root / "ProjectSettings/EditorBuildSettings.asset"
    lines = settings_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_keys: list[str] = []

    for line in lines:
        stripped = line.strip()
        removed_key = None
        for key in XR_CONFIG_OBJECT_KEYS_TO_REMOVE:
            if stripped.startswith(f"{key}:"):
                removed_key = key
                break

        if removed_key is not None:
            removed_keys.append(removed_key)
            continue

        output.append(line)

    if removed_keys:
        settings_path.write_text("".join(output), encoding="utf-8")

    return removed_keys


def remove_project_paths(project_root: Path, relative_paths: tuple[str, ...]) -> list[Path]:
    removed_paths: list[Path] = []

    for relative_path in relative_paths:
        target_path = project_root / relative_path
        meta_path = target_path.with_name(f"{target_path.name}.meta")

        if target_path.is_dir():
            shutil.rmtree(target_path)
            removed_paths.append(target_path)
        elif target_path.exists():
            target_path.unlink()
            removed_paths.append(target_path)

        if meta_path.exists():
            meta_path.unlink()
            removed_paths.append(meta_path)

    return removed_paths


def strip_linker_assemblies(project_root: Path) -> list[str]:
    link_path = project_root / "Assets/Basis/link.xml"
    if not link_path.exists():
        return []

    lines = link_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_assemblies: list[str] = []

    for line in lines:
        stripped = line.strip()
        removed_name = None
        for assembly_name in LINKER_ASSEMBLIES_TO_REMOVE:
            if stripped == f'<assembly fullname="{assembly_name}" preserve="all" />':
                removed_name = assembly_name
                break

        if removed_name is not None:
            removed_assemblies.append(removed_name)
            continue

        output.append(line)

    if removed_assemblies:
        link_path.write_text("".join(output), encoding="utf-8")

    return removed_assemblies


def strip_addressable_entries(asset_path: Path, guid_to_asset_path: dict[str, str]) -> list[str]:
    lines = asset_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_entries: list[str] = []
    index = 0

    while index < len(lines):
        line = lines[index]
        output.append(line)
        index += 1

        if line != "  m_SerializeEntries:\n":
            continue

        while index < len(lines) and lines[index].startswith("  - m_GUID:"):
            block_start = index
            guid = lines[index].split(":", 1)[1].strip()
            block_end = index + 1
            while block_end < len(lines):
                current = lines[block_end]
                if current.startswith("  - m_GUID:"):
                    break
                block_end += 1

            block = lines[block_start:block_end]
            address = ""
            for block_line in block:
                marker = "m_Address:"
                if marker in block_line:
                    address = block_line.split(marker, 1)[1].strip()
                    break

            if should_remove_entry(address, guid, guid_to_asset_path):
                asset_ref = guid_to_asset_path.get(guid, "<missing>")
                removed_entries.append(f"{address} [guid={guid} path={asset_ref}]")
            else:
                output.extend(block)

            index = block_end

    if removed_entries:
        asset_path.write_text("".join(output), encoding="utf-8")

    return removed_entries


def main() -> int:
    project_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Basis")
    project_root = project_root.resolve()

    if not project_root.exists():
        print(f"Project root not found: {project_root}", file=sys.stderr)
        return 1

    guid_to_asset_path = build_guid_index(project_root)

    removed_dependencies = remove_manifest_dependencies(project_root)
    if removed_dependencies:
        print(f"Removed {len(removed_dependencies)} manifest dependencies from {project_root / 'Packages/manifest.json'}")
        for dependency in removed_dependencies:
            print(f"  - {dependency}")

    removed_config_keys = strip_editor_build_settings(project_root)
    if removed_config_keys:
        print(f"Removed {len(removed_config_keys)} XR config objects from {project_root / 'ProjectSettings/EditorBuildSettings.asset'}")
        for key in removed_config_keys:
            print(f"  - {key}")

    asset_groups_dir = project_root / "Assets/AddressableAssetsData/AssetGroups"
    removed_total = 0
    for asset_path in sorted(asset_groups_dir.glob("*.asset")):
        removed = strip_addressable_entries(asset_path, guid_to_asset_path)
        if removed:
            removed_total += len(removed)
            print(f"Removed {len(removed)} Addressables entries from {asset_path}")
            for entry in removed:
                print(f"  - {entry}")

    removed_xr_paths = remove_project_paths(project_root, XR_ASSET_PATHS_TO_REMOVE)
    for path in removed_xr_paths:
        print(f"Removed XR path: {path}")

    removed_linker_assemblies = strip_linker_assemblies(project_root)
    if removed_linker_assemblies:
        print(f"Removed {len(removed_linker_assemblies)} linker preserve entries from {project_root / 'Assets/Basis/link.xml'}")
        for assembly_name in removed_linker_assemblies:
            print(f"  - {assembly_name}")

    for relative_dir in PACKAGE_DIRS_TO_REMOVE:
        package_dir = project_root / relative_dir
        if package_dir.exists():
            shutil.rmtree(package_dir)
            print(f"Removed package directory: {package_dir}")
        else:
            print(f"Package directory already absent: {package_dir}")

    if removed_total == 0:
        print("No sample/test Addressables entries needed removal.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
