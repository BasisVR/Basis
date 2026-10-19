import argparse
import concurrent.futures
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

RNNOISE_REPO = "https://github.com/Yellow-Dog-Man/rnnoise.git"
RNNOISE_COMMIT = "f0b73e38c029660bc6e010b583b406f6470f8a1b"
DEFINES = ("COMPILE_OPUS", "RNNOISE_BUILD", "NDEBUG")
INCLUDES = ("include", "src")
PREFIX = "basis_rnnoise_"
VARIANTS = {"wasm64": ("-sMEMORY64=1", "-msimd128", "-mbulk-memory", "-mbulk-memory-opt", "-mnontrapping-fptoint"), "wasm32": ("-mno-bulk-memory", "-mno-bulk-memory-opt", "-mno-nontrapping-fptoint")}
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


def am_sources(rnnoise):
    text = (rnnoise / "Makefile.am").read_text().replace("\\\n", " ")
    return re.search(r"^librnnoise_la_SOURCES\s*=\s*(.*)$", text, re.M).group(1).split()


def required_symbols():
    text = (PACKAGE / "Plugins/Native.cs").read_text(encoding="utf-8-sig")
    return set(re.findall(r"\[DllImport\(LIBRARY_NAME\)\]\s*public static extern [^(]*?\b(\w+)\s*\(", text))


def run(cmd, env, cwd=None):
    result = subprocess.run([str(c) for c in cmd], env=env, cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{' '.join(str(c) for c in cmd)}\n{result.stdout}{result.stderr}")
    return result.stdout


def defined_symbols(nm, files, env):
    return {line.strip() for line in run([nm, "--defined-only", "--extern-only", "-j", *files], env).splitlines() if line.strip() and not line.strip().endswith(":")}


def compile_all(emcc, flags, jobs, env, rnnoise):
    with concurrent.futures.ThreadPoolExecutor(os.cpu_count()) as pool:
        list(pool.map(lambda job: run([*emcc, "-c", job[0], "-o", job[1], *flags], env, rnnoise), jobs))


def build_variant(variant, emsdk, python, env, rnnoise, work, out):
    objdir = work / variant
    objdir.mkdir(parents=True)
    exe = ".exe" if sys.platform == "win32" else ""
    nm, ar = emsdk / f"llvm/llvm-nm{exe}", emsdk / f"llvm/llvm-ar{exe}"
    emcc = [python, emsdk / "emscripten/emcc.py"]
    flags = ["-O3", *VARIANTS[variant], *[f"-D{d}" for d in DEFINES], *[f"-I{i}" for i in INCLUDES]]
    jobs = [(src, objdir / (Path(src).stem + ".o")) for src in am_sources(rnnoise)]
    compile_all(emcc, flags, jobs, env, rnnoise)
    internal = sorted(s for s in defined_symbols(nm, [obj for _, obj in jobs], env) if not s.startswith("rnnoise_"))
    rename = objdir / "rename.h"
    rename.write_text("".join(f"#define {s} {PREFIX}{s}\n" for s in internal))
    compile_all(emcc, [*flags, "-include", rename], jobs, env, rnnoise)
    objects = sorted(obj for _, obj in jobs)
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()
    run([ar, "rcs", out, *objects], env)
    defined = defined_symbols(nm, [out], env)
    missing = sorted(required_symbols() - defined)
    leaked = sorted(s for s in defined if not s.startswith(("rnnoise_", PREFIX)))
    if missing or leaked:
        sys.exit(f"{out.name} missing {missing} leaked {leaked}")
    print(f"{out} {out.stat().st_size // 1024} KB, {len(objects)} objects, {len(internal)} internal symbols prefixed {PREFIX}")


def main():
    parser = argparse.ArgumentParser(description="Builds rnnoise for Unity Web (wasm32 and wasm64) using the Emscripten bundled with Unity, with its internal CELT symbols prefixed so it links next to libopus")
    parser.add_argument("--emsdk", type=Path, help="Unity WebGLSupport/BuildTools/Emscripten folder, defaults to the editor in ProjectVersion.txt")
    parser.add_argument("--rnnoise", type=Path, help=f"rnnoise checkout at {RNNOISE_COMMIT[:8]}, cloned when omitted")
    parser.add_argument("--out", type=Path, default=PACKAGE / "Plugins/web")
    parser.add_argument("--variants", nargs="+", choices=list(VARIANTS), default=list(VARIANTS))
    args = parser.parse_args()
    emsdk = (args.emsdk or find_emsdk()).resolve()
    bundled = emsdk / ("python/python.exe" if sys.platform == "win32" else "python/bin/python3")
    python = bundled if bundled.is_file() else Path(sys.executable)
    env = dict(os.environ, EM_CONFIG=str(emsdk / ".emscripten"), EMSDK_PYTHON=str(python), EMCC_SKIP_SANITY_CHECK="1", EM_FROZEN_CACHE="1")
    print(f"Emscripten {(emsdk / 'emscripten/emscripten-version.txt').read_text().strip()} at {emsdk}")
    with tempfile.TemporaryDirectory() as temp:
        work = Path(temp)
        rnnoise = args.rnnoise.resolve() if args.rnnoise else work / "rnnoise"
        if not args.rnnoise:
            run(["git", "clone", "-q", "--filter=blob:none", RNNOISE_REPO, rnnoise], env)
            run(["git", "-C", rnnoise, "-c", "advice.detachedHead=false", "checkout", "-q", RNNOISE_COMMIT], env)
        for variant in args.variants:
            build_variant(variant, emsdk, python, env, rnnoise, work, args.out / f"librnnoise_{variant}.a")


if __name__ == "__main__":
    main()
