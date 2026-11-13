# GPU tests

One pipeline for the native GPU code Basis ships: the path tracer (tracer, bridge, and the Unity side
of its protocol, driven by `basis-rt-harness`) and the D3D11 variable rate shading plugin (driven by
`harness_stereo`). It runs on the real GPU and driver, outside Unity, so a broken build fails here
instead of crashing the editor.

```
python Tools/GpuTests/run.py build                      # tracer + bridge + harness (cargo), VRS plugin + harness (MSVC)
python Tools/GpuTests/run.py snapshot --name before     # keep a build for A/B before changing code
python Tools/GpuTests/run.py run --baseline before      # full tier, current build against the snapshot
python Tools/GpuTests/run.py run --tier smoke           # quick pass
python Tools/GpuTests/run.py list                       # every scenario and what it covers
```

Results go to `results/<time>/`: `summary.md`, `summary.json`, one log per run, and `*.stacks.txt`
with every thread's stack for any run that hung.

## What fails a run

- **Hang**: every run has a timeout. On expiry the pipeline attaches cdb (WinDbg) and saves all stacks.
- **GPU reset or fault**: any `nvlddmkm` event during the run (153 timeout/reset, 13 MMU fault). The
  pipeline stops at the first one, since everything after it would run on a reset GPU.
- **Crash**: non zero exit, device removed, tracer stopped, Rust panic.
- **Blocking waits**: Unity side GPU waits on a tracer frame that had not finished. In process, each
  one is a potential TDR; the late and spike scenarios make the tracer slow on purpose
  (`BASIS_RT_TEST_DELAY_MS`, `BASIS_RT_TEST_SPIKE=every:ms`, test only hooks in the tracer).
- **Stutter**: p99 and max frame time, and frames over twice the median (and 8 ms past it).
- **Bad output**: non finite pixels; mean luminance drift and stutter regressions against `--baseline`.
- **VRS misuse**: stale or undersized shading rate images, nothing bound, depth only passes, release
  while enabled, driver trim mid enable, eye texture flips (2644/3136) with the image recreated late.

## Tiers

- `smoke`: one run per path (copy, in process, D3D11, D3D12, stereo, VRS).
- `full` (default): plus late tracer, stutter spikes, render scale swaps, target reallocation, texture
  streaming, two contexts (mirror), Vulkan tracer, long VRS run.
- `stress`: VR sized targets and multi second tracer frames. These reset the GPU on a broken build, so
  they refuse to run while Unity or SteamVR is open unless `--allow-gpu-risk` is passed.

Add a scenario by appending to `SCENARIOS` in `run.py`: harness env vars, frame count, and the
`RESULT` metrics it must satisfy.
