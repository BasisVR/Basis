import json
import os
import struct
import zlib

import numpy as np

FRAMES_MAGIC = 0x50414342
REC_ACTOR, REC_SCENE, REC_LIGHTS, REC_CAMERA = 1, 2, 3, 4
MESH_MAGIC = b"BCMESH01"
F_NORMALS, F_TANGENTS, F_COLORS, F_SKIN, F_WORLD = 1, 2, 4, 8, 1 << 16


class ActorTrack:
    def __init__(self, info):
        self.info = info
        self.bone_count = len(info["bones"])
        self.shape_count = sum(len(r["shapes"]) for r in info["renderers"])
        self.renderer_count = len(info["renderers"])
        self.frames = []
        self.root = []
        self.visible = []
        self.bones = []
        self.shapes = []
        self.renderers = []
        self._bones = np.zeros((self.bone_count, 10), np.float32)
        rest = [b["rest"] for b in info["bones"]]
        if rest:
            self._bones[:] = np.asarray(rest, np.float32)
        self._shapes = np.zeros(self.shape_count, np.float32)

    def finish(self):
        n = len(self.frames)
        self.frames = np.asarray(self.frames, np.int32)
        self.root = np.asarray(self.root, np.float32).reshape(n, 16) if n else np.zeros((0, 16), np.float32)
        self.visible = np.asarray(self.visible, bool)
        self.bones = np.asarray(self.bones, np.float32).reshape(n, self.bone_count, 10) if n else np.zeros((0, self.bone_count, 10), np.float32)
        self.shapes = np.asarray(self.shapes, np.float32).reshape(n, self.shape_count) if n else np.zeros((0, self.shape_count), np.float32)
        self.renderers = np.asarray(self.renderers, bool).reshape(n, self.renderer_count) if n else np.zeros((0, self.renderer_count), bool)


class Capture:
    def __init__(self, folder, frame_step=1, frame_limit=None):
        self.folder = folder
        with open(os.path.join(folder, "capture.json"), "r", encoding="utf-8") as f:
            self.manifest = json.load(f)
        mat_path = os.path.join(folder, "materials.json")
        self.materials = []
        if os.path.exists(mat_path):
            with open(mat_path, "r", encoding="utf-8") as f:
                self.materials = json.load(f).get("materials", [])
        self.frame_step = max(1, int(frame_step))
        self.frame_limit = frame_limit
        self.times = []
        self.ticks = []
        self.actors = {a["id"]: ActorTrack(a) for a in self.manifest.get("actors", [])}
        self.scene_keys = {}
        self.light_keys = {}
        self.camera_keys = {}
        self._read_frames()

    @property
    def start_ticks(self):
        return self.manifest["time"]["startServerTicks"]

    def _read_frames(self):
        path = os.path.join(self.folder, "frames.bin")
        if not os.path.exists(path):
            self.times = np.zeros(0)
            self.ticks = np.zeros(0, np.int64)
            return
        with open(path, "rb") as f:
            data = f.read()
        magic, version = struct.unpack_from("<Ii", data, 0)
        if magic != FRAMES_MAGIC:
            raise ValueError("frames.bin is not a Basis capture")
        pos = 8
        kept = 0
        while pos + 16 <= len(data):
            first, count, raw_len, comp_len = struct.unpack_from("<iiii", data, pos)
            pos += 16
            if pos + comp_len > len(data):
                break
            raw = zlib.decompress(data[pos:pos + comp_len], -15)
            pos += comp_len
            off = 0
            for _ in range(count):
                off, frame_used = self._read_frame(raw, off, kept)
                if frame_used:
                    kept += 1
                if self.frame_limit is not None and kept >= self.frame_limit:
                    break
            if self.frame_limit is not None and kept >= self.frame_limit:
                break
        self.times = np.asarray(self.times, np.float64)
        self.ticks = np.asarray(self.ticks, np.int64)
        for a in self.actors.values():
            a.finish()
            a.times = self.times[a.frames] if len(a.frames) else np.zeros(0)

    def _read_frame(self, raw, off, kept):
        index, time, ticks, records = struct.unpack_from("<idqi", raw, off)
        off += 24
        use = index % self.frame_step == 0
        if use:
            self.times.append(time)
            self.ticks.append(ticks)
        for _ in range(records):
            rtype, rid, length = struct.unpack_from("<Bii", raw, off)
            off += 9
            body = raw[off:off + length]
            off += length
            if rtype == REC_ACTOR:
                self._actor(rid, body, kept, use)
            elif rtype == REC_SCENE:
                self._scene(body, kept, use)
            elif rtype == REC_LIGHTS:
                self._lights(body, kept, use)
            elif rtype == REC_CAMERA:
                self._camera(rid, body, kept, use)
        return off, use

    def _actor(self, rid, b, kept, use):
        a = self.actors.get(rid)
        if a is None:
            return
        o = 0
        flags = b[o]
        o += 1
        root = np.frombuffer(b, np.float32, 16, o)
        o += 64
        n = struct.unpack_from("<i", b, o)[0]
        o += 4
        mask_len = (n + 7) >> 3
        mask = np.unpackbits(np.frombuffer(b, np.uint8, mask_len, o), bitorder="little")[:n].astype(bool)
        o += mask_len
        changed = int(mask.sum())
        if changed:
            a._bones[mask] = np.frombuffer(b, np.float32, changed * 10, o).reshape(changed, 10)
        o += changed * 40
        s = struct.unpack_from("<i", b, o)[0]
        o += 4
        mask_len = (s + 7) >> 3
        smask = np.unpackbits(np.frombuffer(b, np.uint8, mask_len, o), bitorder="little")[:s].astype(bool)
        o += mask_len
        changed = int(smask.sum())
        if changed and s == a.shape_count:
            a._shapes[smask] = np.frombuffer(b, np.float32, changed, o)
        o += changed * 4
        r = struct.unpack_from("<i", b, o)[0]
        o += 4
        rmask = np.unpackbits(np.frombuffer(b, np.uint8, (r + 7) >> 3, o), bitorder="little")[:r].astype(bool)
        if not use:
            return
        a.frames.append(kept)
        a.root.append(root.copy())
        a.visible.append(bool(flags & 1))
        a.bones.append(a._bones.copy())
        a.shapes.append(a._shapes.copy())
        a.renderers.append(rmask if r == a.renderer_count else np.ones(a.renderer_count, bool))

    def _scene(self, b, kept, use):
        count = struct.unpack_from("<i", b, 0)[0]
        o = 4
        for _ in range(count):
            idx, active = struct.unpack_from("<iB", b, o)
            o += 5
            m = np.frombuffer(b, np.float32, 16, o).copy()
            o += 64
            self.scene_keys.setdefault(idx, []).append((kept, active, m, use))

    def _lights(self, b, kept, use):
        count = struct.unpack_from("<i", b, 0)[0]
        o = 4
        for _ in range(count):
            idx, n = struct.unpack_from("<ii", b, o)
            o += 8
            v = np.frombuffer(b, np.float32, n, o).copy()
            o += n * 4
            self.light_keys.setdefault(idx, []).append((kept, v, use))

    def _camera(self, rid, b, kept, use):
        enabled = b[0]
        m = np.frombuffer(b, np.float32, 16, 1).copy()
        n = struct.unpack_from("<i", b, 65)[0]
        v = np.frombuffer(b, np.float32, n, 69).copy()
        self.camera_keys.setdefault(rid, []).append((kept, enabled, m, v, use))

    def mesh_path(self, mesh_id):
        for m in self.manifest.get("meshes", []):
            if m["id"] == mesh_id and m.get("file"):
                return os.path.join(self.folder, m["file"])
        return None

    def texture_path(self, tex_id):
        for t in self.manifest.get("textures", []):
            if t["id"] == tex_id and t.get("file"):
                return os.path.join(self.folder, t["file"])
        return None

    def texture_info(self, tex_id):
        for t in self.manifest.get("textures", []):
            if t["id"] == tex_id:
                return t
        return None


class MeshData:
    pass


def read_mesh(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != MESH_MAGIC:
        raise ValueError("not a Basis capture mesh: " + path)
    o = 8
    version, n, flags = struct.unpack_from("<iii", data, o)
    o += 12
    m = MeshData()
    m.vertex_count = n
    m.flags = flags
    m.world_space = bool(flags & F_WORLD)

    def take(count, width=1, dtype=np.float32):
        nonlocal o
        arr = np.frombuffer(data, dtype, count * width, o)
        o += arr.nbytes
        return arr.reshape(count, width) if width > 1 else arr

    m.positions = take(n, 3)
    m.normals = take(n, 3) if flags & F_NORMALS else None
    m.tangents = take(n, 4) if flags & F_TANGENTS else None
    m.colors = take(n, 4) if flags & F_COLORS else None
    m.uvs = {}
    for u in range(8):
        if flags & (16 << u):
            d = struct.unpack_from("<i", data, o)[0]
            o += 4
            m.uvs[u] = take(n, d)
    sub_count = struct.unpack_from("<i", data, o)[0]
    o += 4
    m.submeshes = []
    for _ in range(sub_count):
        topology, count = struct.unpack_from("<ii", data, o)
        o += 8
        m.submeshes.append((topology, take(count, 1, np.int32)))
    m.bindposes = None
    m.bone_indices = None
    m.bone_weights = None
    if flags & F_SKIN:
        bc = struct.unpack_from("<i", data, o)[0]
        o += 4
        m.bindposes = take(bc, 16).reshape(bc, 4, 4).transpose(0, 2, 1) if bc else np.zeros((0, 4, 4), np.float32)
        m.bone_indices = take(n, 4, np.int32)
        m.bone_weights = take(n, 4)
    shape_count = struct.unpack_from("<i", data, o)[0]
    o += 4
    m.shapes = []
    for _ in range(shape_count):
        ln = struct.unpack_from("<i", data, o)[0]
        o += 4
        name = data[o:o + ln].decode("utf-8", "replace")
        o += ln
        frames = struct.unpack_from("<i", data, o)[0]
        o += 4
        fl = []
        for _ in range(frames):
            weight, nnz = struct.unpack_from("<fi", data, o)
            o += 8
            rec = np.frombuffer(data, np.dtype([("v", "<i4"), ("dp", "<f4", 3), ("dn", "<f4", 3), ("dt", "<f4", 3)]), nnz, o)
            o += nnz * 40
            fl.append((weight, rec))
        m.shapes.append((name, fl))
    return m


def matrix_from_column_major(values):
    return np.asarray(values, np.float64).reshape(4, 4).T
