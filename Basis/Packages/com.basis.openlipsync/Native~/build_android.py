import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

TARGET = "aarch64-linux-android24"
FLAGS = ("-O3", "-fPIC", "-shared", "-ffunction-sections", "-fdata-sections", "-Wl,--gc-sections", "-Wl,-z,max-page-size=16384", "-Wl,--build-id=sha1", "-Wl,--hash-style=gnu", "-s", "-lm")
EXPORTS = ("basis_ols_weight_count", "basis_ols_net_create", "basis_ols_net_destroy", "basis_ols_session_create", "basis_ols_session_destroy", "basis_ols_session_reset", "basis_ols_step")
ALLOWED_NEEDED = {"libm.so", "libc.so", "libdl.so"}
NATIVE = Path(__file__).resolve().parent
PACKAGE = NATIVE.parent
PROJECT = PACKAGE.parent.parent


def editor_roots():
    if sys.platform == "win32":
        return [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity/Hub/Editor"]
    if sys.platform == "darwin":
        return [Path("/Applications/Unity/Hub/Editor")]
    return [Path.home() / "Unity/Hub/Editor"]


def host():
    if sys.platform == "win32":
        return "windows-x86_64", ".exe"
    if sys.platform == "darwin":
        return "darwin-x86_64", ""
    return "linux-x86_64", ""


def ndk_for(editor):
    name, _ = host()
    for data in (editor / "Editor/Data", editor / "Unity.app/Contents", editor):
        path = data / "PlaybackEngines/AndroidPlayer/NDK"
        if (path / "toolchains/llvm/prebuilt" / name / "bin").is_dir():
            return path
    return None


def find_ndk():
    version_file = PROJECT / "ProjectSettings/ProjectVersion.txt"
    wanted = re.search(r"m_EditorVersion:\s*(\S+)", version_file.read_text()).group(1) if version_file.is_file() else None
    found = []
    for root in editor_roots():
        if root.is_dir():
            for editor in sorted(root.iterdir(), reverse=True):
                ndk = ndk_for(editor)
                if ndk:
                    if editor.name == wanted:
                        return ndk
                    found.append(ndk)
    if not found:
        sys.exit("No Unity install with the Android module found, pass --ndk")
    print(f"Unity {wanted} has no Android module, using {found[0]}")
    return found[0]


def run(cmd, cwd=None):
    result = subprocess.run([str(c) for c in cmd], cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{' '.join(str(c) for c in cmd)}\n{result.stdout}{result.stderr}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description="Builds the OpenLipSync streaming network kernel as an arm64 Android shared library with the NDK bundled with Unity, for the native Android lip sync backend")
    parser.add_argument("--ndk", type=Path, help="Unity AndroidPlayer/NDK folder, defaults to the editor in ProjectVersion.txt")
    parser.add_argument("--out", type=Path, default=PACKAGE / "Plugins/android-arm64/native/libbasis_openlipsync.so")
    args = parser.parse_args()
    ndk = (args.ndk or find_ndk()).resolve()
    name, exe = host()
    bin_dir = ndk / "toolchains/llvm/prebuilt" / name / "bin"
    revision = re.search(r"Pkg\.Revision\s*=\s*(\S+)", (ndk / "source.properties").read_text())
    print(f"NDK {revision.group(1) if revision else '?'} at {ndk}")
    with tempfile.TemporaryDirectory() as temp:
        script = Path(temp) / "exports.map"
        script.write_text("{\n  global:\n" + "".join(f"    {e};\n" for e in EXPORTS) + "  local:\n    *;\n};\n")
        library = Path(temp) / "libbasis_openlipsync.so"
        run([bin_dir / ("clang" + exe), f"--target={TARGET}", *FLAGS, f"-Wl,--version-script={script}", "openlipsync_net.c", "-o", library], NATIVE)
        header = run([bin_dir / ("llvm-readelf" + exe), "--file-header", library])
        needed = set(re.findall(r"Shared library: \[([^\]]+)\]", run([bin_dir / ("llvm-readelf" + exe), "--dynamic", library])))
        symbols = run([bin_dir / ("llvm-readelf" + exe), "--dyn-syms", "--wide", library])
        data = library.read_bytes()
    if "AArch64" not in header:
        sys.exit("the kernel is not an AArch64 library")
    if not needed <= ALLOWED_NEEDED:
        sys.exit(f"the kernel links {sorted(needed - ALLOWED_NEEDED)}, only {sorted(ALLOWED_NEEDED)} are expected")
    exported = set(re.findall(r"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+(\S+)", symbols))
    if exported != set(EXPORTS):
        sys.exit(f"the kernel exports {sorted(exported)}, expected {sorted(EXPORTS)}")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(data)
    print(f"{args.out} {len(data) // 1024} KB kernel, needs {sorted(needed)}")


if __name__ == "__main__":
    main()
