"""
Port of BasisArmSolveCore's elbow placement (raw-pole path) to numpy, so we can train against the
SOLVER's elbow error instead of the bend-angle proxy that misranked the models.

Raw-pole path (SwivelModel / NeuralSwivel: HintWeight, !HintIsTracker, perpendicular pole, no TipRotation):
  - full pole commit  -> final elbow bend direction == the pole (bind pose erased)
  - two-bone triangle solve with elbow angle clamped to [23, 170] deg (the 170 reach clamp)
  - BasisElbowAnatomyCore anatomy guard: elbow may not rise above max(shoulder, hand) + margin

VALIDATION GATE: this must reproduce the in-Unity SwivelModel elbow err (3.62%). If it does not, the
port is wrong and nothing built on it can be trusted (same discipline as the frame validation).
"""
import os
import numpy as np
import train_swivel as T

TEMP = T.TEMP
DEG = np.pi / 180.0


def load_v2(limb="arm"):
    path = os.path.join(TEMP, T.POLY[limb][2])
    feat, phi, rad, clip = T.load_dump(path)
    E = T.load_positions(path)
    if E is None:
        raise SystemExit("dump is v1 (no ex,ey,ez) -- regenerate in Unity first")
    return feat, phi, rad, clip, E


def per_clip_UL(feat, E, clip):
    """Rigid bone lengths (normalized). The solver uses fixed bind-pose U,L; estimate per clip by median."""
    U = np.linalg.norm(E, axis=1)
    L = np.linalg.norm(feat - E, axis=1)
    Uc = {c: np.median(U[clip == c]) for c in np.unique(clip)}
    Lc = {c: np.median(L[clip == c]) for c in np.unique(clip)}
    return np.array([Uc[c] for c in clip]), np.array([Lc[c] for c in clip])


def two_bone(dist, U, L):
    """Elbow along-axis distance `a` and circle radius `rho`, with the [23,170] deg elbow-angle clamp."""
    cos_th = np.clip((U * U + L * L - dist * dist) / (2 * U * L), -1, 1)
    theta = np.clip(np.arccos(cos_th), 23 * DEG, 170 * DEG)   # elbow angle clamp (the 170 reach clamp)
    d_eff = np.sqrt(np.maximum(U * U + L * L - 2 * U * L * np.cos(theta), 1e-12))
    cos_al = np.clip((U * U + d_eff * d_eff - L * L) / (2 * U * d_eff), -1, 1)
    alpha = np.arccos(cos_al)                                  # shoulder angle
    return U * np.cos(alpha), U * np.sin(alpha)               # a (along axis), rho (radius)


def anatomy_guard(elbow, hand, totalLen, up=np.array([0.0, 1.0, 0.0])):
    """BasisElbowAnatomyCore.GuardSwivelRad, applied to the elbow POSITION (shoulder = origin).
    Body-frame up = +y (approximates world PlayerUp for upright mocap)."""
    N = len(elbow)
    up = np.tile(up, (N, 1)).astype(np.float64)
    ac = hand
    acsq = np.sum(ac * ac, 1)
    acN = ac / np.sqrt(np.maximum(acsq, 1e-12))[:, None]
    ae = elbow
    aeProj = ae - acN * np.sum(ae * acN, 1, keepdims=True)
    radius = np.linalg.norm(aeProj, axis=1)
    upProj = up - acN * np.sum(up * acN, 1, keepdims=True)
    upLen = np.linalg.norm(upProj, axis=1)
    upN = upProj / np.maximum(upLen, 1e-12)[:, None]
    w = np.cross(acN, upN)

    handUp = np.sum(ac * up, 1)
    ceiling = np.maximum(handUp, 0.0)
    soft, hard = SoftMargin * totalLen, HardMargin * totalLen
    hSoft, hHard = ceiling + soft, ceiling + hard
    h = np.sum(ae * up, 1)                                     # elbow height above shoulder
    M = hHard - hSoft
    e = h - hSoft
    hGuard = hSoft + M * e / (M + e + 1e-12)

    fires = (h > hSoft) & (radius > 1e-5) & (upLen > 1e-5) & (M > 1e-5)
    along = np.sum(ae * acN, 1) * np.sum(acN * up, 1)
    denom = radius * upLen
    cG = np.clip((hGuard - along) / np.maximum(denom, 1e-12), -1, 1)
    poleDir = aeProj / np.maximum(radius, 1e-12)[:, None]
    s = np.sum(poleDir * w, 1)
    sG = np.where(s < 0, -1.0, 1.0) * np.sqrt(np.maximum(1 - cG * cG, 0))
    poleG = upN * cG[:, None] + w * sG[:, None]
    guarded_elbow = along[:, None] * acN + radius[:, None] * poleG   # a*axis + rho*poleGuarded
    return np.where(fires[:, None], guarded_elbow, elbow)


SoftMargin, HardMargin = 0.05, 0.15


def solver_elbow(feat, pole, U, L):
    dist = np.linalg.norm(feat, axis=1)
    axis = feat / np.maximum(dist, 1e-9)[:, None]
    a, rho = two_bone(np.clip(dist, 1e-4, U + L - 1e-4), U, L)
    elbow = a[:, None] * axis + rho[:, None] * pole
    return anatomy_guard(elbow, feat, U + L)


def solver_err_perframe(pole, feat, E, U, L):
    return np.linalg.norm(solver_elbow(feat, pole, U, L) - E, axis=1)


def solver_err(pole, feat, E, U, L):
    """Mean per-frame elbow position error (fraction of arm) -- the Unity ElbowErrFracArm metric."""
    return float(np.mean(solver_err_perframe(pole, feat, E, U, L)))


def optimal_pole(feat, E, U, L, ngrid=720):
    """The pole that MINIMIZES the solver's elbow error, found per-frame by 1D grid search over the swivel
    angle. This is the training target that directly optimizes the solver metric (it can even beat the TRUTH
    pole, since a non-truth pole can compensate for the clamp/guard)."""
    ax, uu, vv = T.circle_frame(feat)
    best_err = np.full(len(feat), 1e9)
    best_phi = np.zeros(len(feat))
    for g in range(ngrid):
        phi = -np.pi + 2 * np.pi * g / ngrid
        pole = uu * np.cos(phi) + vv * np.sin(phi)
        err = solver_err_perframe(pole, feat, E, U, L)
        take = err < best_err
        best_err[take] = err[take]
        best_phi[take] = phi
    return best_phi, best_err


if __name__ == "__main__":
    feat, phi, rad, clip, E = load_v2("arm")
    U, L = per_clip_UL(feat, E, clip)
    print(f"U+L per clip: mean {np.mean(U+L):.4f}  (should be ~1.0)")

    # truth pole (from dump phi) and the shipped SwivelModel polynomial pole
    pole_true = T.benddir_from_phi(feat, phi)
    pole_poly = T.benddir_from_phi(feat, T.poly_swivel(feat, T.ARM_SIN, T.ARM_COS))

    print("\n--- VALIDATION (must reproduce in-Unity SwivelModel elbow err = 3.62%) ---")
    print(f"  proxy (rad*bend-angle)       : {100*T.bend_poserr(pole_poly, feat, phi, rad):.3f}%   (the misleading one, ~3.45)")
    print(f"  SOLVER-ported (poly pole)    : {100*solver_err(pole_poly, feat, E, U, L):.3f}%   <- must be ~3.62")
    print(f"  SOLVER-ported (truth pole)   : {100*solver_err(pole_true, feat, E, U, L):.3f}%   (floor: perfect pole)")

    best_phi, best_err = optimal_pole(feat, E, U, L)
    print(f"\n  SOLVER-OPTIMAL pole (per-frame search): {100*np.mean(best_err):.3f}%")
    print(f"  -> headroom below the poly: {100*(solver_err(pole_poly, feat, E, U, L) - np.mean(best_err)):.3f}%"
          f"  (this is what a solver-trained model could win)")
