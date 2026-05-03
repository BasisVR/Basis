#!/usr/bin/env python3
"""Build VPM package zips and merge them into a repository index.

  build         Emit <out>/artifacts/<name>-<version>.zip and
                <out>/meta/<name>.json for each entry in vpm-packages.txt.
  update-index  Merge those meta files into an index.json, rewriting urls.
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MANIFEST = REPO_ROOT / ".github" / "vpm-packages.txt"
PACKAGES_DIR = REPO_ROOT / "Basis" / "Packages"

GUID_RE = re.compile(r"^guid:\s*(\S+)", re.MULTILINE)
LICENSE_FILENAMES = {"license", "license.md", "license.txt"}
NAME_RE = re.compile(r"^[A-Za-z0-9.\-]+$")

# Semver 2.0.0 without +build (VPM forbids it).
SEMVER_RE = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)"
    r"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$"
)

# Zip format minimum date, used when git is unavailable.
ZIP_DATE_TIME_FALLBACK = (1980, 1, 1, 0, 0, 0)

INDEX_TEMPLATE = {
    "name": "Basis VPM Repository",
    "id": "org.basisvr.vpm",
    "url": "https://raw.githubusercontent.com/basisvr/Basis/vpm/vpm-repo.json",
    "author": "BasisVR",
    "packages": {},
}

GITHUB_ACTIONS = os.environ.get("GITHUB_ACTIONS") == "true"


def warn(msg):
    """Emit a workflow annotation under GitHub Actions, plain text otherwise."""
    prefix = "::warning::" if GITHUB_ACTIONS else "warn: "
    print(f"{prefix}{msg}", file=sys.stderr)


def head_commit_datetime():
    """HEAD commit time as a zip date_time tuple, for reproducible builds."""
    try:
        result = subprocess.run(
            ["git", "log", "-1", "--format=%ct", "HEAD"],
            capture_output=True, text=True, cwd=str(REPO_ROOT), timeout=10,
            check=True,
        )
        epoch = int(result.stdout.strip())
        dt = datetime.datetime.fromtimestamp(epoch, tz=datetime.timezone.utc)
    except (FileNotFoundError, subprocess.TimeoutExpired,
            subprocess.CalledProcessError, ValueError):
        return ZIP_DATE_TIME_FALLBACK
    if dt.year < 1980:
        return ZIP_DATE_TIME_FALLBACK
    return (dt.year, dt.month, dt.day, dt.hour, dt.minute, dt.second)


def parse_args():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    build = sub.add_parser("build", help="Generate package zips and metadata.")
    build.add_argument("--dev-suffix", default="", metavar="SUFFIX",
                       help="Append a semver prerelease suffix to every package version.")
    build.add_argument("--out", default=str(REPO_ROOT / "out"), metavar="DIR",
                       help="Output directory (default: <repo>/out).")

    update = sub.add_parser("update-index", help="Merge metadata into an index.json.")
    update.add_argument("existing_index", metavar="EXISTING_INDEX",
                        help="Existing index.json. Missing or empty starts from a fresh template.")
    update.add_argument("base_url", metavar="RELEASE_BASE_URL",
                        help="Base URL prefixed to each zip filename.")
    update.add_argument("meta_dir", metavar="METADATA_DIR",
                        help="Directory of per-package metadata json files.")
    update.add_argument("--require-superset", action="store_true",
                        help="Abort if any name@version in the input is missing from the output.")
    update.add_argument("--self-url", metavar="URL", default=None,
                        help="Override the index 'url' on first-run bootstrap.")
    update.add_argument("--added-list", metavar="FILE", default=None,
                        help="Write TSV (name, version, zipname) of newly-added entries to FILE.")

    return p.parse_args()


def read_manifest(path):
    if not path.is_file():
        sys.exit(f"error: manifest not found at {path}")
    out = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if line and not line.startswith("#"):
            out.append(line)
    if not out:
        sys.exit(f"error: {path} contains no package entries")
    return out


def build_asmdef_index(packages_dir):
    """GUID and asmdef-name maps to the owning package folder."""
    by_guid = {}
    by_name = {}
    for asmdef in packages_dir.rglob("*.asmdef"):
        meta = asmdef.with_suffix(asmdef.suffix + ".meta")
        if not meta.is_file():
            continue
        m = GUID_RE.search(meta.read_text(encoding="utf-8"))
        if not m:
            continue
        guid = m.group(1).strip()
        if not guid:
            continue
        data = json.loads(asmdef.read_text(encoding="utf-8-sig"))
        name = (data.get("name") or "").strip()
        if not name:
            continue
        pkg_folder = asmdef.relative_to(packages_dir).parts[0]
        by_guid[guid] = pkg_folder
        by_name[name] = pkg_folder
    return by_guid, by_name


def build_dll_index(packages_dir):
    """Map DLL filename -> owning package folder, for precompiledReferences resolution."""
    by_dll = {}
    for dll in packages_dir.rglob("*.dll"):
        pkg_folder = dll.relative_to(packages_dir).parts[0]
        by_dll[dll.name] = pkg_folder
    return by_dll


def apply_dev_suffix(version, suffix):
    if not suffix:
        return version
    sep = "." if "-" in version else "-"
    return f"{version}{sep}{suffix}"


def compute_vpm_dependencies(pkg_folder, packages, by_guid, by_name, by_dll, suffix):
    """Resolve asmdef references and precompiledReferences to their owning
    package, returning {name: version} for those listed in vpm-packages.txt.

    Note: this can't see DLLs picked up via 'autoReferenced: true' alone (i.e.
    a 'using Foo;' with no explicit precompiledReferences entry) — those deps
    must be declared manually in package.json."""
    pkg_dir = PACKAGES_DIR / pkg_folder
    targets = set()
    for asmdef in pkg_dir.rglob("*.asmdef"):
        data = json.loads(asmdef.read_text(encoding="utf-8-sig"))
        for ref in data.get("references") or []:
            ref = ref.strip()
            if not ref:
                continue
            if ref.startswith("GUID:"):
                target = by_guid.get(ref[5:])
            else:
                target = by_name.get(ref)
            if target and target != pkg_folder:
                targets.add(target)
        for dll_ref in data.get("precompiledReferences") or []:
            dll_ref = dll_ref.strip()
            if not dll_ref:
                continue
            target = by_dll.get(dll_ref)
            if target and target != pkg_folder:
                targets.add(target)

    package_set = set(packages)
    deps = {}
    for target in sorted(targets):
        if target not in package_set:
            warn(f"{pkg_folder} references {target} "
                 f"(not in vpm-packages.txt), omitting from vpmDependencies")
            continue
        data = json.loads((PACKAGES_DIR / target / "package.json").read_text(encoding="utf-8"))
        deps[data["name"]] = apply_dev_suffix(data["version"], suffix)
    return deps


def validate_source_paths(pkg_dir):
    """Reject symlinks, traversal, absolute paths, backslashes, and colons."""
    bad = False
    for entry in pkg_dir.rglob("*"):
        rel = entry.relative_to(pkg_dir).as_posix()
        problem = None
        if entry.is_symlink():
            problem = "symlink"
        elif ".." in rel.split("/"):
            problem = "path traversal"
        elif rel.startswith("/"):
            problem = "absolute path"
        elif "\\" in rel:
            problem = "backslash in path"
        elif ":" in rel:
            problem = "colon in path"
        if problem:
            print(f"  {problem}: {rel}", file=sys.stderr)
            bad = True
    return not bad


def has_license_file(pkg_dir):
    return any(name.lower() in LICENSE_FILENAMES for name in os.listdir(pkg_dir))


def write_zip(zip_path, pkg_dir, modified_manifest, zip_date_time):
    """Deterministic zip: sorted entries, posix paths, fixed timestamp."""
    files = sorted(p for p in pkg_dir.rglob("*") if p.is_file())
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in files:
            arcname = path.relative_to(pkg_dir).as_posix()
            if arcname == "package.json":
                data = json.dumps(modified_manifest, indent=2).encode("utf-8")
            else:
                data = path.read_bytes()
            zi = zipfile.ZipInfo(arcname, date_time=zip_date_time)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(zi, data)


def sha256_of_file(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def cmd_build(args):
    out_dir = Path(args.out).resolve()
    artifacts_dir = out_dir / "artifacts"
    meta_dir = out_dir / "meta"
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    meta_dir.mkdir(parents=True, exist_ok=True)

    packages = read_manifest(MANIFEST)

    missing = [p for p in packages if not (PACKAGES_DIR / p / "package.json").is_file()]
    if missing:
        print("error: package.json missing for the following entries:", file=sys.stderr)
        for p in missing:
            print(f"  - {p} (expected at {PACKAGES_DIR / p / 'package.json'})", file=sys.stderr)
        sys.exit(1)

    by_guid, by_name = build_asmdef_index(PACKAGES_DIR)
    by_dll = build_dll_index(PACKAGES_DIR)
    zip_dt = head_commit_datetime()

    manifests = {}
    for pkg in packages:
        data = json.loads((PACKAGES_DIR / pkg / "package.json").read_text(encoding="utf-8"))
        if not SEMVER_RE.fullmatch(data["version"]):
            sys.exit(f"error: {pkg}: 'version' {data['version']!r} is not valid semver")
        manifests[pkg] = data

    # name -> suffixed version, used to rewrite vpmDependencies under --dev-suffix
    known_versions = {
        d["name"]: apply_dev_suffix(d["version"], args.dev_suffix)
        for d in manifests.values()
    }

    count = 0
    for pkg in packages:
        pkg_dir = PACKAGES_DIR / pkg
        manifest_data = manifests[pkg]
        pkg_name = manifest_data["name"]
        pkg_version = apply_dev_suffix(manifest_data["version"], args.dev_suffix)

        if not NAME_RE.fullmatch(pkg_name):
            sys.exit(f"error: {pkg}: 'name' {pkg_name!r} contains invalid characters")
        if pkg_name != pkg_name.lower():
            warn(f"{pkg_name} should be lowercase")

        print(f"==> {pkg_name} @ {pkg_version}")

        if not validate_source_paths(pkg_dir):
            sys.exit(f"error: {pkg_name} failed source path validation")

        modified = dict(manifest_data)
        modified["version"] = pkg_version
        # _fingerprint is a Unity import-time hash, not an authoring field.
        modified.pop("_fingerprint", None)

        # Auto-derive vpmDependencies from asmdef refs; manually-declared ones win.
        derived = compute_vpm_dependencies(pkg, packages, by_guid, by_name, by_dll, args.dev_suffix)
        if derived:
            merged = dict(derived)
            merged.update(modified.get("vpmDependencies") or {})
            modified["vpmDependencies"] = merged
            print(f"    vpmDeps: {json.dumps(derived)}")

        # Rewrite deps that point at known packages so --dev-suffix versions resolve.
        if modified.get("vpmDependencies"):
            modified["vpmDependencies"] = {
                k: known_versions.get(k, v)
                for k, v in modified["vpmDependencies"].items()
            }

        if not modified.get("license"):
            warn(f"{pkg_name} has no 'license' field")
        if not has_license_file(pkg_dir):
            warn(f"{pkg_name} has no LICENSE file at package root")

        zip_path = artifacts_dir / f"{pkg_name}-{pkg_version}.zip"
        write_zip(zip_path, pkg_dir, modified, zip_dt)
        zip_sha = sha256_of_file(zip_path)

        meta_obj = dict(modified)
        meta_obj["zipSHA256"] = zip_sha
        meta_obj["url"] = "PLACEHOLDER://" + zip_path.name

        out_meta = meta_dir / f"{pkg_name}.json"
        out_meta.write_text(json.dumps(meta_obj, indent=2) + "\n", encoding="utf-8")

        print(f"    zip:    {zip_path}")
        print(f"    sha256: {zip_sha}")
        print(f"    meta:   {out_meta}")
        count += 1

    print()
    print(f"done. {count} package(s) emitted to {out_dir}")


def collect_pairs(index):
    pairs = set()
    for name, pkg in (index.get("packages") or {}).items():
        for ver in (pkg.get("versions") or {}).keys():
            pairs.add((name, ver))
    return pairs


def cmd_update_index(args):
    base_url = args.base_url.rstrip("/")
    meta_dir = Path(args.meta_dir)
    if not meta_dir.is_dir():
        sys.exit(f"error: metadata directory not found: {meta_dir}")

    existing = Path(args.existing_index)
    if existing.is_file() and existing.stat().st_size > 0:
        try:
            index = json.loads(existing.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            sys.exit(f"error: existing index is not valid JSON: {e}")
    else:
        print(f"info: starting from fresh template (no existing index at {existing})",
              file=sys.stderr)
        index = {**INDEX_TEMPLATE, "packages": {}}
        if args.self_url:
            index["url"] = args.self_url

    if not isinstance(index, dict) or not isinstance(index.get("packages", {}), dict):
        sys.exit("error: existing index must be an object with a .packages object")
    index.setdefault("packages", {})

    input_pairs = collect_pairs(index)

    meta_files = sorted(meta_dir.glob("*.json"))
    if not meta_files:
        warn(f"no *.json metadata files found in {meta_dir}")

    added = []  # list of (name, version, zipname)
    skipped = 0
    for meta_path in meta_files:
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            sys.exit(f"error: {meta_path} is not valid JSON: {e}")
        name = meta.get("name")
        version = meta.get("version")
        if not name or not version:
            sys.exit(f"error: {meta_path} missing required name/version")

        versions = index["packages"].get(name, {}).get("versions", {})
        if version in versions:
            print(f"skip:  {name}@{version} (already in index)", file=sys.stderr)
            skipped += 1
            continue

        zip_filename = f"{name}-{version}.zip"
        meta_with_url = dict(meta)
        meta_with_url["url"] = f"{base_url}/{zip_filename}"

        pkg_entry = index["packages"].setdefault(name, {})
        pkg_entry.setdefault("versions", {})[version] = meta_with_url

        print(f"add:   {name}@{version}", file=sys.stderr)
        added.append((name, version, zip_filename))

    if args.require_superset:
        missing = input_pairs - collect_pairs(index)
        if missing:
            listing = ", ".join(f"{n}@{v}" for n, v in sorted(missing))
            sys.exit(f"error: --require-superset: output is missing "
                     f"{len(missing)} pair(s) present in input: {listing}")

    if args.added_list:
        Path(args.added_list).write_text(
            "".join(f"{n}\t{v}\t{z}\n" for n, v, z in added), encoding="utf-8"
        )

    print(json.dumps(index, indent=2))
    print(f"done. {len(added)} added, {skipped} skipped", file=sys.stderr)


def main():
    args = parse_args()
    if args.cmd == "build":
        cmd_build(args)
    elif args.cmd == "update-index":
        cmd_update_index(args)
    else:
        sys.exit(f"error: unknown command {args.cmd!r}")


if __name__ == "__main__":
    main()
