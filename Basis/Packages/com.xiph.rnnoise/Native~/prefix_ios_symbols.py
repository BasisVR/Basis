import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from build_web import PREFIX, find_emsdk

PACKAGE = Path(__file__).resolve().parent.parent


def run(cmd, cwd=None):
    result = subprocess.run([str(c) for c in cmd], cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"{' '.join(str(c) for c in cmd)}\n{result.stdout}{result.stderr}")
    return result.stdout


def symbols(nm, path, *flags):
    return {line.strip() for line in run([nm, *flags, "-j", path]).splitlines() if line.strip() and not line.strip().endswith(":")}


def main():
    parser = argparse.ArgumentParser(description="Prefixes the internal symbols of the iOS librnnoise.a so it links next to libopus.a (both define opus_fft_c, opus_fft_impl and opus_ifft_c)")
    parser.add_argument("--archive", type=Path, default=PACKAGE / "Plugins/ios/librnnoise.a")
    parser.add_argument("--emsdk", type=Path)
    args = parser.parse_args()
    llvm = (args.emsdk or find_emsdk()).resolve() / "llvm"
    exe = ".exe" if sys.platform == "win32" else ""
    nm, ar, objcopy = llvm / f"llvm-nm{exe}", llvm / f"llvm-ar{exe}", llvm / f"llvm-objcopy{exe}"
    archive = args.archive.resolve()
    internal = sorted(s for s in symbols(nm, archive, "--defined-only", "--extern-only") if not s.startswith(("_rnnoise_", "_" + PREFIX)))
    if not internal:
        print(f"{archive} already prefixed")
        return
    with tempfile.TemporaryDirectory() as temp:
        work = Path(temp)
        members = run([ar, "t", archive]).split()
        run([ar, "x", archive], cwd=work)
        renames = work / "renames.txt"
        renames.write_text("".join(f"{s} _{PREFIX}{s[1:]}\n" for s in internal))
        for member in members:
            obj = work / member
            names = symbols(nm, obj, "--defined-only", "--extern-only") | symbols(nm, obj, "-u")
            if names & set(internal):
                run([objcopy, f"--redefine-syms={renames}", obj, obj])
        rebuilt = work / "librnnoise.a"
        run([ar, "--format=darwin", "rcs", rebuilt, *members], cwd=work)
        leftover = sorted(s for s in symbols(nm, rebuilt, "--defined-only", "--extern-only") if not s.startswith(("_rnnoise_", "_" + PREFIX)))
        stale = sorted(symbols(nm, rebuilt, "-u") & set(internal))
        if leftover or stale:
            sys.exit(f"rename incomplete: defined {leftover} referenced {stale}")
        shutil.copyfile(rebuilt, archive)
    print(f"{archive}: {len(internal)} internal symbols prefixed {PREFIX}")


if __name__ == "__main__":
    main()
