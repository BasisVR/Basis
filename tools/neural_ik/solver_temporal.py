"""Train against the solver elbow error AND a TEMPORAL pop penalty (the thing the per-frame port was blind
to). Uses consecutive-frame pairs from the dump: the elbow must not move faster than ~1.5x the hand between
frames -- forbidding the sharp/flipping poles that scored 3.4% per-frame but 19% + 65 pops in Unity.

Reports held-out solver err AND worst-step; the WIN condition is: in-sample <= poly 3.62% with a LOW
worst-step (smooth), verified in Unity."""
import argparse
import os
import numpy as np
import torch
import train_swivel as T
import solver_metric as S
import solver_train as ST

DEV = T.DEVICE


def load_pairs():
    r = np.genfromtxt(os.path.join(T.TEMP, "basis_swivel_train.csv"), delimiter=",", names=True, dtype=None, encoding="utf-8")
    feat = T.clamp_domain(np.stack([r["x"], r["y"], r["z"]], 1).astype(np.float64))
    phi = r["phi"].astype(np.float64); rad = r["rad"].astype(np.float64)
    clip = r["clip"].astype(str); side = r["side"].astype(str)
    E = np.stack([r["ex"], r["ey"], r["ez"]], 1).astype(np.float64)
    cur, prev = [], []
    for c in np.unique(clip):
        for sd in ("L", "R"):
            idx = np.where((clip == c) & (side == sd))[0]
            for k in range(1, len(idx)):
                if np.linalg.norm(feat[idx[k]] - feat[idx[k - 1]]) < 0.15:   # skip pass-boundary/gap jumps
                    cur.append(idx[k]); prev.append(idx[k - 1])
    return feat, phi, rad, clip, E, np.array(cur), np.array(prev)


def train(feat, E, U, L, cur, prev, trmask, widths, epochs, seed, smooth, wd, pop, gain=1.5):
    keep = trmask[cur] & trmask[prev]
    cur, prev = cur[keep], prev[keep]
    Xc = torch.tensor(ST.clamp_feat(feat[cur]), dtype=torch.float32, device=DEV)
    Xp = torch.tensor(ST.clamp_feat(feat[prev]), dtype=torch.float32, device=DEV)
    Ec = torch.tensor(E[cur], dtype=torch.float32, device=DEV)
    Uc = torch.tensor(U[cur], dtype=torch.float32, device=DEV); Lc = torch.tensor(L[cur], dtype=torch.float32, device=DEV)
    Up = torch.tensor(U[prev], dtype=torch.float32, device=DEV); Lp = torch.tensor(L[prev], dtype=torch.float32, device=DEV)
    dhand = (Xc - Xp).norm(dim=1)
    torch.manual_seed(seed)
    m = ST.MLP(widths).to(DEV)
    opt = torch.optim.Adam(m.parameters(), lr=2e-3, weight_decay=wd)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, epochs)
    n, bs = len(Xc), 8192
    for ep in range(epochs):
        perm = torch.randperm(n, device=DEV)
        for b in range(0, n, bs):
            j = perm[b:b + bs]
            opt.zero_grad()
            ec = ST.t_solver_elbow(Xc[j], m(Xc[j]), Uc[j], Lc[j])
            ep_ = ST.t_solver_elbow(Xp[j], m(Xp[j]), Up[j], Lp[j])
            acc = ((ec - Ec[j]).pow(2).sum(1) + 1e-9).sqrt().mean()
            delbow = (ec - ep_).norm(dim=1)
            popt = torch.relu(delbow - gain * dhand[j]).pow(2).mean()   # elbow may not outrun the hand
            loss = acc + pop * popt
            if smooth > 0:
                loss = loss + smooth * (m(Xc[j] + torch.randn_like(Xc[j]) * 0.03) - m(Xc[j])).pow(2).mean()
            loss.backward()
            opt.step()
        sched.step()
    return m


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--widths", default="64,64"); ap.add_argument("--epochs", type=int, default=500)
    ap.add_argument("--seeds", type=int, default=8); ap.add_argument("--smooth", type=float, default=0.3)
    ap.add_argument("--wd", type=float, default=1e-4); ap.add_argument("--pop", type=float, default=5.0)
    ap.add_argument("--out", default="")
    a = ap.parse_args()
    widths = tuple(int(w) for w in a.widths.split(","))
    feat, phi, rad, clip, E, cur, prev = load_pairs()
    U, L = S.per_clip_UL(feat, E, clip)
    tr, va, _ = T.clip_split(clip, 4)
    poly_all = S.solver_err(T.benddir_from_phi(feat, T.poly_swivel(feat, T.ARM_SIN, T.ARM_COS)), feat, E, U, L)
    print(f"device={DEV}  pairs={len(cur)}  shipped poly in-sample {100*poly_all:.3f}% (Unity 3.62)")

    cands = []
    for s in range(a.seeds):
        m = train(feat, E, U, L, cur, prev, tr, widths, a.epochs, s, a.smooth, a.wd, a.pop)
        e = S.solver_err(T.benddir_from_phi(feat[va], ST.model_phi(m, feat[va])), feat[va], E[va], U[va], L[va])
        wi, wb, _ = T.neural_worst_step(*T.mlp_weights(m))
        print(f"    seed {s}: held-out {100*e:.3f}%  worst-step {max(wi,wb):5.1f}")
        if np.isfinite(e):
            cands.append((s, e, max(wi, wb)))
    # ship: train on ALL data, pick smoothest of the accurate
    allm = np.ones(len(feat), bool); ship = []
    for s in range(max(a.seeds, 8)):
        m = train(feat, E, U, L, cur, prev, allm, widths, a.epochs, 100 + s, a.smooth, a.wd, a.pop)
        ins = S.solver_err(T.benddir_from_phi(feat, ST.model_phi(m, feat)), feat, E, U, L)
        wi, wb, _ = T.neural_worst_step(*T.mlp_weights(m))
        if np.isfinite(ins) and max(wi, wb) < 12:
            ship.append((ins, max(wi, wb), m))
    if not ship:
        print("  no smooth ship model (all worst-step>12) -- raise --pop / --smooth"); raise SystemExit
    bi = min(x[0] for x in ship)
    ins, step, m = min([x for x in ship if x[0] <= bi * 1.05], key=lambda x: x[1])
    print(f"\n  SHIP in-sample {100*ins:.3f}% vs poly {100*poly_all:.3f}% ({(poly_all-ins)/poly_all*100:+.1f}%)  worst-step {step:.1f}")
    if a.out:
        Ws, Bs = T.mlp_weights(m)
        parity = float(np.max(np.abs(T.delta_angle_deg(T.mlp_forward_np(Ws, Bs, feat), ST.model_phi(m, feat)))))
        wi, wb, _ = T.neural_worst_step(Ws, Bs)
        T.emit_csharp(m, ins, poly_all, "SwivelModel (solver+pop, in-Unity)  ", ins, parity, (wi, wb), a.out, "arm")
        print(f"  wrote {a.out}  (parity {parity:.1e}, worst-step {wi:.1f}/{wb:.1f})")
