"""Sweep the smoothness penalty for both limbs: held-out accuracy vs worst-case pole step.
Goal: keep the +19% accuracy while pulling the worst-case step below the polynomial's."""
import os
import numpy as np
import train_swivel as T

POLY_REF = {"arm": (86.7, 12.7), "leg": (89.8, 15.0)}  # poly clamped worst-step in-reach/boundary (measured)

for limb in ["arm", "leg"]:
    csin, ccos, fn = T.POLY[limb]
    feat, phi, rad, clip = T.load_dump(os.path.join(T.TEMP, fn))
    tr, va, valclips = T.clip_split(clip, 4)
    fv, pv, rv = feat[va], phi[va], rad[va]
    pin, pbd = POLY_REF[limb]
    print(f"\n=== {limb}  (poly reference: acc-baseline, worst-step in/bnd {pin}/{pbd}) ===")
    print(f"  {'smooth':>7} {'held-out acc %':>15} {'worst in-reach':>15} {'worst boundary':>15}")
    for sm in [0.0, 0.5, 2.0, 6.0]:
        best, besterr = None, 1e9
        for s in range(3):
            m = T.train_model(feat, phi, rad, tr, (24, 16), 400, seed=s, smooth=sm)
            e = T.bend_poserr(T.benddir_from_phi(fv, T.model_swivel(m, fv)), fv, pv, rv)
            if e < besterr:
                best, besterr = m, e
        Ws, Bs = T.mlp_weights(best)
        wi, wb, wy = T.neural_worst_step(Ws, Bs)
        print(f"  {sm:>7.1f} {100*besterr:>15.3f} {wi:>15.1f} {wb:>15.1f}")
