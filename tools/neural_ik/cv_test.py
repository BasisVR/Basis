"""5-fold cross-validation over the 20 clips: the ROBUST test of whether the solver-trained MLP beats the
polynomial on generalization (new motions), removing the seed/split-luck that makes a single held-out
optimistic. Per fold: refit the poly on 16 clips + train MLP seeds on 16, score both on the held-out 4 via
the validated solver metric. Report MLP mean-over-seeds (no selection) and best-of-seeds, vs the poly."""
import numpy as np
import train_swivel as T
import solver_metric as S
import solver_train as ST

feat, phi, rad, clip, E = S.load_v2("arm")
U, L = S.per_clip_UL(feat, E, clip)
clips = np.array(sorted(np.unique(clip)))
rng = np.random.default_rng(0)
folds = np.array_split(rng.permutation(clips), 5)
SEEDS, W, WD, SM, EP = 4, (64, 64), 1e-4, 0.3, 500

poly_errs, mlp_mean_errs, mlp_best_errs, knn_errs = [], [], [], []
from sklearn.neighbors import NearestNeighbors
for fi, hold in enumerate(folds):
    va = np.isin(clip, hold)
    tr = ~va
    fv, Ev, Uv, Lv = feat[va], E[va], U[va], L[va]
    rsin, rcos = T.fit_poly(feat, phi, rad, tr)
    pe = S.solver_err(T.benddir_from_phi(fv, T.poly_swivel(feat, rsin, rcos)[va]), fv, Ev, Uv, Lv)
    # k-NN oracle (input floor for this fold)
    nn = NearestNeighbors(n_neighbors=64).fit(feat[tr]); _, idx = nn.kneighbors(fv)
    kp = np.arctan2(np.mean(np.sin(phi[tr][idx]), 1), np.mean(np.cos(phi[tr][idx]), 1))
    ke = S.solver_err(T.benddir_from_phi(fv, kp), fv, Ev, Uv, Lv)
    errs = []
    for s in range(SEEDS):
        m = ST.train_solver(feat, E, U, L, tr, W, EP, s, SM, wd=WD)
        errs.append(S.solver_err(T.benddir_from_phi(fv, ST.model_phi(m, fv)), fv, Ev, Uv, Lv))
    poly_errs.append(pe); mlp_mean_errs.append(np.mean(errs)); mlp_best_errs.append(np.min(errs)); knn_errs.append(ke)
    print(f"  fold {fi} (hold {list(hold)}): poly {100*pe:.2f}%  kNN {100*ke:.2f}%  MLP mean {100*np.mean(errs):.2f}% best {100*np.min(errs):.2f}%")

print(f"\n  5-fold MEAN:  poly {100*np.mean(poly_errs):.3f}%   k-NN {100*np.mean(knn_errs):.3f}%   "
      f"MLP mean-seed {100*np.mean(mlp_mean_errs):.3f}%   MLP best-seed {100*np.mean(mlp_best_errs):.3f}%")
print(f"  MLP mean-seed vs poly: {(np.mean(poly_errs)-np.mean(mlp_mean_errs))/np.mean(poly_errs)*100:+.1f}%   "
      f"MLP best-seed vs poly: {(np.mean(poly_errs)-np.mean(mlp_best_errs))/np.mean(poly_errs)*100:+.1f}%")
