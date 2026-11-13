"""Basis GPU test pipeline: builds the path tracer (tracer, bridge, harness) and the D3D11 VRS plugin
(plugin, stereo harness), runs a tiered scenario matrix against one build and optionally a baseline
build, and fails on what has actually broken Basis before:

  hangs            every run has a timeout; on expiry all thread stacks are captured with cdb
  GPU resets       NVIDIA driver events (153 = timeout/reset, 13 = MMU fault) logged during a run
  crashes          non zero exits, device removed, tracer stopped, Rust panics
  stutters         frame time p99/max and stutter counts from each harness
  blocking waits   Unity side GPU waits on unfinished tracer frames (the in-process TDR cause)
  bad output       non finite pixels, mean luminance drift against the baseline build

Usage (from anywhere):
  python Tools/GpuTests/run.py build [--name current]
  python Tools/GpuTests/run.py snapshot --name before        (copy the last build aside, for A/B)
  python Tools/GpuTests/run.py run [--build current] [--baseline before] [--tier smoke|full|stress]
                                   [--only substring] [--allow-gpu-risk] [--repeat n]
  python Tools/GpuTests/run.py list

Scenarios marked gpu_risk can reset the GPU on a broken build; they refuse to run while a Unity editor
or SteamVR is open unless --allow-gpu-risk is passed. Results land in Tools/GpuTests/results/<time>/.
"""
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
PROJECT = REPO / "Basis"
RT_SRC = PROJECT / "Packages/com.basis.globalillumination/Native~/BasisRayTracer"
RT_D3D12 = PROJECT / "Packages/com.basis.globalillumination/Native~/Tracer/D3D12"
VRS_SRC = PROJECT / "Packages/com.basis.framework/Rendering/VariableRateShading/Native~/BasisVrsD3D11"
BUILDS = HERE / "builds"
RESULTS = HERE / "results"
CDB_CANDIDATES = [
    Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft/WindowsApps/cdbX64.exe",
    Path("C:/Program Files (x86)/Windows Kits/10/Debuggers/x64/cdb.exe"),
    Path("C:/Program Files/Windows Kits/10/Debuggers/x64/cdb.exe"),
]

# ---------------------------------------------------------------------------------------------------
# Scenarios. kind "rt" runs basis-rt-harness, kind "vrs" runs harness_stereo. Every scenario also gets
# the generic checks (exit 0, no hang, no driver event, no device removal, RESULT line present).
# expect: list of (metric, op, value) checked against the RESULT line.
# ---------------------------------------------------------------------------------------------------
INPROC = {"API": "11", "EMBED": "1", "DIRECT": "1"}
STEREO = {"VIEWS": "2", "W": "640", "H": "640"}
PACED = {"NOSTALL": "1"}

SCENARIOS = [
    # smoke: every build path once, fast
    dict(name="rt_copy_d3d12", kind="rt", tier="smoke", frames=60, env={}, expect=[("fetch_misses", "<=", 6), ("nonfinite", "==", 0)]),
    dict(name="rt_copy_d3d11", kind="rt", tier="smoke", frames=60, env={"API": "11"}, expect=[("nonfinite", "==", 0)]),
    dict(name="rt_inproc_stereo", kind="rt", tier="smoke", frames=60, env={**INPROC, **STEREO, **PACED}, expect=[("blocked_waits", "==", 0), ("nonfinite", "==", 0)]),
    dict(name="vrs_stereo", kind="vrs", tier="smoke", frames=400, expect=[("failures", "==", 0), ("frame_stutters", "<=", 2)]),

    # full: late tracer, stutter spikes, render scale swaps, retargets, texture streaming, contexts, APIs
    dict(name="rt_inproc_late", kind="rt", tier="full", frames=90, env={**INPROC, **STEREO, **PACED, "BASIS_RT_TEST_DELAY_MS": "40"},
         expect=[("blocked_waits", "==", 0), ("rt_p99", "<", 70)], note="tracer 40 ms late every frame: Unity must not wait on it"),
    dict(name="rt_inproc_spike", kind="rt", tier="full", frames=120, env={**INPROC, **STEREO, **PACED, "BASIS_RT_TEST_SPIKE": "10:400"},
         expect=[("blocked_waits", "==", 0), ("rt_max", "<", 120)], note="400 ms tracer spike every 10 frames must not stall the render thread"),
    dict(name="rt_copy_late", kind="rt", tier="full", frames=90, env={"API": "11", **STEREO, **PACED, "BASIS_RT_TEST_DELAY_MS": "40"},
         expect=[("blocked_waits", "==", 0), ("rt_max", "<", 120)]),
    dict(name="rt_inproc_every_frame", kind="rt", tier="full", frames=60, env={**INPROC, **STEREO},
         expect=[("nonfinite", "==", 0)], note="submits every frame without the completion gate (out of process Unity pacing)"),
    dict(name="rt_inproc_resize", kind="rt", tier="full", frames=120, env={**INPROC, **STEREO, **PACED, "RESIZE": "9", "RESIZE_SCALE": "1186"},
         expect=[("blocked_waits", "==", 0), ("nonfinite", "==", 0)], note="render scale 1.186/1.0/0 swaps (2644 <-> 3136 eye flip)"),
    dict(name="rt_inproc_retarget", kind="rt", tier="full", frames=120, env={**INPROC, **STEREO, **PACED, "RETARGET": "11"},
         expect=[("blocked_waits", "==", 0), ("retargets", ">=", 5)], note="direct targets reallocated every 11 frames"),
    dict(name="rt_inproc_retarget_flip", kind="rt", tier="full", frames=90, env={**INPROC, **STEREO, **PACED, "RETARGETFLIP": "1"},
         expect=[("blocked_waits", "==", 0)]),
    dict(name="rt_copy_resize", kind="rt", tier="full", frames=120, env={"API": "11", **STEREO, "RESIZE": "9"}, expect=[("nonfinite", "==", 0)]),
    dict(name="rt_textures", kind="rt", tier="full", frames=90, env={"API": "11", "TEXTURES": "32"}, expect=[("nonfinite", "==", 0)], note="texture upload streaming"),
    dict(name="rt_inproc_textures", kind="rt", tier="full", frames=90, env={**INPROC, **PACED, "TEXTURES": "32"}, expect=[("nonfinite", "==", 0), ("blocked_waits", "==", 0)]),
    dict(name="rt_two_contexts", kind="rt", tier="full", frames=60, env={"API": "11", "CONTEXTS": "2"}, expect=[("nonfinite", "==", 0)], note="main camera + mirror"),
    dict(name="rt_vulkan_tracer", kind="rt", tier="full", frames=60, env={"TRACER": "vulkan"}, expect=[("nonfinite", "==", 0)]),
    dict(name="rt_inproc_swap_stop", kind="rt", tier="full", frames=40, gpu_risk=True, env={**INPROC, **STEREO, **PACED, "STOPTEST": "1", "SAMPLES": "8"},
         expect=[("stop_client_idle_ms", ">=", 0), ("stop_client_idle_ms", "<", 500)],
         note="built in <-> external swap: tracer stopped with a frame in flight, then the render thread's queued bridge events run; Unity's GPU must still drain"),
    dict(name="rt_copy_swap_stop", kind="rt", tier="full", frames=40, gpu_risk=True, env={"API": "11", **STEREO, "STOPTEST": "1", "SAMPLES": "8"},
         expect=[("stop_client_idle_ms", ">=", 0), ("stop_client_idle_ms", "<", 500)], note="same swap from the separate process side"),
    dict(name="vrs_long", kind="vrs", tier="full", frames=3000, expect=[("failures", "==", 0), ("frame_stutters", "<=", 10)]),

    # stress: realistic VR sizes and loads that reset the GPU on a broken build
    dict(name="rt_inproc_vr_size", kind="rt", tier="stress", frames=40, timeout=600, gpu_risk=True,
         env={**INPROC, **PACED, "VIEWS": "2", "W": "2644", "H": "2644", "SAMPLES": "16"}, expect=[("blocked_waits", "==", 0)]),
    dict(name="rt_inproc_tdr_delay", kind="rt", tier="stress", frames=30, timeout=600, gpu_risk=True,
         env={**INPROC, **STEREO, **PACED, "BASIS_RT_TEST_SPIKE": "5:2500"}, expect=[("blocked_waits", "==", 0), ("rt_max", "<", 200)],
         note="2.5 s tracer frames: a GPU wait on them would pass the TDR limit"),
    dict(name="vrs_stress", kind="vrs", tier="stress", frames=20000, timeout=900, gpu_risk=True, expect=[("failures", "==", 0)]),
]
TIERS = {"smoke": ["smoke"], "full": ["smoke", "full"], "stress": ["smoke", "full", "stress"]}
DEFAULT_TIMEOUT = 240
COOLDOWN_SECONDS = 2  # let the previous process's GPU teardown finish before the next run starts
LUMINANCE_TOLERANCE = 0.05


def log(msg):
    print(msg, flush=True)


# ------------------------------------------------------------------------------------------- build --

def msvc_env_bat(commands, workdir):
    bat = workdir / "_build.bat"
    lines = [
        "@echo off",
        'for /d %%v in ("%ProgramFiles%\\Microsoft Visual Studio\\*") do for /d %%e in ("%%v\\*") do if exist "%%e\\VC\\Auxiliary\\Build\\vcvars64.bat" set "VCVARS=%%e\\VC\\Auxiliary\\Build\\vcvars64.bat"',
        'if not defined VCVARS (echo MSVC not found & exit /b 1)',
        'call "%VCVARS%" >nul 2>nul',
        f'cd /d "{workdir}"',
    ] + [c + " || exit /b 1" for c in commands]
    bat.write_text("\r\n".join(lines) + "\r\n", encoding="ascii")
    return subprocess.run(["cmd", "/c", str(bat)], capture_output=True, text=True)


def build(name):
    out = BUILDS / name
    if out.exists():
        shutil.rmtree(out)
    (out / "rt").mkdir(parents=True)
    (out / "vrs").mkdir(parents=True)
    cargo = shutil.which("cargo") or str(Path.home() / ".cargo/bin/cargo.exe")
    log("cargo build: tracer, bridge, harness")
    r = subprocess.run([cargo, "build", "--release", "-p", "basis-rt-tracer", "-p", "basis-rt-bridge", "-p", "basis-rt-harness"], cwd=RT_SRC, capture_output=True, text=True)
    if r.returncode != 0:
        log(r.stderr[-4000:])
        raise SystemExit("cargo build failed")
    target = RT_SRC / "target/release"
    for f in ["BasisRayTracer.exe", "basis-rt-harness.exe", "BasisRayTracingBridge.dll"]:
        shutil.copy2(target / f, out / "rt" / f)
    if RT_D3D12.exists():
        shutil.copytree(RT_D3D12, out / "rt/D3D12")
    log("msvc build: VRS plugin, stereo harness")
    work = out / "vrs"
    r = msvc_env_bat([
        f'cl /nologo /O2 /MT /W4 /GS /LD /Fo"{work}\\\\" "{VRS_SRC}\\BasisVrsD3D11.cpp" /link /DLL /OUT:"{work}\\BasisVrsD3D11.dll"',
        f'cl /nologo /O2 /MT /W4 /EHsc /Fo"{work}\\\\" "{VRS_SRC}\\harness_stereo.cpp" /link /OUT:"{work}\\harness_stereo.exe"',
    ], work)
    if r.returncode != 0:
        log(r.stdout[-4000:] + r.stderr[-2000:])
        raise SystemExit("msvc build failed")
    for f in work.glob("*.obj"):
        f.unlink()
    git = subprocess.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    dirty = subprocess.run(["git", "-C", str(REPO), "status", "--porcelain", "--", str(RT_SRC), str(VRS_SRC)], capture_output=True, text=True).stdout.strip()
    (out / "build.json").write_text(json.dumps({"name": name, "time": datetime.datetime.now().isoformat(timespec="seconds"), "git": git, "uncommitted_native_changes": bool(dirty)}, indent=2))
    log(f"built {out}")


def snapshot(source, name):
    src, dst = BUILDS / source, BUILDS / name
    if not src.exists():
        raise SystemExit(f"no build {source}")
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    log(f"snapshot {source} -> {name}")


# ---------------------------------------------------------------------------------------- guards ---

def gpu_risky_processes():
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe' or Name='vrserver.exe' or Name='vrcompositor.exe'\" | ForEach-Object { $_.Name + ' ' + $_.CommandLine }"],
                       capture_output=True, text=True)
    found = []
    for line in r.stdout.splitlines():
        low = line.lower()
        if "unity.exe" in low and ("-batchmode" in low or "assetimportworker" in low):
            continue
        if line.strip():
            found.append(line.split(" ")[0])
    return sorted(set(found))


def driver_events(since):
    stamp = since.strftime("%Y-%m-%dT%H:%M:%S")
    r = subprocess.run(["powershell", "-NoProfile", "-Command",
                        f"Get-WinEvent -FilterHashtable @{{LogName='System'; ProviderName='nvlddmkm'; StartTime=[datetime]'{stamp}'}} -ErrorAction SilentlyContinue | "
                        "ForEach-Object { '{0} {1} {2}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.Id, (($_.Properties | Select-Object -Skip 1 -First 1).Value) }"],
                       capture_output=True, text=True)
    return [l.strip() for l in r.stdout.splitlines() if l.strip()]


def capture_stacks(pid, path):
    cdb = next((c for c in CDB_CANDIDATES if c.exists()), None)
    if cdb is None:
        path.write_text("cdb not found; install WinDbg to capture hang stacks\n")
        return
    r = subprocess.run([str(cdb), "-p", str(pid), "-pv", "-c", "~*kc 30; q"], capture_output=True, text=True, timeout=180)
    path.write_text(r.stdout)


def kill_tree(pid):
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)], capture_output=True)


# ----------------------------------------------------------------------------------------- runs ----

RESULT_RE = re.compile(r"^RESULT (.*)$", re.M)
BAD_TEXT = [("device removed", re.compile(r"device removed|DEVICE REMOVED|887a0005", re.I)),
            ("tracer stopped", re.compile(r"tracer stopped|tracer failed to start")),
            ("panic", re.compile(r"panicked at")),
            ("retarget failed", re.compile(r"retarget failed"))]


def parse_result(text):
    m = RESULT_RE.findall(text)
    if not m:
        return None
    out = {}
    for pair in " ".join(m).split():
        if "=" in pair:
            k, v = pair.split("=", 1)
            try:
                out[k] = float(v)
            except ValueError:
                out[k] = v
    return out


def compare(a, op, b):
    return {"<": a < b, "<=": a <= b, "==": a == b, ">=": a >= b, ">": a > b}[op]


def run_scenario(s, build_dir, run_dir, label):
    frames = s["frames"]
    timeout = s.get("timeout", DEFAULT_TIMEOUT)
    log_path = run_dir / f"{s['name']}.{label}.log"
    env = dict(os.environ)
    for k in list(env):
        if k in ("STOPTEST", "NOSTALL", "EMBED", "DIRECT", "API", "VIEWS", "W", "H", "RESIZE", "RETARGET", "RETARGETFLIP", "TEXTURES", "CONTEXTS", "TRACER", "SAMPLES") or k.startswith("BASIS_RT_TEST_"):
            del env[k]
    env.update(s.get("env", {}))
    if s["kind"] == "rt":
        rt = build_dir / "rt"
        tracer = rt / f"rt_{label}.exe"
        if not tracer.exists():
            shutil.copy2(rt / "BasisRayTracer.exe", tracer)
        cmd = [str(rt / "basis-rt-harness.exe"), tracer.as_posix(), str(run_dir / f"{s['name']}.{label}.ppm"), str(frames)]
        cwd = rt
    else:
        vrs = build_dir / "vrs"
        cmd = [str(vrs / "harness_stereo.exe"), str(vrs / "BasisVrsD3D11.dll"), str(frames)]
        cwd = vrs
    time.sleep(COOLDOWN_SECONDS)
    started = datetime.datetime.now().replace(microsecond=0) - datetime.timedelta(seconds=1)
    t0 = time.time()
    proc = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=open(log_path, "w", encoding="utf-8", errors="replace"), stderr=subprocess.STDOUT)
    hung = False
    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        hung = True
        capture_stacks(proc.pid, run_dir / f"{s['name']}.{label}.stacks.txt")
        kill_tree(proc.pid)
        proc.wait()
    seconds = time.time() - t0
    text = log_path.read_text(encoding="utf-8", errors="replace")
    issues = []
    if hung:
        issues.append(f"HANG after {timeout}s (stacks saved)")
    elif proc.returncode != 0:
        issues.append(f"exit code {proc.returncode:#x}" if proc.returncode < 0 or proc.returncode > 255 else f"exit code {proc.returncode}")
    for name, rx in BAD_TEXT:
        if rx.search(text):
            issues.append(name)
    events = driver_events(started)
    if events:
        issues.append("GPU DRIVER EVENT: " + "; ".join(events))
    result = parse_result(text)
    if result is None and not hung:
        issues.append("no RESULT line")
    if result:
        for metric, op, value in s.get("expect", []):
            if metric not in result:
                issues.append(f"missing metric {metric}")
            elif not compare(result[metric], op, value):
                issues.append(f"{metric}={result[metric]:g} (want {op} {value})")
    return dict(scenario=s["name"], build=label, status="PASS" if not issues else "FAIL", seconds=round(seconds, 1),
                issues=issues, result=result or {}, gpu_events=events, log=log_path.name)


def baseline_checks(cur, base):
    """Regressions only meaningful against a baseline run of the same scenario."""
    issues = []
    if cur["status"] != "PASS" or base["status"] != "PASS":
        return issues
    a, b = cur["result"], base["result"]
    if "mean_lum" in a and "mean_lum" in b and b["mean_lum"] > 1e-6:
        drift = abs(a["mean_lum"] - b["mean_lum"]) / b["mean_lum"]
        if drift > LUMINANCE_TOLERANCE:
            issues.append(f"mean luminance drift {drift:.1%} vs baseline")
    for key in ("rt_stutters", "trip_stutters", "frame_stutters"):
        if key in a and key in b and a[key] > b[key] + 3:
            issues.append(f"{key} {a[key]:g} vs baseline {b[key]:g}")
    for key in ("rt_p99", "frame_p99"):
        if key in a and key in b and a[key] > b[key] * 1.5 + 2:
            issues.append(f"{key} {a[key]:.2f} ms vs baseline {b[key]:.2f} ms")
    return issues


KEY_METRICS = ["rt_med", "rt_p99", "rt_max", "rt_stutters", "blocked_waits", "unfinished_skips", "fetch_misses", "nonfinite", "mean_lum",
               "frame_med", "frame_p99", "frame_stutters", "uncovered", "failures"]


def run(args):
    build_dir = BUILDS / args.build
    if not build_dir.exists():
        raise SystemExit(f"no build '{args.build}', run: python {Path(__file__).name} build --name {args.build}")
    base_dir = BUILDS / args.baseline if args.baseline else None
    if base_dir and not base_dir.exists():
        raise SystemExit(f"no baseline build '{args.baseline}'")
    tiers = TIERS[args.tier]
    chosen = [s for s in SCENARIOS if s["tier"] in tiers and (not args.only or args.only in s["name"])]
    risky = gpu_risky_processes()
    run_dir = RESULTS / datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True)
    log(f"results: {run_dir}")
    rows = []
    for s in chosen:
        if s.get("gpu_risk") and risky and not args.allow_gpu_risk:
            log(f"SKIP {s['name']}: GPU reset risk while {', '.join(risky)} running (use --allow-gpu-risk)")
            rows.append(dict(scenario=s["name"], build=args.build, status="SKIP", seconds=0, issues=[f"gpu risk with {', '.join(risky)} open"], result={}, gpu_events=[], log=""))
            continue
        for rep in range(args.repeat):
            targets = [(args.build, build_dir)] + ([(args.baseline, base_dir)] if base_dir else [])
            by_label = {}
            for label, d in targets:
                r = run_scenario(s, d, run_dir, label + (f"-{rep}" if args.repeat > 1 else ""))
                r["build"] = label
                by_label[label] = r
                rows.append(r)
                metrics = " ".join(f"{k}={r['result'][k]:g}" for k in KEY_METRICS if k in r["result"])
                log(f"{r['status']:4} {s['name']:28} {label:10} {r['seconds']:6.1f}s  {metrics}  {'; '.join(r['issues'])}")
                if r["gpu_events"]:
                    log("GPU driver reported an error; stopping before anything else runs on a reset GPU.")
                    write_report(run_dir, rows, args)
                    return 2
            if base_dir:
                extra = baseline_checks(by_label[args.build], by_label[args.baseline])
                if extra:
                    by_label[args.build]["issues"] += extra
                    by_label[args.build]["status"] = "FAIL"
                    log(f"     regression vs {args.baseline}: {'; '.join(extra)}")
    return write_report(run_dir, rows, args)


def write_report(run_dir, rows, args):
    (run_dir / "summary.json").write_text(json.dumps(rows, indent=2))
    lines = [f"# GPU tests {run_dir.name}", "", f"build `{args.build}`" + (f", baseline `{args.baseline}`" if args.baseline else "") + f", tier `{args.tier}`", "",
             "| scenario | build | status | time | key metrics | issues |", "|---|---|---|---|---|---|"]
    for r in rows:
        metrics = ", ".join(f"{k} {r['result'][k]:g}" for k in KEY_METRICS if k in r["result"])
        lines.append(f"| {r['scenario']} | {r['build']} | {r['status']} | {r['seconds']}s | {metrics} | {'; '.join(r['issues'])} |")
    notes = [s for s in SCENARIOS if s.get("note") and any(r["scenario"] == s["name"] for r in rows)]
    if notes:
        lines += ["", "## What the scenarios exercise", ""] + [f"- **{s['name']}**: {s['note']}" for s in notes]
    (run_dir / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    failed = [r for r in rows if r["status"] == "FAIL" and r["build"] == args.build]
    log(f"\n{len(rows)} runs, {len(failed)} failed for build '{args.build}'. Report: {run_dir / 'summary.md'}")
    return 1 if failed else 0


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build"); b.add_argument("--name", default="current")
    sn = sub.add_parser("snapshot"); sn.add_argument("--name", required=True); sn.add_argument("--source", default="current")
    r = sub.add_parser("run")
    r.add_argument("--build", default="current"); r.add_argument("--baseline")
    r.add_argument("--tier", choices=list(TIERS), default="full"); r.add_argument("--only")
    r.add_argument("--allow-gpu-risk", action="store_true"); r.add_argument("--repeat", type=int, default=1)
    sub.add_parser("list")
    args = p.parse_args()
    if args.cmd == "build":
        build(args.name)
    elif args.cmd == "snapshot":
        snapshot(args.source, args.name)
    elif args.cmd == "list":
        for s in SCENARIOS:
            log(f"{s['tier']:6} {s['kind']:3} {s['name']:28} {'GPU RISK ' if s.get('gpu_risk') else ''}{s.get('note', '')}")
    else:
        sys.exit(run(args))


if __name__ == "__main__":
    main()
