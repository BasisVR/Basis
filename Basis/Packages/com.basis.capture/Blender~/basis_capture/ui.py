import os

import bpy
from bpy.props import BoolProperty, FloatProperty, IntProperty, StringProperty
from mathutils import Vector


def update_lights(scene, context):
    for light in bpy.data.lights:
        base = light.get("basis_base_energy")
        if base is not None:
            light.energy = base * scene.basis_light_scale


def update_world(scene, context):
    world = scene.world
    if world is None or world.node_tree is None:
        return
    for node in world.node_tree.nodes:
        if node.type == "BACKGROUND":
            node.inputs["Strength"].default_value = scene.basis_environment_strength


def players(context=None):
    found = {}
    for o in bpy.data.objects:
        uuid = o.get("basis_player")
        if uuid and o.type == "ARMATURE":
            found.setdefault(uuid, o)
    return sorted(found.items(), key=lambda kv: kv[1].name)


def owned(uuid):
    return [o for o in bpy.data.objects if o.get("basis_player") == uuid]


def speaker(uuid):
    return next((o for o in owned(uuid) if o.type == "SPEAKER"), None)


class BASIS_OT_player_visibility(bpy.types.Operator):
    bl_idname = "basis_capture.player_visibility"
    bl_label = "Show or Hide Player"
    bl_description = "Show or hide this player's avatar and voice"
    bl_options = {"REGISTER", "UNDO"}
    uuid: StringProperty()
    solo: BoolProperty(default=False)

    def execute(self, context):
        if self.solo:
            for uuid, _ in players():
                show = uuid == self.uuid
                for o in owned(uuid):
                    o.hide_set(not show)
                    o.hide_render = not show
                    if o.type == "SPEAKER":
                        o.data.muted = not show
            return {"FINISHED"}
        objs = owned(self.uuid)
        show = any(o.hide_get() for o in objs)
        for o in objs:
            o.hide_set(not show)
            o.hide_render = not show
            if o.type == "SPEAKER":
                o.data.muted = not show
        return {"FINISHED"}


class BASIS_OT_show_all(bpy.types.Operator):
    bl_idname = "basis_capture.show_all"
    bl_label = "Show Everyone"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        for uuid, _ in players():
            for o in owned(uuid):
                o.hide_set(False)
                o.hide_render = False
                if o.type == "SPEAKER":
                    o.data.muted = False
        return {"FINISHED"}


class BASIS_OT_select_player(bpy.types.Operator):
    bl_idname = "basis_capture.select_player"
    bl_label = "Select Player"
    bl_options = {"REGISTER", "UNDO"}
    uuid: StringProperty()

    def execute(self, context):
        arm = dict(players()).get(self.uuid)
        if arm is None:
            return {"CANCELLED"}
        for o in context.view_layer.objects:
            o.select_set(False)
        arm.hide_set(False)
        arm.select_set(True)
        context.view_layer.objects.active = arm
        return {"FINISHED"}


class BASIS_OT_nudge_player(bpy.types.Operator):
    bl_idname = "basis_capture.nudge_player"
    bl_label = "Nudge Player Timeline"
    bl_description = "Move this player's motion, face and voice in time"
    bl_options = {"REGISTER", "UNDO"}
    uuid: StringProperty()
    frames: FloatProperty(default=1.0)

    def execute(self, context):
        moved = 0
        for idb in list(bpy.data.objects) + list(bpy.data.shape_keys):
            if idb.get("basis_player") != self.uuid or idb.animation_data is None:
                continue
            for track in idb.animation_data.nla_tracks:
                for strip in sorted(track.strips, key=lambda s: -s.frame_start if self.frames > 0 else s.frame_start):
                    if hasattr(strip, "frame_start_ui"):
                        strip.frame_start_ui += self.frames
                    else:
                        strip.frame_end += self.frames
                        strip.frame_start += self.frames
                    moved += 1
        arm = dict(players()).get(self.uuid)
        if arm is not None:
            arm["basis_offset"] = arm.get("basis_offset", 0.0) + self.frames
        context.view_layer.update()
        return {"FINISHED"} if moved else {"CANCELLED"}


class BASIS_OT_follow_camera(bpy.types.Operator):
    bl_idname = "basis_capture.follow_camera"
    bl_label = "Add Follow Camera"
    bl_description = "Add a camera that follows this player from behind and looks at their head"
    bl_options = {"REGISTER", "UNDO"}
    uuid: StringProperty()

    def execute(self, context):
        arm = dict(players()).get(self.uuid)
        if arm is None:
            return {"CANCELLED"}
        head = "Head" if arm.pose.bones.get("Head") else None
        cam = bpy.data.objects.new(f"Follow {arm.name}", bpy.data.cameras.new(f"Follow {arm.name}"))
        cam.data.lens = 24
        cam.data.clip_start = 0.05
        context.scene.collection.objects.link(cam)
        cam.parent = arm
        cam.location = (0.0, 1.6, 1.9)
        track = cam.constraints.new("DAMPED_TRACK")
        track.target = arm
        if head:
            track.subtarget = head
        track.track_axis = "TRACK_NEGATIVE_Z"
        context.scene.camera = cam
        cam["basis_player"] = self.uuid
        return {"FINISHED"}


class BASIS_OT_use_camera(bpy.types.Operator):
    bl_idname = "basis_capture.use_camera"
    bl_label = "Use Camera"
    bl_options = {"REGISTER", "UNDO"}
    name: StringProperty()

    def execute(self, context):
        cam = bpy.data.objects.get(self.name)
        if cam is None or cam.type != "CAMERA":
            return {"CANCELLED"}
        context.scene.camera = cam
        return {"FINISHED"}


class BASIS_OT_reimport(bpy.types.Operator):
    bl_idname = "basis_capture.reimport"
    bl_label = "Import Again"
    bl_description = "Import this take again with different options"

    def execute(self, context):
        folder = context.scene.get("basis_take", {}).get("folder") if context.scene.get("basis_take") else None
        path = os.path.join(folder, "capture.json") if folder else ""
        bpy.ops.import_scene.basis_capture("INVOKE_DEFAULT", filepath=path)
        return {"FINISHED"}


class VIEW3D_PT_basis_capture(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Basis Capture"
    bl_label = "Basis Capture"

    def draw(self, context):
        layout = self.layout
        take = context.scene.get("basis_take")
        row = layout.row(align=True)
        row.operator("import_scene.basis_capture", text="Import Take", icon="IMPORT")
        if take:
            row.operator("basis_capture.reimport", text="", icon="FILE_REFRESH")
        if not take:
            layout.label(text="No take imported yet", icon="INFO")
            return
        box = layout.box()
        col = box.column(align=True)
        col.label(text=take.get("world") or "Unknown world", icon="WORLD")
        col.label(text=f"{take.get('duration', 0):.1f} s at {take.get('rate') or 'every frame'} Hz, {take.get('players', 0)} players", icon="TIME")
        col.label(text=take.get("recorder") or "", icon="USER")
        col.operator("wm.path_open", text="Open Take Folder", icon="FILE_FOLDER").filepath = take.get("folder", "")


class VIEW3D_PT_basis_capture_players(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Basis Capture"
    bl_label = "Players"
    bl_parent_id = "VIEW3D_PT_basis_capture"

    @classmethod
    def poll(cls, context):
        return bool(context.scene.get("basis_take"))

    def draw(self, context):
        layout = self.layout
        layout.operator("basis_capture.show_all", icon="HIDE_OFF")
        for uuid, arm in players():
            box = layout.box()
            head = box.row(align=True)
            hidden = arm.hide_get()
            vis = head.operator("basis_capture.player_visibility", text="", icon="HIDE_ON" if hidden else "HIDE_OFF", emboss=False)
            vis.uuid = uuid
            vis.solo = False
            name = arm.get("basis_player_label") or arm.name
            tag = "  (own capture)" if arm.get("basis_spliced") else ("  (recorder)" if arm.get("basis_is_local") else "")
            sel = head.operator("basis_capture.select_player", text=name + tag, emboss=False)
            sel.uuid = uuid
            solo = head.operator("basis_capture.player_visibility", text="", icon="SOLO_ON")
            solo.uuid = uuid
            solo.solo = True
            cam = head.operator("basis_capture.follow_camera", text="", icon="OUTLINER_OB_CAMERA")
            cam.uuid = uuid
            row = box.row(align=True)
            row.label(text=f"Offset {arm.get('basis_offset', 0.0):+.0f} f")
            for step in (-10, -1, 1, 10):
                op = row.operator("basis_capture.nudge_player", text=f"{step:+d}")
                op.uuid = uuid
                op.frames = step
            spk = speaker(uuid)
            if spk is not None:
                row = box.row(align=True)
                row.prop(spk.data, "muted", text="", icon="MUTE_IPO_ON" if spk.data.muted else "MUTE_IPO_OFF", emboss=False)
                row.prop(spk.data, "volume", text="Voice", slider=True)


class VIEW3D_PT_basis_capture_look(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Basis Capture"
    bl_label = "Look"
    bl_parent_id = "VIEW3D_PT_basis_capture"

    @classmethod
    def poll(cls, context):
        return bool(context.scene.get("basis_take"))

    def draw(self, context):
        scene = context.scene
        col = self.layout.column()
        col.prop(scene, "basis_avatar_toon", text="Toon Avatars (Eevee)")
        col.prop(scene, "basis_baked_strength", text="Baked Lighting", slider=True)
        col.prop(scene, "basis_environment_strength", text="Environment", slider=True)
        col.prop(scene, "basis_light_scale", text="Lights", slider=True)
        col.separator()
        col.prop(scene.view_settings, "view_transform", text="View")
        col.prop(scene.view_settings, "exposure")
        if scene.render.engine == "CYCLES" and scene.basis_avatar_toon:
            col.label(text="Toon avatars need Eevee; Cycles shows them as PBR", icon="ERROR")


class VIEW3D_PT_basis_capture_cameras(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Basis Capture"
    bl_label = "Cameras"
    bl_parent_id = "VIEW3D_PT_basis_capture"
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return bool(context.scene.get("basis_take"))

    def draw(self, context):
        col = self.layout.column(align=True)
        for cam in sorted((o for o in bpy.data.objects if o.type == "CAMERA"), key=lambda o: o.name):
            active = context.scene.camera == cam
            op = col.operator("basis_capture.use_camera", text=cam.name, icon="RADIOBUT_ON" if active else "RADIOBUT_OFF", depress=active)
            op.name = cam.name


classes = (BASIS_OT_player_visibility, BASIS_OT_show_all, BASIS_OT_select_player, BASIS_OT_nudge_player, BASIS_OT_follow_camera, BASIS_OT_use_camera, BASIS_OT_reimport, VIEW3D_PT_basis_capture, VIEW3D_PT_basis_capture_players, VIEW3D_PT_basis_capture_look, VIEW3D_PT_basis_capture_cameras)


def register():
    bpy.types.Scene.basis_avatar_toon = BoolProperty(name="Toon Avatars", default=True)
    bpy.types.Scene.basis_baked_strength = FloatProperty(name="Baked Lighting", default=1.0, min=0.0, soft_max=4.0)
    bpy.types.Scene.basis_environment_strength = FloatProperty(name="Environment", default=1.0, min=0.0, soft_max=4.0, update=update_world)
    bpy.types.Scene.basis_light_scale = FloatProperty(name="Lights", default=1.0, min=0.0, soft_max=10.0, update=update_lights)
    for c in classes:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
    for p in ("basis_avatar_toon", "basis_baked_strength", "basis_environment_strength", "basis_light_scale"):
        if hasattr(bpy.types.Scene, p):
            delattr(bpy.types.Scene, p)
