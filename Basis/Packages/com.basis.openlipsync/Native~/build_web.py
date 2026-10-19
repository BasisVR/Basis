import argparse
import base64
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

FLAGS = ("-O3", "-msimd128", "-mbulk-memory", "-mnontrapping-fptoint", "--no-entry", "-sSTANDALONE_WASM=1", "-sFILESYSTEM=0", "-sALLOW_MEMORY_GROWTH=1", "-sINITIAL_MEMORY=33554432", "-sERROR_ON_UNDEFINED_SYMBOLS=1")
EXPORTS = ("basis_ols_weight_count", "basis_ols_net_create", "basis_ols_net_destroy", "basis_ols_session_create", "basis_ols_session_destroy", "basis_ols_session_reset", "basis_ols_step", "malloc", "free")
ALLOWED_IMPORTS = {("env", "emscripten_notify_memory_growth")}
NATIVE = Path(__file__).resolve().parent
PACKAGE = NATIVE.parent
PROJECT = PACKAGE.parent.parent


def editor_roots():
    if sys.platform == "win32":
        return [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity/Hub/Editor"]
    if sys.platform == "darwin":
        return [Path("/Applications/Unity/Hub/Editor")]
    return [Path.home() / "Unity/Hub/Editor"]


def emsdk_for(editor):
    for data in (editor / "Editor/Data", editor):
        path = data / "PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
        if (path / ".emscripten").is_file():
            return path
    return None


def find_emsdk():
    version_file = PROJECT / "ProjectSettings/ProjectVersion.txt"
    wanted = re.search(r"m_EditorVersion:\s*(\S+)", version_file.read_text()).group(1) if version_file.is_file() else None
    found = []
    for root in editor_roots():
        if root.is_dir():
            for editor in sorted(root.iterdir(), reverse=True):
                emsdk = emsdk_for(editor)
                if emsdk:
                    if editor.name == wanted:
                        return emsdk
                    found.append(emsdk)
    if not found:
        sys.exit("No Unity install with the Web module found, pass --emsdk")
    print(f"Unity {wanted} has no Web module, using {found[0]}")
    return found[0]


def run(cmd, env, cwd=None):
    result = subprocess.run([str(c) for c in cmd], env=env, cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{' '.join(str(c) for c in cmd)}\n{result.stdout}{result.stderr}")
    return result.stdout


def leb(data, pos):
    value = shift = 0
    while True:
        byte = data[pos]
        pos += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return value, pos


def name(data, pos):
    length, pos = leb(data, pos)
    return data[pos:pos + length].decode(), pos + length


def interface(data):
    if data[:8] != b"\0asm\1\0\0\0":
        sys.exit("kernel is not a wasm module")
    imports, exports, pos = set(), set(), 8
    while pos < len(data):
        section = data[pos]
        size, body = leb(data, pos + 1)
        end = body + size
        if section in (2, 7):
            count, at = leb(data, body)
            for _ in range(count):
                if section == 2:
                    module, at = name(data, at)
                    field, at = name(data, at)
                    kind = data[at]
                    at += 1
                    if kind == 0:
                        _, at = leb(data, at)
                    elif kind == 1:
                        at += 1
                        flags, at = leb(data, at)
                        _, at = leb(data, at)
                        if flags & 1:
                            _, at = leb(data, at)
                    elif kind == 2:
                        flags, at = leb(data, at)
                        _, at = leb(data, at)
                        if flags & 1:
                            _, at = leb(data, at)
                    else:
                        at += 2
                    imports.add((module, field))
                else:
                    field, at = name(data, at)
                    at += 1
                    _, at = leb(data, at)
                    exports.add(field)
        pos = end
    return imports, exports


def main():
    parser = argparse.ArgumentParser(description="Builds the OpenLipSync streaming network kernel as a standalone wasm module for the Unity Web lip sync worker, using the Emscripten bundled with Unity, and embeds it in BasisWebLipSyncKernel.jslib")
    parser.add_argument("--emsdk", type=Path, help="Unity WebGLSupport/BuildTools/Emscripten folder, defaults to the editor in ProjectVersion.txt")
    parser.add_argument("--out", type=Path, default=PACKAGE / "Plugins/Web/BasisWebLipSyncKernel.jslib")
    parser.add_argument("--wasm", type=Path, help="also write the raw kernel module here")
    args = parser.parse_args()
    emsdk = (args.emsdk or find_emsdk()).resolve()
    bundled = emsdk / ("python/python.exe" if sys.platform == "win32" else "python/bin/python3")
    python = bundled if bundled.is_file() else Path(sys.executable)
    env = dict(os.environ, EM_CONFIG=str(emsdk / ".emscripten"), EMSDK_PYTHON=str(python), EMCC_SKIP_SANITY_CHECK="1", EM_FROZEN_CACHE="1")
    print(f"Emscripten {(emsdk / 'emscripten/emscripten-version.txt').read_text().strip()} at {emsdk}")
    with tempfile.TemporaryDirectory() as temp:
        wasm = Path(temp) / "openlipsync.wasm"
        run([python, emsdk / "emscripten/emcc.py", "openlipsync_net.c", "-o", wasm, *FLAGS, "-sEXPORTED_FUNCTIONS=" + ",".join("_" + e for e in EXPORTS)], env, NATIVE)
        data = wasm.read_bytes()
    imports, exports = interface(data)
    if not imports <= ALLOWED_IMPORTS:
        sys.exit(f"kernel imports {sorted(imports - ALLOWED_IMPORTS)}, the worker only provides {sorted(ALLOWED_IMPORTS)}")
    missing = (set(EXPORTS) | {"memory"}) - exports
    if missing:
        sys.exit(f"kernel does not export {sorted(missing)}")
    if args.wasm:
        args.wasm.parent.mkdir(parents=True, exist_ok=True)
        args.wasm.write_bytes(data)
    encoded = base64.b64encode(data).decode()
    lines = ["var BasisWebLipSyncKernelLibrary = {", "  $BasisWebLipSyncKernel: function () {", "    return '" + encoded + "';", "  }", "};", "", "mergeInto(LibraryManager.library, BasisWebLipSyncKernelLibrary);", ""]
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes("\r\n".join(lines).encode())
    print(f"{args.out} {len(data) // 1024} KB kernel, imports {sorted(imports)}")


if __name__ == "__main__":
    main()
