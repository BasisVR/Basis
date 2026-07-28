"""Does HAND VELOCITY carry swivel signal beyond hand position? Reconstruct per-(clip,side) velocity from
the dump's frame order (masking the double-pass boundary), then run the k-NN oracle with position vs
position+velocity. If pos+vel beats the ~3.7% position-only floor, velocity is worth adding."""
import os
import numpy as np
from sklearn.neighbors import NearestNeighbors
import train_swivel as T
import solver_metric as S

r = np.genfromtxt(os.path.join(T.TEMP, "basis_swivel_train.csv"), delimiter=",", names=True, dtype=None, encoding="utf-8")
feat = T.clamp_domain(np.stack([r["x"], r["y"], r["z"]], 1).astype(np.float64))
phi = r["phi"].astype(np.float64); rad = r["rad"].astype(np.float64)
clip = r["clip"].astype(str); side = r["side"].astype(str)
E = np.stack([r["ex"], r["ey"], r["ez"]], 1).astype(np.float64)

vel = np.zeros_like(feat)
for c in np.unique(clip):
    for sd in ("L", "R"):
        idx = np.where((clip == c) & (side == sd))[0]      # temporal order within each pass
        v = np.zeros((len(idx), 3)); v[1:] = feat[idx][1:] - feat[idx][:-1]
        mag = np.linalg.norm(v, axis=1)
        v[mag > np.percentile(mag, 99)] = 0                 # mask pass-boundary / gap jumps
        vel[idx] = v
print(f"velocity mag: median {np.median(np.linalg.norm(vel,1)):.4f}  p90 {np.percentile(np.linalg.norm(vel,axis=1),90):.4f}")

U, L = S.per_clip_UL(feat, E, clip)
tr, va, _ = T.clip_split(clip, 4)
fv, Ev, Uv, Lv = feat[va], E[va], U[va], L[va]


def knn(X, k=64):
    nn = NearestNeighbors(n_neighbors=k).fit(X[tr]); _, idx = nn.kneighbors(X[va])
    pred = np.arctan2(np.mean(np.sin(phi[tr][idx]), 1), np.mean(np.cos(phi[tr][idx]), 1))
    return S.solver_err(T.benddir_from_phi(fv, pred), fv, Ev, Uv, Lv)


print(f"  k-NN [pos only]        held-out: {100*knn(feat):.3f}%")
for sc in (3, 6, 10, 20):
    X = np.concatenate([feat, vel * sc], 1)
    print(f"  k-NN [pos + vel x{sc:2d}]     held-out: {100*knn(X):.3f}%")
