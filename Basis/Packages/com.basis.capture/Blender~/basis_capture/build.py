import math
import os

import bpy
import numpy as np
from mathutils import Matrix

from . import reader, shading, space, splice

TOPOLOGY_TRIANGLES, TOPOLOGY_QUADS = 0, 2


class Options:
    def __init__(self, **kw):
        self.import_scene = kw.get("import_scene", True)
        self.import_actors = kw.get("import_actors", True)
        self.import_cameras = kw.get("import_cameras", True)
        self.import_audio = kw.get("import_audio", True)
        self.splice_peers = kw.get("splice_peers", True)
        self.resample = kw.get("resample", True)
        self.frame_step = kw.get("frame_step", 1)
        self.light_scale = kw.get("light_scale", 1.0)
        self.use_lightmaps = kw.get("use_lightmaps", True)
        self.avatar_shading = kw.get("avatar_shading", "TOON")
        self.spatial_audio = kw.get("spatial_audio", True)
        self.build_world = kw.get("build_world", True)
        self.players = kw.get("players")
        self.import_world_meshes = kw.get("import_world_meshes", True)
        self.import_lights = kw.get("import_lights", True)
        self.time_range = kw.get("time_range")
        self.set_scene_range = kw.get("set_scene_range", True)


def bl_matrix(m):
    return Matrix(np.asarray(m, np.float64).tolist())


class Importer:
    def __init__(self, context, folder, options):
        self.context = context
        self.folder = folder
        self.options = options
        self.scene = context.scene
        self.fps = self.scene.render.fps / self.scene.render.fps_base
        self.cap = reader.Capture(folder, frame_step=options.frame_step)
        self.images = {}
        self.materials = {}
        self.meshes = {}
        self.report = []
        name = os.path.basename(os.path.normpath(folder))
        self.collection = bpy.data.collections.new("Basis Capture " + name)
        self.scene.collection.children.link(self.collection)
        duration = float(self.cap.times[-1]) if len(self.cap.times) else 0.0
        self.target = np.arange(0.0, duration + 1e-9, 1.0 / self.fps) if options.resample else np.asarray(self.cap.times, np.float64)
        self.last_frame = 1
        self.players = {}
        self.mesh_data = {}
        self.shader = shading.Builder(self)
        if hasattr(self.scene, "basis_avatar_toon"):
            self.scene.basis_avatar_toon = options.avatar_shading == "TOON"
            self.scene.basis_light_scale = options.light_scale
        self.lightmaps = {l["index"]: (l.get("color", -1), l.get("direction", -1)) for l in self.cap.manifest.get("lightmaps", [])}

    def frame_of(self, t):
        return 1.0 + np.asarray(t, np.float64) * self.fps

    def run(self):
        if self.options.splice_peers:
            self.report += splice.splice_peers(self.cap)
        if self.options.import_actors:
            for track in self.cap.actors.values():
                kind = track.info.get("kind")
                if kind == "player" and self.options.players is not None and track.info.get("uuid") not in self.options.players:
                    continue
                if kind != "player" and not self.options.import_world_meshes:
                    continue
                if len(track.times):
                    try:
                        self.build_actor(track)
                    except Exception as e:
                        self.report.append(f"actor {track.info.get('displayName') or track.info.get('name')}: {e}")
        if self.options.import_scene:
            self.build_scene_objects()
            if self.options.build_world:
                self.report.append(shading.build_world(self))
        self.report.append(shading.apply_post(self))
        if self.options.import_cameras:
            self.build_cameras()
        if self.options.import_audio:
            self.build_audio()
        m = self.cap.manifest
        world = next((c.get("url") for c in m.get("content", []) if c.get("mode") == "Scene"), None)
        rec = m.get("recorder", {})
        self.scene["basis_take"] = {"folder": self.folder, "world": (world or "").split("/")[-2] if world else "", "duration": float(m.get("time", {}).get("duration", 0.0)), "rate": int(m.get("options", {}).get("rateHz", 0) or 0), "players": len(self.players), "recorder": rec.get("displayName") or ""}
        if self.options.set_scene_range and len(self.cap.times):
            lo, hi = (self.options.time_range or (0.0, float(self.cap.times[-1])))
            self.scene.frame_start = max(1, int(math.floor(self.frame_of(lo))))
            self.scene.frame_end = max(self.scene.frame_start, int(math.ceil(self.frame_of(min(hi, float(self.cap.times[-1]))))))
        return self.report

    def link(self, obj):
        self.collection.objects.link(obj)
        return obj

    def action_for(self, idblock, name):
        ad = idblock.animation_data or idblock.animation_data_create()
        if ad.action is None:
            ad.action = bpy.data.actions.new(name)
        return ad.action

    def fcurve(self, idblock, path, index, group):
        action = self.action_for(idblock, idblock.name)
        if hasattr(action, "fcurve_ensure_for_datablock"):
            return action.fcurve_ensure_for_datablock(idblock, path, index=index, group_name=group)
        fc = action.fcurves.find(path, index=index)
        return fc or action.fcurves.new(path, index=index, action_group=group)

    def keys(self, idblock, path, index, frames, values, group="", interpolation=1):
        values = np.asarray(values, np.float64)
        frames = np.asarray(frames, np.float64)
        if len(values) == 0:
            return
        if np.ptp(values) < 1e-7:
            frames, values = frames[:1], values[:1]
        fc = self.fcurve(idblock, path, index, group)
        n = len(frames)
        fc.keyframe_points.add(n)
        co = np.empty(n * 2, np.float32)
        co[0::2] = frames
        co[1::2] = values
        fc.keyframe_points.foreach_set("co", co)
        fc.keyframe_points.foreach_set("interpolation", np.full(n, interpolation, np.int32))
        fc.update()

    def step_keys(self, idblock, path, frames, values, group=""):
        values = np.asarray(values)
        if len(values) == 0:
            return
        change = np.ones(len(values), bool)
        change[1:] = values[1:] != values[:-1]
        idx = np.nonzero(change)[0]
        fc = self.fcurve(idblock, path, 0, group)
        fc.keyframe_points.add(len(idx))
        co = np.empty(len(idx) * 2, np.float32)
        co[0::2] = np.asarray(frames, np.float64)[idx]
        co[1::2] = values[idx].astype(np.float64)
        fc.keyframe_points.foreach_set("co", co)
        fc.keyframe_points.foreach_set("interpolation", np.zeros(len(idx), np.int32))
        fc.update()

    def transform_keys(self, obj, frames, matrices, group="Object Transforms"):
        loc, quat, scale = space.decompose(matrices)
        quat = space.continuous(quat)
        obj.rotation_mode = "QUATERNION"
        for i in range(3):
            self.keys(obj, "location", i, frames, loc[:, i], group)
            self.keys(obj, "scale", i, frames, scale[:, i], group)
        for i in range(4):
            self.keys(obj, "rotation_quaternion", i, frames, quat[:, i], group)
        obj.matrix_world = bl_matrix(matrices[0])

    def actor_times(self, track):
        t = track.times
        if not self.options.resample:
            return t, np.arange(len(t))
        lo, hi = t[0], t[-1]
        if self.options.time_range:
            lo, hi = max(lo, self.options.time_range[0]), min(hi, self.options.time_range[1])
        tgt = self.target[(self.target >= lo - 1e-9) & (self.target <= hi + 1e-9)]
        if len(tgt) == 0:
            tgt = np.array([lo])
        return tgt, None

    def build_actor(self, track):
        info = track.info
        bones = info["bones"]
        nb = len(bones)
        parents = np.array([b["parent"] for b in bones], np.int64)
        src_t = track.times
        tgt, _ = self.actor_times(track)
        trs = track.bones if not self.options.resample else space.resample_trs(src_t, track.bones, tgt)
        root = track.root if not self.options.resample else space.resample(src_t, track.root, tgt)
        frames = self.frame_of(tgt)
        nf = len(tgt)

        def chain(local_trs):
            local = space.trs_to_matrix(local_trs)
            world = np.empty_like(local)
            for b in range(nb):
                p = parents[b]
                world[..., b, :, :] = local[..., b, :, :] if p < 0 else world[..., p, :, :] @ local[..., b, :, :]
            return world

        a0 = chain(trs[0])
        rest = a0.copy()
        assigned = np.zeros(nb, bool)
        mesh_cache = {}
        for r in info["renderers"]:
            if r["mesh"] < 0:
                continue
            path = self.cap.mesh_path(r["mesh"])
            if not path:
                continue
            md = mesh_cache.get(r["mesh"]) or reader.read_mesh(path)
            mesh_cache[r["mesh"]] = md
            if not r["skinned"] or md.bindposes is None:
                continue
            m_obj = a0[r["transform"]] if r["transform"] >= 0 else np.eye(4)
            for slot, b in enumerate(r["bones"]):
                if b < 0 or assigned[b] or slot >= len(md.bindposes):
                    continue
                try:
                    rest[b] = m_obj @ np.linalg.inv(md.bindposes[slot])
                    assigned[b] = True
                except np.linalg.LinAlgError:
                    pass
        rest_n, rest_s = space.strip_scale(rest)
        rest_b = space.C @ rest_n @ space.CT

        label = info.get("displayName") or info.get("name") or f"actor {info['id']}"
        arm = bpy.data.armatures.new(f"{label} Armature")
        arm_obj = self.link(bpy.data.objects.new(label, arm))
        arm_obj["basis_uuid"] = info.get("uuid") or ""
        arm_obj["basis_avatar_url"] = info.get("avatarUrl") or ""
        arm_obj["basis_spliced"] = bool(info.get("spliced"))
        arm_obj["basis_is_local"] = bool(info.get("isLocal"))
        arm.display_type = "STICK"
        names = []
        used = set()
        for b in bones:
            n = b["name"] or "bone"
            base, k = n, 1
            while n in used:
                k += 1
                n = f"{base}.{k:03d}"
            used.add(n)
            names.append(n)
        view_layer = self.context.view_layer
        view_layer.objects.active = arm_obj
        arm_obj.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        ebs = []
        heads = rest_b[:, :3, 3]
        for b in range(nb):
            eb = arm.edit_bones.new(names[b])
            children = np.nonzero(parents == b)[0]
            length = float(np.linalg.norm(heads[children[0]] - heads[b])) if len(children) else 0.0
            if length < 1e-3:
                length = float(np.linalg.norm(heads[b] - heads[parents[b]])) * 0.5 if parents[b] >= 0 else 0.05
            eb.head = (0.0, 0.0, 0.0)
            eb.tail = (0.0, max(length, 0.005), 0.0)
            eb.matrix = bl_matrix(rest_b[b])
            ebs.append(eb)
        for b in range(nb):
            if parents[b] >= 0:
                ebs[b].parent = ebs[parents[b]]
        bpy.ops.object.mode_set(mode="OBJECT")

        rel_rest = np.empty_like(rest_b)
        for b in range(nb):
            rel_rest[b] = rest_b[b] if parents[b] < 0 else np.linalg.inv(rest_b[parents[b]]) @ rest_b[b]
        inv_rel_rest = np.linalg.inv(rel_rest)
        inv_scale = np.zeros((nb, 4, 4))
        inv_scale[:, 0, 0] = 1.0 / rest_s[:, 0]
        inv_scale[:, 1, 1] = 1.0 / rest_s[:, 1]
        inv_scale[:, 2, 2] = 1.0 / rest_s[:, 2]
        inv_scale[:, 3, 3] = 1.0
        basis = np.empty((nf, nb, 10), np.float64)
        step = 1024
        for s in range(0, nf, step):
            w = chain(trs[s:s + step])
            pose = space.C @ (w @ inv_scale) @ space.CT
            rel = np.empty_like(pose)
            for b in range(nb):
                p = parents[b]
                rel[:, b] = pose[:, b] if p < 0 else np.linalg.inv(pose[:, p]) @ pose[:, b]
            m = inv_rel_rest[None] @ rel
            loc, quat, scale = space.decompose(m)
            basis[s:s + step, :, 0:3] = loc
            basis[s:s + step, :, 3:7] = quat
            basis[s:s + step, :, 7:10] = scale
        basis[:, :, 3:7] = space.continuous(basis[:, :, 3:7])
        for b in range(nb):
            pb = arm_obj.pose.bones[names[b]]
            pb.rotation_mode = "QUATERNION"
            path = f'pose.bones["{bpy.utils.escape_identifier(names[b])}"]'
            for i in range(3):
                self.keys(arm_obj, path + ".location", i, frames, basis[:, b, i], names[b])
                self.keys(arm_obj, path + ".scale", i, frames, basis[:, b, 7 + i], names[b])
            for i in range(4):
                self.keys(arm_obj, path + ".rotation_quaternion", i, frames, basis[:, b, 3 + i], names[b])
        root_m = space.C @ root.reshape(-1, 4, 4).transpose(0, 2, 1) @ space.CT
        self.transform_keys(arm_obj, frames, root_m)
        visible = track.visible if not self.options.resample else np.asarray(space.resample(src_t, track.visible.astype(np.float32), tgt) > 0.5)

        owned = []
        shapes = track.shapes if not self.options.resample else space.resample(src_t, track.shapes, tgt)
        rmask = track.renderers if not self.options.resample else (space.resample(src_t, track.renderers.astype(np.float32), tgt) > 0.5)
        for ri, r in enumerate(info["renderers"]):
            md = mesh_cache.get(r["mesh"])
            if md is None:
                continue
            mats = [self.material(mid, None, True) for mid in r["materials"]]
            mesh = self.build_mesh(md, f"{label} {r['name']}", mats)
            obj = self.link(bpy.data.objects.new(f"{label} {r['name']}", mesh))
            obj.parent = arm_obj
            obj.matrix_parent_inverse = Matrix.Identity(4)
            m0 = a0[r["transform"]] if r["transform"] >= 0 else np.eye(4)
            if r["skinned"] and md.bone_indices is not None:
                obj.matrix_basis = bl_matrix(space.C @ m0 @ space.CT)
                groups = {}
                for slot, b in enumerate(r["bones"]):
                    if b >= 0:
                        groups[slot] = obj.vertex_groups.get(names[b]) or obj.vertex_groups.new(name=names[b])
                bi, bw = md.bone_indices, md.bone_weights
                for k in range(4):
                    nz = np.nonzero(bw[:, k] > 0)[0]
                    for v in nz:
                        g = groups.get(int(bi[v, k]))
                        if g is not None:
                            g.add([int(v)], float(bw[v, k]), "ADD")
            elif r["transform"] >= 0:
                b = r["transform"]
                obj.matrix_basis = bl_matrix(space.C @ rest[b] @ np.linalg.inv(a0[b]) @ m0 @ space.CT)
                g = obj.vertex_groups.new(name=names[b])
                g.add(list(range(md.vertex_count)), 1.0, "REPLACE")
            else:
                obj.matrix_basis = Matrix.Identity(4)
            mod = obj.modifiers.new("Armature", "ARMATURE")
            mod.object = arm_obj
            if mesh.shape_keys is not None and r["shapes"]:
                key = mesh.shape_keys
                frame_weights = mesh.get("basis_shape_weights", {})
                for k, sname in enumerate(r["shapes"]):
                    kb = key.key_blocks.get(sname)
                    if kb is None:
                        continue
                    full = frame_weights.get(sname, 100.0)
                    vals = shapes[:, r["shapeOffset"] + k] / (full if full else 100.0)
                    self.keys(key, f'key_blocks["{bpy.utils.escape_identifier(sname)}"].value', 0, frames, vals, "Shapes")
            vis = (rmask[:, ri] & visible).astype(np.float64)
            hidden = 1.0 - vis
            if np.any(hidden):
                self.step_keys(obj, "hide_render", frames, hidden)
                self.step_keys(obj, "hide_viewport", frames, hidden)
            owned.append(obj)
            if mesh.shape_keys is not None:
                owned.append(mesh.shape_keys)
        owned.insert(0, arm_obj)
        uuid = info.get("uuid") or f"actor{info['id']}"
        self.players.setdefault(uuid, []).append({"armature": arm_obj, "names": names, "info": info, "label": label})
        for idb in owned:
            idb["basis_player"] = uuid
            idb["basis_player_label"] = label
            self.push_to_nla(idb, label)

    def push_to_nla(self, idb, label):
        ad = idb.animation_data
        if ad is None or ad.action is None:
            return
        act = ad.action
        act.name = f"{label} | {idb.name}"
        act["basis_player_label"] = label
        start = float(act.frame_range[0])
        slot = getattr(ad, "action_slot", None)
        track = ad.nla_tracks.new()
        track.name = f"Basis {label}"
        strip = track.strips.new(label, int(math.floor(start)), act)
        if slot is not None and hasattr(strip, "action_slot"):
            try:
                strip.action_slot = slot
            except (TypeError, AttributeError, RuntimeError):
                pass
        if abs(strip.frame_start - start) > 1e-4 and hasattr(strip, "frame_start_ui"):
            strip.frame_start_ui = start
        strip.extrapolation = "HOLD_FORWARD"
        ad.action = None

    def build_mesh(self, md, name, mats):
        mesh = bpy.data.meshes.new(name)
        verts = space.to_blender_points(md.positions)
        faces = []
        mat_index = []
        for si, (topology, idx) in enumerate(md.submeshes):
            if topology == TOPOLOGY_TRIANGLES:
                tri = idx[: len(idx) - len(idx) % 3].reshape(-1, 3)[:, [0, 2, 1]]
            elif topology == TOPOLOGY_QUADS:
                tri = idx[: len(idx) - len(idx) % 4].reshape(-1, 4)[:, [0, 3, 2, 1]]
            else:
                continue
            faces.append(tri)
            mat_index.append(np.full(len(tri), si, np.int32))
        nv = len(verts)
        mesh.vertices.add(nv)
        mesh.vertices.foreach_set("co", verts.astype(np.float32).ravel())
        loops = []
        sizes = []
        for f in faces:
            loops.append(f.ravel())
            sizes.append(np.full(len(f), f.shape[1], np.int32))
        loop_v = np.concatenate(loops).astype(np.int32) if loops else np.zeros(0, np.int32)
        sizes = np.concatenate(sizes) if sizes else np.zeros(0, np.int32)
        starts = np.zeros(len(sizes), np.int32)
        if len(sizes):
            starts[1:] = np.cumsum(sizes)[:-1]
        mesh.loops.add(len(loop_v))
        mesh.loops.foreach_set("vertex_index", loop_v)
        mesh.polygons.add(len(sizes))
        mesh.polygons.foreach_set("loop_start", starts)
        try:
            mesh.polygons.foreach_set("loop_total", sizes)
        except (AttributeError, TypeError):
            pass
        for m in mats:
            mesh.materials.append(m)
        if len(sizes):
            mesh.polygons.foreach_set("material_index", np.concatenate(mat_index))
        mesh.update(calc_edges=True)
        mesh.validate(clean_customdata=False)
        for u, uv in sorted(md.uvs.items()):
            layer = mesh.uv_layers.new(name=f"UV{u}")
            if layer is None:
                break
            layer.data.foreach_set("uv", uv[loop_v, :2].astype(np.float32).ravel())
        if md.colors is not None:
            attr = mesh.color_attributes.new("Color", "FLOAT_COLOR", "POINT")
            attr.data.foreach_set("color", md.colors.astype(np.float32).ravel())
        if md.normals is not None and len(sizes):
            n = space.to_blender_points(md.normals)
            ln = np.linalg.norm(n, axis=1, keepdims=True)
            n = n / np.where(ln < 1e-12, 1.0, ln)
            try:
                mesh.normals_split_custom_set_from_vertices(n.astype(np.float32).tolist())
            except Exception:
                pass
        if md.shapes:
            basis_key = None
            weights = {}
            for sname, fl in md.shapes:
                if not fl:
                    continue
                weight, rec = fl[-1]
                if basis_key is None:
                    tmp = bpy.data.objects.new("_tmp", mesh)
                    basis_key = tmp.shape_key_add(name="Basis", from_mix=False)
                kb = tmp.shape_key_add(name=sname, from_mix=False)
                weights[kb.name] = float(weight) if weight else 100.0
                co = verts.copy()
                if len(rec):
                    co[rec["v"]] += space.to_blender_points(rec["dp"])
                kb.data.foreach_set("co", co.astype(np.float32).ravel())
            if basis_key is not None:
                bpy.data.objects.remove(tmp)
                mesh["basis_shape_weights"] = weights
        return mesh

    def image(self, tex_id):
        if tex_id is None or tex_id < 0:
            return None
        if tex_id in self.images:
            return self.images[tex_id]
        path = self.cap.texture_path(tex_id)
        img = None
        if path and os.path.exists(path):
            img = bpy.data.images.load(path, check_existing=True)
            info = self.cap.texture_info(tex_id) or {}
            if not info.get("srgb", True) or path.endswith(".exr"):
                try:
                    img.colorspace_settings.name = "Non-Color" if not path.endswith(".exr") else "Linear Rec.709"
                except TypeError:
                    pass
        self.images[tex_id] = img
        return img

    def material(self, mid, lightmap=None, avatar=False):
        if mid is None or mid < 0 or mid >= len(self.cap.materials):
            return None
        key = (mid, lightmap, avatar)
        if key not in self.materials:
            self.materials[key] = self.shader.build(mid, self.cap.materials[mid], lightmap, avatar)
        return self.materials[key]

    def scene_mesh(self, mesh_id, mats, name, lightmap_uv=None):
        key = (mesh_id, tuple(m.name if m else "" for m in mats), lightmap_uv)
        if key in self.meshes:
            return self.meshes[key]
        path = self.cap.mesh_path(mesh_id)
        if not path:
            return None
        md = self.mesh_data.get(mesh_id) or reader.read_mesh(path)
        self.mesh_data[mesh_id] = md
        mesh = self.build_mesh(md, name, mats)
        mesh["basis_world_space"] = md.world_space
        if lightmap_uv is not None:
            src = md.uvs.get(1, md.uvs.get(0))
            if src is not None:
                loops = np.zeros(len(mesh.loops), np.int32)
                mesh.loops.foreach_get("vertex_index", loops)
                uv = src[:, :2] * np.array(lightmap_uv[:2]) + np.array(lightmap_uv[2:4])
                layer = mesh.uv_layers.new(name="Lightmap")
                if layer is not None:
                    layer.data.foreach_set("uv", uv[loops].astype(np.float32).ravel())
        self.meshes[key] = mesh
        return mesh

    def build_scene_objects(self):
        coll = bpy.data.collections.new("World")
        self.collection.children.link(coll)
        for so in self.cap.manifest.get("sceneObjects", []):
            idx = so["index"]
            keys = self.cap.scene_keys.get(idx, [])
            init = np.asarray(so.get("initialMatrix", np.eye(4).ravel().tolist()), np.float64).reshape(4, 4)
            if so["kind"] == "mesh" and not self.options.import_world_meshes:
                continue
            if so["kind"] == "light" and not self.options.import_lights:
                continue
            if so["kind"] == "mesh":
                lm_index = so.get("lightmapIndex", -1)
                lm_tex, lm_dir = self.lightmaps.get(lm_index, (-1, -1)) if self.options.use_lightmaps else (-1, -1)
                lightmap = (lm_index, lm_tex, lm_dir) if lm_tex is not None and lm_tex >= 0 else None
                lm_uv = tuple(round(float(v), 6) for v in so.get("lightmapScaleOffset", [1, 1, 0, 0])) if lightmap else None
                mats = [self.material(m, lightmap) for m in so.get("materials", [])]
                mesh = self.scene_mesh(so["mesh"], mats, so["name"], lm_uv)
                if mesh is None:
                    continue
                obj = bpy.data.objects.new(so["name"], mesh)
                coll.objects.link(obj)
                if mesh.get("basis_world_space"):
                    obj.matrix_world = Matrix.Identity(4)
                    self.visibility_keys(obj, keys)
                    continue
                self.object_keys(obj, keys, init, None)
            elif so["kind"] == "light":
                lt = {"Directional": "SUN", "Spot": "SPOT", "Point": "POINT", "Rectangle": "AREA", "Disc": "AREA"}.get(so.get("lightType"), "POINT")
                data = bpy.data.lights.new(so["name"], lt)
                data.color = shading.light_color(so)
                intensity = float(so.get("intensity", 1.0))
                data["basis_base_energy"] = shading.light_energy(lt, intensity)
                data.energy = data["basis_base_energy"] * self.options.light_scale
                data["basis_bake_type"] = so.get("bakeType", "")
                data.use_shadow = so.get("shadows", "Soft") != "None"
                if lt in ("POINT", "SPOT"):
                    data.shadow_soft_size = 0.05 if so.get("shadows") == "Hard" else 0.15
                if lt == "SPOT":
                    data.spot_size = math.radians(float(so.get("spotAngle", 30.0)))
                    inner = float(so.get("innerSpotAngle", 0.0))
                    data.spot_blend = max(0.0, min(1.0, 1.0 - inner / max(1e-3, float(so.get("spotAngle", 30.0)))))
                if hasattr(data, "use_custom_distance") and lt != "SUN":
                    data.use_custom_distance = True
                    data.cutoff_distance = float(so.get("range", 10.0))
                obj = bpy.data.objects.new(so["name"], data)
                coll.objects.link(obj)
                self.object_keys(obj, keys, init, space.LOOK)
                lk = self.cap.light_keys.get(idx, [])
                if len(lk) > 1:
                    fr = self.frame_of(self.cap.times[np.clip([k[0] for k in lk], 0, len(self.cap.times) - 1)])
                    vals = np.array([k[1] for k in lk])
                    energy = np.array([shading.light_energy(lt, v) for v in vals[:, 4]]) * self.options.light_scale
                    self.keys(data, "energy", 0, fr, energy * vals[:, 0], "Light")
                    lin = space.srgb_to_linear(vals[:, 1:4])
                    for i in range(3):
                        self.keys(data, "color", i, fr, lin[:, i], "Light")

    def visibility_keys(self, obj, keys):
        if not keys:
            return
        fr = self.frame_of(self.cap.times[np.clip([k[0] for k in keys], 0, max(0, len(self.cap.times) - 1))]) if len(self.cap.times) else np.ones(len(keys))
        hidden = np.array([0.0 if k[1] else 1.0 for k in keys])
        if np.any(hidden):
            self.step_keys(obj, "hide_render", fr, hidden)
            self.step_keys(obj, "hide_viewport", fr, hidden)

    def object_keys(self, obj, keys, init_row_major, post):
        post = np.eye(4) if post is None else post
        if not keys:
            obj.matrix_world = bl_matrix(space.C @ init_row_major @ space.CT @ post)
            return
        idx = np.array([k[0] for k in keys])
        mats = np.array([k[2] for k in keys]).reshape(-1, 4, 4).transpose(0, 2, 1)
        if len(keys) == 1:
            obj.matrix_world = bl_matrix(space.C @ mats[0] @ space.CT @ post)
            self.visibility_keys(obj, keys)
            return
        hold_idx, hold_m = [], []
        for i in range(len(idx)):
            if i > 0 and idx[i] - idx[i - 1] > 1:
                hold_idx.append(idx[i] - 1)
                hold_m.append(mats[i - 1])
            hold_idx.append(idx[i])
            hold_m.append(mats[i])
        t = self.cap.times[np.clip(hold_idx, 0, len(self.cap.times) - 1)]
        m = space.C @ np.array(hold_m) @ space.CT @ post
        self.transform_keys(obj, self.frame_of(t), m)
        self.visibility_keys(obj, keys)

    def build_cameras(self):
        cams = self.cap.manifest.get("cameras", [])
        if not cams:
            return
        coll = bpy.data.collections.new("Cameras")
        self.collection.children.link(coll)
        for c in cams:
            keys = self.cap.camera_keys.get(c["id"], [])
            if not keys:
                continue
            data = bpy.data.cameras.new(c["name"])
            data.sensor_fit = "VERTICAL"
            data.sensor_height = 24.0
            obj = bpy.data.objects.new(("Main " if c.get("main") else "") + c["name"], data)
            coll.objects.link(obj)
            idx = np.array([k[0] for k in keys])
            t = self.cap.times[np.clip(idx, 0, len(self.cap.times) - 1)]
            fr = self.frame_of(t)
            mats = space.C @ np.array([k[2] for k in keys]).reshape(-1, 4, 4).transpose(0, 2, 1) @ space.CT @ space.LOOK
            vals = np.array([k[3] for k in keys])
            fov = np.radians(np.clip(vals[:, 0], 1.0, 179.0))
            lens = 12.0 / np.tan(fov / 2.0)
            data.clip_start = float(vals[0, 1])
            data.clip_end = float(vals[0, 2])
            if len(keys) == 1:
                obj.matrix_world = bl_matrix(mats[0])
                data.lens = float(lens[0])
            else:
                self.transform_keys(obj, fr, mats)
                self.keys(data, "lens", 0, fr, lens, "Camera")
            if c.get("main"):
                self.scene.camera = obj
                aspect = float(vals[0, 3]) if vals[0, 3] > 0 else 16 / 9
                self.scene.render.resolution_y = 1080
                self.scene.render.resolution_x = int(round(1080 * aspect))

    def build_audio(self):
        import json
        entries = []
        for a in self.cap.manifest.get("audio", []):
            entries.append((os.path.join(self.folder, a["file"]), a, a.get("uuid", "")))
        peers = os.path.join(self.folder, "peers")
        if os.path.isdir(peers):
            for name in os.listdir(peers):
                mpath = os.path.join(peers, name, "capture.json")
                if not os.path.exists(mpath):
                    continue
                with open(mpath, "r", encoding="utf-8") as f:
                    m = json.load(f)
                for a in m.get("audio", []):
                    entries.append((os.path.join(peers, name, a["file"]), a, a.get("uuid", name)))
        if not entries:
            return
        self.scene.audio_distance_model = "INVERSE_CLAMPED"
        coll = bpy.data.collections.new("Voices")
        self.collection.children.link(coll)
        channel = 1
        for path, a, who in entries:
            if not os.path.exists(path):
                continue
            offset = (a.get("startServerTicks", self.cap.start_ticks) - self.cap.start_ticks) / 1e7
            frame = int(round(float(self.frame_of(offset))))
            if self.options.players is not None and who not in self.options.players:
                continue
            player = (self.players.get(who) or [None])[0]
            label = player["label"] if player else who[:12]
            if player is not None and self.options.spatial_audio:
                try:
                    self.speaker(player, path, frame, label, who, coll)
                    self.report.append(f"voice: {label} speaks from their head from frame {frame}")
                    continue
                except Exception as e:
                    self.report.append(f"voice speaker for {label} failed ({e}), using the sequencer")
            se = self.scene.sequence_editor or self.scene.sequence_editor_create()
            strips = se.strips if hasattr(se, "strips") else se.sequences
            strips.new_sound(f"Voice {label}", path, channel, frame)
            channel += 1

    def speaker(self, player, path, frame, label, uuid, coll):
        arm = player["armature"]
        info = player["info"]
        human = info.get("humanBones") or []
        head_index = human[10] if len(human) > 10 and human[10] >= 0 else -1
        data = bpy.data.speakers.new(f"Voice {label}")
        data.sound = bpy.data.sounds.load(path, check_existing=True)
        data.distance_reference = 1.0
        data.attenuation = 1.0
        obj = bpy.data.objects.new(f"Voice {label}", data)
        coll.objects.link(obj)
        obj.parent = arm
        if head_index >= 0:
            bone = player["names"][head_index]
            obj.parent_type = "BONE"
            obj.parent_bone = bone
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.location = (0.0, -arm.data.bones[bone].length * 0.4, 0.0)
        obj["basis_player"] = uuid
        obj["basis_player_label"] = label
        for other in bpy.data.objects:
            if other.animation_data is not None:
                for t in other.animation_data.nla_tracks:
                    t.select = False
        ad = obj.animation_data_create()
        track = ad.nla_tracks.new()
        track.name = f"Basis {label}"
        track.select = True
        ad.nla_tracks.active = track
        made = add_sound_strip(obj, frame, self.scene)
        ad = obj.animation_data
        if not made or not any(t.strips for t in ad.nla_tracks):
            bpy.data.objects.remove(obj)
            bpy.data.speakers.remove(data)
            raise RuntimeError("needs the Blender window (run the import from File > Import)" if not made else "no sound strip was created")


def add_sound_strip(obj, frame, scene):
    wm = bpy.context.window_manager
    if wm is None or not wm.windows:
        return False
    win = wm.windows[0]
    area = max(win.screen.areas, key=lambda a: a.width * a.height)
    old_type, old_ui = area.type, getattr(area, "ui_type", None)
    prev = scene.frame_current
    view_layer = bpy.context.view_layer
    selected = [o for o in view_layer.objects if o.select_get()]
    active = view_layer.objects.active
    try:
        area.type = "NLA_EDITOR"
        region = next((r for r in area.regions if r.type == "WINDOW"), None)
        for o in selected:
            o.select_set(False)
        obj.select_set(True)
        view_layer.objects.active = obj
        scene.frame_set(frame)
        with bpy.context.temp_override(window=win, area=area, region=region, scene=scene, active_object=obj, object=obj, selected_objects=[obj]):
            bpy.ops.nla.soundclip_add()
        return True
    finally:
        area.type = old_type
        if old_ui:
            try:
                area.ui_type = old_ui
            except TypeError:
                pass
        scene.frame_set(prev)
        obj.select_set(False)
        for o in selected:
            o.select_set(True)
        view_layer.objects.active = active


def import_capture(context, folder, **kw):
    folder = os.path.dirname(folder) if folder.lower().endswith(".json") else folder
    imp = Importer(context, folder, Options(**kw))
    return imp, imp.run()
