import json
import os

import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from . import build, ui


class BasisImportPlayer(bpy.types.PropertyGroup):
    uuid: StringProperty()
    label: StringProperty()
    detail: StringProperty()
    include: BoolProperty(name="Include", default=True)


def take_folder(path):
    if not path:
        return None
    folder = os.path.dirname(path) if not os.path.isdir(path) else path
    return folder if os.path.exists(os.path.join(folder, "capture.json")) else None


class IMPORT_OT_basis_capture(bpy.types.Operator, ImportHelper):
    bl_idname = "import_scene.basis_capture"
    bl_label = "Import Basis Capture"
    bl_description = "Import a Basis scene capture (pick its capture.json)"
    bl_options = {"REGISTER", "UNDO", "PRESET"}

    filename_ext = ".json"
    filter_glob: StringProperty(default="capture.json", options={"HIDDEN"})
    players: CollectionProperty(type=BasisImportPlayer)
    loaded_from: StringProperty(options={"HIDDEN", "SKIP_SAVE"})
    take_summary: StringProperty(options={"HIDDEN", "SKIP_SAVE"})
    take_world: StringProperty(options={"HIDDEN", "SKIP_SAVE"})
    take_duration: FloatProperty(options={"HIDDEN", "SKIP_SAVE"})
    import_actors: BoolProperty(name="Avatars", default=True)
    import_world_meshes: BoolProperty(name="World and Props", description="World and prop geometry with their materials", default=True)
    import_lights: BoolProperty(name="Lights", default=True)
    build_world: BoolProperty(name="Environment and Sky", description="Use the environment captured in Basis for ambient light, reflections and the sky", default=True)
    use_lightmaps: BoolProperty(name="Baked Lighting", description="Light world surfaces with the world's exported lightmaps, like in Basis", default=True)
    import_cameras: BoolProperty(name="Cameras", default=True)
    import_audio: BoolProperty(name="Voices", default=True)
    spatial_audio: BoolProperty(name="Voices From Heads", description="Play each player's voice from a speaker on their head instead of the sequencer", default=True)
    splice_peers: BoolProperty(name="Use Players' Own Captures", description="Where a player sent their own capture, play their exact motion on their own timeline instead of what reached the recorder over the network", default=True)
    use_range: BoolProperty(name="Only Part of the Take", default=False)
    start_time: FloatProperty(name="From", unit="TIME_ABSOLUTE", default=0.0, min=0.0, subtype="TIME_ABSOLUTE")
    end_time: FloatProperty(name="To", unit="TIME_ABSOLUTE", default=0.0, min=0.0, subtype="TIME_ABSOLUTE")
    resample: BoolProperty(name="Resample to Scene FPS", description="Key every scene frame instead of every recorded sample", default=True)
    frame_step: IntProperty(name="Read Every Nth Sample", default=1, min=1, max=60)
    avatar_shading: EnumProperty(name="Avatar Shading", items=[("TOON", "Toon (Eevee)", "Approximate Poiyomi and lilToon toon lighting"), ("PBR", "PBR", "Principled BSDF, works in Cycles")], default="TOON")
    light_scale: FloatProperty(name="Light Strength", default=1.0, min=0.0, soft_max=10.0)

    def load_take(self):
        folder = take_folder(self.filepath)
        if folder == self.loaded_from:
            return False
        self.loaded_from = folder or ""
        chosen = {p.uuid: p.include for p in self.players}
        self.players.clear()
        self.take_summary = ""
        self.take_world = ""
        if not folder:
            return True
        try:
            with open(os.path.join(folder, "capture.json"), "r", encoding="utf-8") as f:
                m = json.load(f)
        except Exception as e:
            self.take_summary = f"Could not read capture.json: {e}"
            return True
        peers = set()
        peer_root = os.path.join(folder, "peers")
        if os.path.isdir(peer_root):
            for name in os.listdir(peer_root):
                try:
                    with open(os.path.join(peer_root, name, "capture.json"), "r", encoding="utf-8") as f:
                        pm = json.load(f)
                    peers.update(a.get("uuid") for a in pm.get("actors", []) if a.get("isLocal"))
                except Exception:
                    pass
        seen = set()
        for a in m.get("actors", []):
            if a.get("kind") != "player" or a.get("uuid") in seen:
                continue
            seen.add(a.get("uuid"))
            item = self.players.add()
            item.uuid = a.get("uuid") or ""
            item.include = chosen.get(item.uuid, True)
            item.label = a.get("displayName") or a.get("name") or "Player"
            tags = []
            if a.get("isLocal"):
                tags.append("recorder")
            if a.get("uuid") in peers:
                tags.append("own capture")
            tags.append(f"{len(a.get('bones', []))} bones")
            item.detail = ", ".join(tags)
        t = m.get("time", {})
        self.take_duration = float(t.get("duration", 0.0))
        if self.end_time <= 0.0:
            self.end_time = self.take_duration
        world = next((c.get("url") for c in m.get("content", []) if c.get("mode") == "Scene"), "")
        self.take_world = world.split("/")[-2] if world.count("/") > 1 else world
        rate = m.get("options", {}).get("rateHz") or 0
        self.take_summary = f"{self.take_duration:.1f} s, {rate or 'every frame'} Hz, {len(self.players)} players, {len(m.get('sceneObjects', []))} world objects"
        return True

    def check(self, context):
        return self.load_take()

    def draw(self, context):
        try:
            self.load_take()
        except Exception:
            pass
        layout = self.layout
        if not self.loaded_from:
            layout.label(text="Pick a take's capture.json", icon="INFO")
            return
        box = layout.box()
        box.label(text=self.take_world or "Take", icon="WORLD")
        box.label(text=self.take_summary)
        box = layout.box()
        row = box.row()
        row.prop(self, "import_actors", text="Players")
        col = box.column(align=True)
        col.enabled = self.import_actors
        for item in self.players:
            r = col.row(align=True)
            r.prop(item, "include", text=item.label)
            r.label(text=item.detail)
        col.prop(self, "splice_peers")
        col.prop(self, "avatar_shading", text="Shading")
        box = layout.box()
        box.prop(self, "import_world_meshes")
        sub = box.column(align=True)
        sub.prop(self, "import_lights")
        sub.prop(self, "use_lightmaps")
        sub.prop(self, "build_world")
        sub.prop(self, "light_scale")
        box = layout.box()
        box.prop(self, "import_cameras")
        box.prop(self, "import_audio")
        sub = box.column()
        sub.enabled = self.import_audio
        sub.prop(self, "spatial_audio")
        box = layout.box()
        box.prop(self, "use_range")
        sub = box.row(align=True)
        sub.enabled = self.use_range
        sub.prop(self, "start_time")
        sub.prop(self, "end_time")
        box.prop(self, "resample")
        box.prop(self, "frame_step")

    def execute(self, context):
        self.load_take()
        folder = take_folder(self.filepath)
        if not folder:
            self.report({"ERROR"}, "Pick a take's capture.json")
            return {"CANCELLED"}
        include = {p.uuid for p in self.players if p.include} if len(self.players) else None
        time_range = (self.start_time, self.end_time) if self.use_range and self.end_time > self.start_time else None
        _, notes = build.import_capture(context, folder, import_actors=self.import_actors, players=include, import_scene=self.import_world_meshes or self.import_lights or self.build_world, import_world_meshes=self.import_world_meshes, import_lights=self.import_lights, build_world=self.build_world, import_cameras=self.import_cameras, import_audio=self.import_audio, splice_peers=self.splice_peers, resample=self.resample, frame_step=self.frame_step, avatar_shading=self.avatar_shading, use_lightmaps=self.use_lightmaps, spatial_audio=self.spatial_audio, light_scale=self.light_scale, time_range=time_range)
        for n in notes:
            self.report({"INFO"}, n)
        return {"FINISHED"}


def player_ids(context):
    obj = context.active_object
    while obj is not None and "basis_player" not in obj:
        obj = obj.parent
    return obj["basis_player"] if obj is not None else None


class ANIM_OT_basis_shift_player(bpy.types.Operator):
    bl_idname = "anim.basis_shift_player"
    bl_label = "Shift Player Timeline"
    bl_description = "Move the selected player's motion, face and voice strips together"
    bl_options = {"REGISTER", "UNDO"}

    frames: FloatProperty(name="Frames", default=1.0)

    @classmethod
    def poll(cls, context):
        return player_ids(context) is not None

    def execute(self, context):
        return bpy.ops.basis_capture.nudge_player(uuid=player_ids(context), frames=self.frames)


def menu_func(self, context):
    self.layout.operator(IMPORT_OT_basis_capture.bl_idname, text="Basis Capture (capture.json)")


classes = (BasisImportPlayer, IMPORT_OT_basis_capture, ANIM_OT_basis_shift_player)


def register():
    for c in classes:
        bpy.utils.register_class(c)
    ui.register()
    bpy.types.TOPBAR_MT_file_import.append(menu_func)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(menu_func)
    ui.unregister()
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
