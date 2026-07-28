"""
Train the pole MLP to minimize the SOLVER's elbow error directly (differentiable torch port of
BasisArmSolveCore's raw-pole path), instead of the rad-weighted bend-angle proxy that misranked it.

The numpy port in solver_metric.py is VALIDATED (poly pole -> 3.572% ~ Unity 3.62%); this torch port
mirrors it op-for-op, and we always report the final number with the validated numpy version.
"""
import argparse
import os
import numpy as np
import torch
import torch.nn as nn
import train_swivel as T
import solver_metric as S

DEV = T.DEVICE
DEG = np.pi / 180.0


def t_circle_frame(feat):
    ax = feat / feat.norm(dim=1, keepdim=True).clamp_min(1e-9)
    dn = torch.tensor([0.0, -1.0, 0.0], device=feat.device).expand_as(ax)
    uu = dn - ax * (dn * ax).sum(1, keepdim=True)
    uu = uu / uu.norm(dim=1, keepdim=True).clamp_min(1e-9)
    vv = torch.cross(ax, uu, dim=1)
    return ax, uu, vv


EPS = 1e-6   # arccos'(x) and sqrt'(0) are infinite at the ends; keep gradients finite


def t_two_bone(dist, U, L):
    cos_th = ((U * U + L * L - dist * dist) / (2 * U * L)).clamp(-1 + EPS, 1 - EPS)
    theta = torch.arccos(cos_th).clamp(23 * DEG, 170 * DEG)
    d_eff = (U * U + L * L - 2 * U * L * torch.cos(theta)).clamp_min(1e-8).sqrt()
    cos_al = ((U * U + d_eff * d_eff - L * L) / (2 * U * d_eff)).clamp(-1 + EPS, 1 - EPS)
    alpha = torch.arccos(cos_al)
    return U * torch.cos(alpha), U * torch.sin(alpha)


def t_guard(elbow, hand, totalLen):
    up = torch.tensor([0.0, 1.0, 0.0], device=elbow.device).expand_as(elbow)
    acN = hand / hand.norm(dim=1, keepdim=True).clamp_min(1e-9)
    aeProj = elbow - acN * (elbow * acN).sum(1, keepdim=True)
    radius = aeProj.norm(dim=1)
    upProj = up - acN * (up * acN).sum(1, keepdim=True)
    upLen = upProj.norm(dim=1)
    upN = upProj / upLen.clamp_min(1e-9).unsqueeze(1)
    w = torch.cross(acN, upN, dim=1)
    handUp = (hand * up).sum(1)
    ceiling = handUp.clamp_min(0.0)
    hSoft = ceiling + 0.05 * totalLen
    hHard = ceiling + 0.15 * totalLen
    h = (elbow * up).sum(1)
    M = hHard - hSoft
    e = h - hSoft
    hGuard = hSoft + M * e / (M + e + 1e-12)
    along = (elbow * acN).sum(1) * (acN * up).sum(1)
    denom = (radius * upLen).clamp_min(1e-9)
    cG = ((hGuard - along) / denom).clamp(-1 + EPS, 1 - EPS)
    poleDir = aeProj / radius.clamp_min(1e-9).unsqueeze(1)
    s = (poleDir * w).sum(1)
    sG = torch.where(s < 0, -1.0, 1.0) * (1 - cG * cG).clamp_min(1e-8).sqrt()
    guarded = along.unsqueeze(1) * acN + radius.unsqueeze(1) * (upN * cG.unsqueeze(1) + w * sG.unsqueeze(1))
    fires = (h > hSoft) & (radius > 1e-5) & (upLen > 1e-5)
    return torch.where(fires.unsqueeze(1), guarded, elbow)


def t_solver_elbow(feat, sc, U, L):
    """sc: (N,2) raw (sin,cos) -> normalized pole angle. Returns solved elbow (N,3), differentiable."""
    ax, uu, vv = t_circle_frame(feat)
    scn = sc / sc.norm(dim=1, keepdim=True).clamp_min(1e-6)
    pole = uu * scn[:, 1:2] + vv * scn[:, 0:1]     # cos on uu, sin on vv (matches benddir_from_phi)
    dist = torch.minimum(feat.norm(dim=1).clamp_min(1e-4), U + L - 1e-4)
    a, rho = t_two_bone(dist, U, L)
    elbow = a.unsqueeze(1) * ax + rho.unsqueeze(1) * pole
    return t_guard(elbow, feat, U + L)


class MLP(nn.Module):
    def __init__(self, widths=(32, 32)):
        super().__init__()
        layers, d = [], 3
        for w in widths:
            layers += [nn.Linear(d, w), nn.Tanh()]; d = w
        layers += [nn.Linear(d, 2)]
        self.net = nn.Sequential(*layers)

    def forward(self, x):
        return self.net(x)


def clamp_feat(feat):
    n = np.linalg.norm(feat, axis=1, keepdims=True)
    return np.where(n > 1, feat / n, feat)


def train_solver(feat, E, U, L, tr, widths, epochs, seed, smooth, wd=1e-5):
    Xn = clamp_feat(feat[tr])
    X = torch.tensor(Xn, dtype=torch.float32, device=DEV)
    Et = torch.tensor(E[tr], dtype=torch.float32, device=DEV)
    Ut = torch.tensor(U[tr], dtype=torch.float32, device=DEV)
    Lt = torch.tensor(L[tr], dtype=torch.float32, device=DEV)
    torch.manual_seed(seed)
    m = MLP(widths).to(DEV)
    opt = torch.optim.Adam(m.parameters(), lr=2e-3, weight_decay=wd)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, epochs)
    n, bs = len(X), 8192
    for ep in range(epochs):
        perm = torch.randperm(n, device=DEV)
        for b in range(0, n, bs):
            j = perm[b:b + bs]
            opt.zero_grad()
            elbow = t_solver_elbow(X[j], m(X[j]), Ut[j], Lt[j])
            loss = ((elbow - Et[j]).pow(2).sum(1) + 1e-9).sqrt().mean()   # mean L2 = the metric
            if smooth > 0:
                sc2 = m(X[j] + torch.randn_like(X[j]) * 0.03)
                loss = loss + smooth * (sc2 - m(X[j])).pow(2).mean()
            loss.backward()
            opt.step()
        sched.step()
    return m


@torch.no_grad()
def model_phi(m, feat):
    sc = m(torch.tensor(clamp_feat(feat), dtype=torch.float32, device=DEV)).cpu().numpy()
    return np.arctan2(sc[:, 0], sc[:, 1])


def swivel_of(feat, sc_model):
    sc = sc_model
    return np.arctan2(sc[:, 0], sc[:, 1])


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--widths", default="32,32")
    ap.add_argument("--epochs", type=int, default=400)
    ap.add_argument("--seeds", type=int, default=8)
    ap.add_argument("--smooth", type=float, default=0.3)
    ap.add_argument("--wd", type=float, default=1e-4)
    ap.add_argument("--out", default="")
    a = ap.parse_args()
    widths = tuple(int(w) for w in a.widths.split(","))

    feat, phi, rad, clip, E = S.load_v2("arm")
    U, L = S.per_clip_UL(feat, E, clip)
    tr, va, valclips = T.clip_split(clip, 4)
    print(f"device={DEV}  held-out clips {valclips}")

    rsin, rcos = T.fit_poly(feat, phi, rad, tr)
    base = S.solver_err(T.benddir_from_phi(feat[va], T.poly_swivel(feat, rsin, rcos)[va]), feat[va], E[va], U[va], L[va])
    poly_all = S.solver_err(T.benddir_from_phi(feat, T.poly_swivel(feat, T.ARM_SIN, T.ARM_COS)), feat, E, U, L)
    print(f"  poly refit held-out SOLVER err {100*base:.3f}%   |  shipped poly in-sample {100*poly_all:.3f}% (Unity 3.62)")

    cands = []
    for s in range(a.seeds):
        m = train_solver(feat, E, U, L, tr, widths, a.epochs, s, a.smooth, wd=a.wd)
        e = S.solver_err(T.benddir_from_phi(feat[va], model_phi(m, feat[va])), feat[va], E[va], U[va], L[va])
        wi, wb, _ = T.neural_worst_step(*T.mlp_weights(m))
        print(f"    seed {s}: held-out SOLVER {100*e:.3f}%   worst-step {max(wi,wb):5.1f}")
        if np.isfinite(e) and max(wi, wb) < 45:   # skip NaN and sharp (pole-flip) seeds
            cands.append((s, e, max(wi, wb)))
    best_acc = min(c[1] for c in cands)
    ok = [c for c in cands if c[1] <= best_acc * 1.10]
    best_seed, best_err, best_step = min(ok, key=lambda c: c[2])
    print(f"  -> seed {best_seed}: held-out {100*best_err:.3f}% vs poly {100*base:.3f}%  ({(base-best_err)/base*100:+.1f}%), "
          f"worst-step {best_step:.1f} (smoothest of accurate)")

    # ship model: multi-seed on ALL data, pick the SMOOTHEST of the accurate (the single retrain lands sharp).
    allm = np.ones(len(feat), bool)
    ship = []
    for s in range(max(a.seeds, 8)):
        m = train_solver(feat, E, U, L, allm, widths, a.epochs, 100 + s, max(a.smooth, 0.6), wd=a.wd)
        ins = S.solver_err(T.benddir_from_phi(feat, model_phi(m, feat)), feat, E, U, L)
        wi, wb, _ = T.neural_worst_step(*T.mlp_weights(m))
        if np.isfinite(ins) and max(wi, wb) < 30:
            ship.append((ins, max(wi, wb), m))
    bi = min(x[0] for x in ship)
    ins, step, m = min([x for x in ship if x[0] <= bi * 1.05], key=lambda x: x[1])
    print(f"\n  ALL-DATA neural in-sample SOLVER err {100*ins:.3f}%  vs shipped poly {100*poly_all:.3f}%  "
          f"({(poly_all-ins)/poly_all*100:+.1f}%), worst-step {step:.1f}  <- predicts Unity NeuralSwivel vs SwivelModel")
    if a.out:
        Ws, Bs = T.mlp_weights(m)
        parity = float(np.max(np.abs(T.delta_angle_deg(T.mlp_forward_np(Ws, Bs, feat), model_phi(m, feat)))))
        wi, wb, _ = T.neural_worst_step(Ws, Bs)
        T.emit_csharp(m, best_err, poly_all, "SwivelModel (solver err, in-Unity)   ", ins, parity, (wi, wb), a.out, "arm")
        print(f"  wrote {a.out}  (parity {parity:.1e}, worst-step {wi:.1f}/{wb:.1f})")
