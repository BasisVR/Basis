import argparse
import concurrent.futures
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

OPUS_REPO = "https://github.com/xiph/opus.git"
OPUS_COMMIT = "788cc89ce4f2c42025d8c70ec1b4457dc89cd50f"
OPUS_VERSION = "1.6.1-11-g788cc89c"
SOURCES = {"opus_sources.mk": ("OPUS_SOURCES", "OPUS_SOURCES_FLOAT"), "celt_sources.mk": ("CELT_SOURCES",), "silk_sources.mk": ("SILK_SOURCES", "SILK_SOURCES_FLOAT")}
DEFINES = ("OPUS_BUILD", "HAVE_CONFIG_H", "NDEBUG", "ENABLE_HARDENING", "DISABLE_DEBUG_FLOAT", "VAR_ARRAYS", "HAVE_LRINT", "HAVE_LRINTF")
INCLUDES = (".", "include", "celt", "silk", "silk/float", "dnn")
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


def mk_sources(opus):
    files = []
    for mk, names in SOURCES.items():
        text = (opus / mk).read_text().replace("\\\n", " ")
        values = {m.group(1): m.group(2).split() for m in re.finditer(r"^(\w+)[ \t]*=[ \t]*(.*)$", text, re.M)}
        for name in names:
            files += values[name]
    return files


def required_symbols():
    text = (PACKAGE / "OpusSharp.Core/StaticNativeOpus.cs").read_text(encoding="utf-8-sig")
    return {m.group(1) or m.group(2) for m in re.finditer(r'\[DllImport\(DllName(?:, EntryPoint = "(\w+)")?[^\]]*\]\s*public static extern [^(]*?\b(\w+)\s*\(', text)}


def run(cmd, env, cwd=None):
    result = subprocess.run([str(c) for c in cmd], env=env, cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{' '.join(str(c) for c in cmd)}\n{result.stdout}{result.stderr}")
    return result.stdout


def build_variant(variant, emsdk, python, env, opus, work, out):
    objdir = work / variant
    objdir.mkdir(parents=True)
    emcc = [python, emsdk / "emscripten/emcc.py"]
    flags = ["-O3", *VARIANTS[variant], *[f"-D{d}" for d in DEFINES], f"-I{work}", *[f"-I{i}" for i in INCLUDES]]
    jobs = [(src, objdir / (src.replace("/", "_")[:-2] + ".o")) for src in mk_sources(opus)]
    jobs.append((NATIVE / "opus_shim.c", objdir / "opus_shim.o"))
    with concurrent.futures.ThreadPoolExecutor(os.cpu_count()) as pool:
        list(pool.map(lambda job: run([*emcc, "-c", job[0], "-o", job[1], *flags], env, opus), jobs))
    objects = sorted(obj for _, obj in jobs)
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()
    llvm = emsdk / "llvm"
    exe = ".exe" if sys.platform == "win32" else ""
    run([llvm / f"llvm-ar{exe}", "rcs", out, *objects], env)
    defined = {line.strip() for line in run([llvm / f"llvm-nm{exe}", "--defined-only", "--extern-only", "-j", out], env).splitlines() if line.strip() and not line.strip().endswith(":")}
    missing = sorted(required_symbols() - defined)
    if missing:
        sys.exit(f"{out.name} is missing {', '.join(missing)}")
    print(f"{out} {out.stat().st_size // 1024} KB, {len(objects)} objects")


def main():
    parser = argparse.ArgumentParser(description="Builds libopus with the OpusSharp CTL shim for Unity Web (wasm32 and wasm64) using the Emscripten bundled with Unity")
    parser.add_argument("--emsdk", type=Path, help="Unity WebGLSupport/BuildTools/Emscripten folder, defaults to the editor in ProjectVersion.txt")
    parser.add_argument("--opus", type=Path, help=f"opus checkout at {OPUS_COMMIT[:8]}, cloned when omitted")
    parser.add_argument("--out", type=Path, default=PACKAGE / "Plugins/web/native")
    parser.add_argument("--variants", nargs="+", choices=list(VARIANTS), default=list(VARIANTS))
    args = parser.parse_args()
    emsdk = (args.emsdk or find_emsdk()).resolve()
    bundled = emsdk / ("python/python.exe" if sys.platform == "win32" else "python/bin/python3")
    python = bundled if bundled.is_file() else Path(sys.executable)
    env = dict(os.environ, EM_CONFIG=str(emsdk / ".emscripten"), EMSDK_PYTHON=str(python), EMCC_SKIP_SANITY_CHECK="1", EM_FROZEN_CACHE="1")
    print(f"Emscripten {(emsdk / 'emscripten/emscripten-version.txt').read_text().strip()} at {emsdk}")
    with tempfile.TemporaryDirectory() as temp:
        work = Path(temp)
        opus = args.opus.resolve() if args.opus else work / "opus"
        if not args.opus:
            run(["git", "clone", "-q", "--filter=blob:none", OPUS_REPO, opus], env)
            run(["git", "-C", opus, "-c", "advice.detachedHead=false", "checkout", "-q", OPUS_COMMIT], env)
        (work / "config.h").write_text(f'#define PACKAGE_VERSION "{OPUS_VERSION}"\n')
        for variant in args.variants:
            build_variant(variant, emsdk, python, env, opus, work, args.out / f"libopus_{variant}.a")


if __name__ == "__main__":
    main()
