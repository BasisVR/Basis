import os

import numpy as np

from . import reader, space


def splice_peers(cap):
    notes = []
    peers = os.path.join(cap.folder, "peers")
    if not os.path.isdir(peers):
        return notes
    for name in sorted(os.listdir(peers)):
        folder = os.path.join(peers, name)
        if not os.path.exists(os.path.join(folder, "capture.json")):
            continue
        try:
            peer = reader.Capture(folder)
        except Exception as e:
            notes.append(f"peer {name}: could not read ({e})")
            continue
        notes.append(splice_one(cap, peer, name))
    return notes


def splice_one(cap, peer, name):
    source = next((a for a in peer.actors.values() if a.info.get("isLocal") and len(a.times)), None)
    if source is None:
        return f"peer {name}: no local actor in their capture"
    uuid = source.info.get("uuid")
    targets = [a for a in cap.actors.values() if a.info.get("kind") == "player" and not a.info.get("isLocal") and a.info.get("uuid") == uuid and len(a.times)]
    if not targets:
        return f"peer {name}: {source.info.get('displayName') or uuid} is not in the main capture"
    if len(peer.ticks) == len(peer.times) and len(peer.ticks):
        peer_times = (peer.ticks[source.frames] - cap.start_ticks) / 1e7
    else:
        peer_times = source.times + (peer.start_ticks - cap.start_ticks) / 1e7
    order = np.argsort(peer_times, kind="stable")
    peer_times = peer_times[order]
    lo, hi = peer_times[0], peer_times[-1]
    total = 0
    for target in targets:
        main_t = target.times
        before = main_t < lo
        after = main_t > hi
        src_paths = {b["path"]: i for i, b in enumerate(source.info["bones"])}
        pairs = [(ti, src_paths[b["path"]]) for ti, b in enumerate(target.info["bones"]) if b["path"] in src_paths]
        bones = space.resample_trs(main_t, target.bones, peer_times)
        if pairs:
            ti, si = (np.array(x) for x in zip(*pairs))
            bones[:, ti] = source.bones[order][:, si]
        shapes = space.resample(main_t, target.shapes, peer_times) if target.shapes.shape[1] else np.zeros((len(peer_times), 0), np.float32)
        src_shapes = {}
        for r in source.info["renderers"]:
            for k, s in enumerate(r["shapes"]):
                src_shapes.setdefault((r["name"], s), r["shapeOffset"] + k)
        shape_pairs = []
        for r in target.info["renderers"]:
            for k, s in enumerate(r["shapes"]):
                j = src_shapes.get((r["name"], s))
                if j is not None:
                    shape_pairs.append((r["shapeOffset"] + k, j))
        if shape_pairs and source.shapes.shape[1]:
            ti, si = (np.array(x) for x in zip(*shape_pairs))
            shapes[:, ti] = source.shapes[order][:, si]
        renderers = space.resample(main_t, target.renderers.astype(np.float32), peer_times) > 0.5
        target.times = np.concatenate([main_t[before], peer_times, main_t[after]])
        target.bones = np.concatenate([target.bones[before], bones.astype(np.float32), target.bones[after]])
        target.root = np.concatenate([target.root[before], source.root[order], target.root[after]])
        target.shapes = np.concatenate([target.shapes[before], shapes.astype(np.float32), target.shapes[after]])
        target.visible = np.concatenate([target.visible[before], source.visible[order], target.visible[after]])
        target.renderers = np.concatenate([target.renderers[before], renderers, target.renderers[after]])
        target.info["spliced"] = True
        target.info["splicedRange"] = [float(lo), float(hi)]
        target.info["splicedSamples"] = int(len(peer_times))
        total += len(peer_times)
    who = source.info.get("displayName") or uuid
    rate = (len(peer_times) - 1) / max(1e-6, hi - lo)
    return f"peer {name}: {who} now plays their own {len(peer_times)} samples ({rate:.0f} Hz) from {lo:.2f}s to {hi:.2f}s"
