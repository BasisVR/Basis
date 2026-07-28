# neural_ik — learned swivel-pole models for fullbody IK

> **BOTTOM LINE (2026-07-18, after a deep pass).** The hand-tuned polynomials are **essentially optimal**
> for hand-position-only input. The winning model is a **bounded residual** —
> `swivel = BasisArmSwivelModel.SwivelRad(t) + 25°·tanh(MLP(t))` (`solver_residual.py`) — Unity-verified at
> **3.61% vs the poly's 3.62%, 0 pops**: a real but MARGINAL win (~0.06 mm). Everything else failed and taught
> the lesson: the rad-weighted bend-angle **proxy lied** (+13–22% offline, worse in Unity); a free MLP trained
> against the differentiable **solver port** looked +11% cross-validated but was a **19% / 65-pop disaster in
> Unity** (the per-frame port is blind to temporal pops, so training found sharp poles that game it); **velocity
> carries no swivel signal** (k-NN floor ~3.7%). The residual works ONLY because a bounded correction on the
> smooth poly stays smooth on the trajectory — the one regime the port is faithful in, so the offline gain
> transfers. **Verify pole models in Unity (temporal), never on a per-frame surrogate.** Default is OFF
> (`UseNeuralPole=false`); the bigger headroom is the live gain-capped path (`ElbowField` 7.13%), which needs
> `BasisElbowSwingCapCore` ported. Method: `solver_metric.py` (validated port) → `solver_residual.py` (winner);
> the batch-mode Unity loop is `Unity.exe -runTests -batchmode -testFilter BasisMocapMotionQualityTests`.

Small MLPs that replace the polynomial elbow/knee **swivel-pole** predictors
(`BasisArmSwivelModel`, `BasisLegSwivelModel`, `BasisElbowFieldModel`) with drop-ins that predict the
same `(sin, cos)` swivel from the same features — **more accurate AND smoother**, and inline into the
Burst job as plain constants (no new runtime dependency).

Shipped models (`IK/BasisArmSwivelNeuralModel.cs`, `IK/BasisLegSwivelNeuralModel.cs`, 3→24→16→2 tanh)
are wired into the mocap harness as the `NeuralSwivel` hint source — run
`BasisMocapMotionQualityTests.HintSources_Compared_ForMotionQualityNotJustAccuracy` to A/B them in-Unity.

## Two rules that must never be broken

1. **Fit on the harness's own dumped features, never re-derive them.** A mismatch in any one of
   handedness / body-frame / mirror produces *confident garbage* (this project has been burned twice:
   3.77% offline vs 31% in-harness). The dump is the contract; the runtime and the trainer read the same
   numbers. The codegen carries a **parity guard** (float32 numpy replicate vs torch, ~1e-4 deg) so the
   emitted C# provably equals the trained network.
2. **Accuracy is blind to smoothness.** The mean-error proxy cannot see a worst-case pole *flip* (a big
   swivel jump for a 2-3 mm hand step). Seeds that are accuracy-optimal are often sharp — one leg seed hit
   a 163° flip. Codegen therefore scores every seed on accuracy **and** worst radial step and picks the
   smoothest of the accurate ones, with a smoothness penalty (`--smooth`) on top. `BasisSwivelOverreachTests`
   gates this permanently.

## Workflow

1. **Dump** from Unity: run `BasisMocapMotionQualityTests.HintSources_Compared...`. It writes (schema
   `clip,side,x,y,z,phi,rad,ex,ey,ez`):
   - `%TEMP%/basis_swivel_train.csv`  (arm/elbow, ~55k rows)
   - `%TEMP%/basis_leg_train.csv`     (leg/knee, ~191k rows)
   `x,y,z` = mirrored, body-frame, limb-normalized (hand−shoulder); `phi` = mirrored true swivel;
   `rad` = reachable-circle radius = per-sample weight; `ex,ey,ez` = raw elbow/knee position (for the
   position-target model, schema v2).

2. **Validate the frame** (reproduces the shipped polynomials' documented error to 3 s.f.):
   `python train_swivel.py validate --limb arm`
3. **Compare** vs the shipped baseline on held-out CLIPS: `python train_swivel.py train --limb arm --widths 24,16`
4. **Scaling study** ("less vs more data"): `python train_swivel.py scale --limb arm`
5. **Over-reach probe** (behaviour past |d|=1, offline): `python train_swivel.py overreach --limb arm`
6. **Codegen** the Burst C# (multi-seed, smoothness-aware, parity-checked):
   ```
   python train_swivel.py codegen --limb arm --widths 24,16 --epochs 500 --smooth 2.0 \
       --out "../../Basis/Packages/com.basis.framework/IK/BasisArmSwivelNeuralModel.cs"
   python train_swivel.py codegen --limb leg --widths 24,16 --epochs 500 --smooth 2.0 \
       --out "../../Basis/Packages/com.basis.framework/IK/BasisLegSwivelNeuralModel.cs"
   ```

Requires `torch` (CUDA optional), `numpy`, `sklearn`.

## Results (2026-07-18, CMU corpus, held-out clips)

Metric = proxy pole position error (% of limb) — reproduces the harness's "err %arm" to 3 s.f.
Worst-step = worst radial swivel change (deg) per ~2-3 mm hand step (smoothness; the accuracy proxy is blind).

| joint | shipping baseline | neural | worst-step: poly → neural |
|---|---|---|---|
| elbow | ElbowField 4.20% | **3.67%** (+13%) | in-reach 86.7 → **7.4**, boundary 12.7 → **2.9** |
| knee  | LegSwivelModel 4.16% | **3.23%** (+22%) | in-reach 89.8 → **5.5**, boundary 15.0 → **5.2** |

So the neural pole is **~10× smoother** worst-case *and* more accurate, and (unlike the polynomial, which is
"a random number generator" past reach) stays bounded even without the clamp — proportion mismatch that
drives the input past reach degrades gracefully instead of flipping.

**Scaling ("less vs more data"):** motion-diversity-limited, not frame-limited — frame count saturates
~25% of the corpus; clip count is still improving at all 20 clips. More diverse *motions* is the axis that
pays, which is what the AMASS ceiling below measures.

### Per-clip robustness — the honest caveat (`perclip` mode)

The aggregate held-out numbers above hide variance. Leave-one-CLIP-out (`python train_swivel.py perclip
--limb arm`) trains on 19 clips and scores the 20th against the shipping baseline:

- **Elbow: neural beats ElbowField on 11/20 clips**, some strongly (201: −38%), but regresses on 9/20, a
  few badly (clip 704 +137%, 7705 +109%). **Knee: 10/20**, worst clip 14117 +93%.

This is the bias-variance tradeoff: the higher-capacity net has lower *average* error but higher variance on
motions it wasn't trained on. Since real VR users do motions outside CMU's 20 clips, it will beat the
polynomial on many real motions and lose on some. **Regularization does NOT fix it** — a smaller net with 100×
weight decay was *worse* (9/20, worst +250%): it underfits the diverse corpus and does even worse on the hard
OOD clips. The regressions are a **data-diversity** limit, not a tuning miss, so the fix is more diverse
training motions (AMASS), exactly what the scaling study predicted. Ship the accurate+smooth baseline, and
treat the per-clip spread as the reason to run the AMASS ceiling before trusting it broadly. The in-Unity
motion-quality suite (pops/jitter on real motion) and a headset session are the deciding tests.

## Over-reach ≡ body-proportion mismatch (the key reframing)

The pole (bend direction) is proportion-*independent* given the swivel — bone lengths only change how far
along it the joint sits, which the two-bone solver computes, not the model. What proportion mismatch does is
**shift the input past reach** (`|d|>1`), the region the mocap corpus structurally cannot contain (the hand
is always on the limb). So over-reach, under-reach, and "different body proportions" collapse to one thing:
behave well past `|d|=1`. That is exactly what `BasisSwivelOverreachTests` pins. NOTE: this is a better
*pole*, not full-body lean/shrug compensation for genuinely-unreachable targets (that is a full-body
problem, out of swivel scope).

## Position-target model (`position` mode) — the ElbowField-on-steroids path

The angle formulation beats ElbowField on accuracy but inherits the vertical-arm reference singularity
ElbowField was built to escape (smoothness selection tames it; it does not remove it). The **position model**
predicts a 3-vector and projects onto the circle (like ElbowField) — no angle reference, no hairy-ball
singularity. It needs the v2 dump (`ex,ey,ez`), which is why the harness now emits it.

The trainer + codegen path is BUILT and compile-verified:
```
python train_swivel.py position --limb arm --widths 24,16 \
    --out "../../Basis/Packages/com.basis.framework/IK/BasisArmElbowNeuralFieldModel.cs"
```
It scores against a **singularity-free** ground truth (the true bend dir from the raw position, not the phi
parameterization) and emits `{Cls}.Elbow(tipLocal) -> float3`, used as
`BasisElbowFieldModel.BendDirection(tipLocal, Elbow(tipLocal), out c)`. On a v1 dump it trains on the
RECONSTRUCTED target as a stand-in (loudly warned; no singularity-free benefit) purely so the codegen path
is exercisable — the checked-in `IK/BasisArmElbowNeuralFieldModel.cs` is that EXPERIMENTAL stand-in, unwired.
Regenerate the dump (v2) and re-run to get the real model, then A/B it as a new hint source if it wins.

## AMASS ceiling (measuring how much diverse data buys) — FRAME-SAFE procedure

AMASS is non-commercial, so use it only to measure the ceiling, never to ship weights (keep shipped weights
on the permissive CMU corpus). Do NOT re-derive the feature frame in a Python SMPL pipeline — that is the
confident-garbage trap. Instead route AMASS **through the harness** so the frame is identical by construction:

1. Download AMASS (SMPL-H .npz) and convert the sequences to BVH (any AMASS→BVH exporter; the SMPL skeleton
   maps cleanly onto the CMU/Biovision joint names `BasisBvhLoader` already understands).
2. Drop the .bvh into a new `Tests/MocapCorpus~/amass/` folder (the trailing `~` keeps it out of the asset db).
3. Point the dump test at that folder (small `LoadCorpus`/path change — ask before wiring, it is speculative
   until you have the data), run it → the harness does SMPL-consistent FK + the exact body frame and appends
   AMASS rows to the CSV, directly comparable to the CMU numbers.
4. `python train_swivel.py train --limb arm` on the AMASS-augmented dump → the ceiling. The scaling study
   predicts it should beat CMU-only, since clip diversity has not plateaued.
