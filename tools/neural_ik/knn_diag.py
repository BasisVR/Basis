"""Is the pole INPUT-limited? A k-NN oracle predicts each held-out frame's swivel from the circular mean
of its nearest TRAIN-frame neighbors. If even this memorizing predictor can't beat ~3.5% solver err,
then hand-position alone does not determine the elbow swivel and we need a richer input (velocity)."""
import numpy as np
from sklearn.neighbors import NearestNeighbors
import train_swivel as T
import solver_metric as S

feat, phi, rad, clip, E = S.load_v2("arm")
U, L = S.per_clip_UL(feat, E, clip)
tr, va, _ = T.clip_split(clip, 4)

base = S.solver_err(T.benddir_from_phi(feat[va], T.poly_swivel(feat, T.ARM_SIN, T.ARM_COS)[va]), feat[va], E[va], U[va], L[va])
floor = S.solver_err(T.benddir_from_phi(feat[va], phi[va]), feat[va], E[va], U[va], L[va])
print(f"held-out: poly {100*base:.3f}%   truth-pole floor {100*floor:.3f}%")

nn = NearestNeighbors(n_neighbors=64).fit(feat[tr])
_, idx = nn.kneighbors(feat[va])
for k in (1, 8, 32, 64):
    s = np.mean(np.sin(phi[tr][idx[:, :k]]), 1)
    c = np.mean(np.cos(phi[tr][idx[:, :k]]), 1)
    pred = np.arctan2(s, c)
    err = S.solver_err(T.benddir_from_phi(feat[va], pred), feat[va], E[va], U[va], L[va])
    print(f"  k-NN k={k:3d} (memorizing oracle) held-out solver err: {100*err:.3f}%")
