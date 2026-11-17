import math
import os

import bpy
import numpy as np

from . import space

TOON_SHADERS = ("poiyomi", "liltoon", "lil/", "toon", "mtoon", "unitychan", "sunao")
POINT_WATTS = 4.0 * math.pi * math.pi
SUN_STRENGTH = math.pi


def lin(c):
    c = list(c) + [1.0] * (4 - len(c))
    return [float(v) for v in space.srgb_to_linear(c[:3])] + [float(c[3])]


def kelvin_rgb(k):
    t = max(1000.0, min(40000.0, k)) / 100.0
    r = 255.0 if t <= 66 else 329.698727446 * ((t - 60) ** -0.1332047592)
    g = 99.4708025861 * math.log(t) - 161.1195681661 if t <= 66 else 288.1221695283 * ((t - 60) ** -0.0755148492)
    b = 255.0 if t >= 66 else (0.0 if t <= 19 else 138.5177312231 * math.log(t - 10) - 305.0447927307)
    return [max(0.0, min(255.0, v)) / 255.0 for v in (r, g, b)]


def light_color(info):
    c = lin(info.get("color", [1, 1, 1, 1]))[:3]
    if info.get("useColorTemperature"):
        k = space.srgb_to_linear(kelvin_rgb(float(info.get("colorTemperature", 6500))))
        c = [c[i] * float(k[i]) for i in range(3)]
    return c


def light_energy(light_type, intensity):
    return intensity * (SUN_STRENGTH if light_type == "SUN" else POINT_WATTS)


class Props:
    def __init__(self, data):
        self.v = {p["name"]: p.get("value") for p in data.get("properties", [])}
        self.st = {p["name"]: (p.get("scale"), p.get("offset")) for p in data.get("properties", []) if p.get("type") == "Texture"}
        self.keywords = set(data.get("keywords", []))

    def f(self, *names, default=0.0):
        for n in names:
            v = self.v.get(n)
            if isinstance(v, (int, float)):
                return float(v)
        return default

    def c(self, *names, default=(1.0, 1.0, 1.0, 1.0)):
        for n in names:
            v = self.v.get(n)
            if isinstance(v, list) and len(v) >= 3:
                return v
        return list(default)

    def t(self, *names):
        for n in names:
            v = self.v.get(n)
            if isinstance(v, int) and v >= 0:
                return v, n
        return -1, None


POIYOMI_MODES = {0: "OPAQUE", 1: "CLIP", 9: "CLIP", 2: "BLEND", 3: "BLEND", 4: "BLEND", 5: "BLEND", 6: "BLEND", 7: "BLEND"}


def alpha_mode_of(shader, props, pbr):
    mode = pbr.get("alphaMode", "OPAQUE")
    if "poiyomi" in shader and "_Mode" in props.v:
        if props.f("_AlphaForceOpaque", default=0.0) > 0.5 and int(props.f("_Mode")) == 0:
            return "OPAQUE"
        return POIYOMI_MODES.get(int(props.f("_Mode")), mode)
    if "liltoon" in shader or "/lil" in shader:
        if "cutout" in shader:
            return "CLIP"
        if "transparent" in shader or "fade" in shader or "onepass" in shader or "twopass" in shader:
            return "BLEND"
    if mode == "OPAQUE" and props.f("_DstBlend", default=0.0) not in (0.0,) and props.f("_ZWrite", default=1.0) < 0.5:
        return "BLEND"
    return mode


def drive(socket, scene, prop):
    try:
        fc = socket.driver_add("default_value")
    except (TypeError, AttributeError):
        return
    d = fc.driver
    d.type = "AVERAGE"
    var = d.variables[0] if d.variables else d.variables.new()
    var.type = "SINGLE_PROP"
    var.targets[0].id_type = "SCENE"
    var.targets[0].id = scene
    var.targets[0].data_path = prop


class Builder:
    def __init__(self, importer):
        self.imp = importer
        self.nt = None

    def node(self, kind, x, y, **attrs):
        n = self.nt.nodes.new(kind)
        n.location = (x, y)
        for k, v in attrs.items():
            setattr(n, k, v)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def color_socket(self, node, index=0, output=False):
        sockets = node.outputs if output else node.inputs
        found = [s for s in sockets if s.type == "RGBA" and s.enabled]
        return found[index]

    def mix(self, mode, a, b, x, y, factor=1.0):
        m = self.node("ShaderNodeMix", x, y)
        m.data_type = "RGBA"
        m.blend_type = mode
        m.inputs[0].default_value = factor
        sa, sb = self.color_socket(m, 0), self.color_socket(m, 1)
        if isinstance(a, (list, tuple)):
            sa.default_value = list(a)
        else:
            self.link(a, sa)
        if isinstance(b, (list, tuple)):
            sb.default_value = list(b)
        else:
            self.link(b, sb)
        return self.color_socket(m, 0, True)

    def texture(self, tex_id, x, y, vector=None, uv_map=None, non_color=None):
        img = self.imp.image(tex_id)
        if img is None:
            return None
        if non_color is not None:
            try:
                img.colorspace_settings.name = "Non-Color" if non_color else "sRGB"
            except TypeError:
                pass
        t = self.node("ShaderNodeTexImage", x, y)
        t.image = img
        if uv_map is not None:
            uv = self.node("ShaderNodeUVMap", x - 200, y)
            uv.uv_map = uv_map
            self.link(uv.outputs["UV"], t.inputs["Vector"])
        elif vector is not None:
            self.link(vector, t.inputs["Vector"])
        return t

    def mapping(self, st, x, y):
        if not st:
            return None
        scale, offset = st
        if not scale or not offset or (list(scale) == [1, 1] and list(offset) == [0, 0]):
            return None
        coord = self.node("ShaderNodeUVMap", x - 200, y)
        coord.uv_map = "UV0"
        m = self.node("ShaderNodeMapping", x, y)
        m.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
        m.inputs["Location"].default_value = (offset[0], offset[1], 0.0)
        self.link(coord.outputs["UV"], m.inputs["Vector"])
        return m.outputs["Vector"]

    def alpha_mode(self, mat, mode, cutoff):
        if mode == "OPAQUE":
            return
        if hasattr(mat, "surface_render_method"):
            mat.surface_render_method = "DITHERED" if mode == "CLIP" else "BLENDED"
        else:
            try:
                mat.blend_method = "CLIP" if mode == "CLIP" else "BLEND"
                if mode == "CLIP":
                    mat.alpha_threshold = cutoff
            except (TypeError, AttributeError):
                pass
        for attr, value in (("use_transparent_shadow", True), ("shadow_method", "CLIP" if mode == "CLIP" else "HASHED"), ("use_transparency_overlap", True)):
            if hasattr(mat, attr):
                try:
                    setattr(mat, attr, value)
                except (TypeError, AttributeError):
                    pass

    def final_alpha(self, alpha, mode, cutoff):
        if alpha is None:
            alpha = 1.0
        if mode == "CLIP":
            if isinstance(alpha, (int, float)):
                return 1.0 if alpha >= cutoff else 0.0
            gt = self.node("ShaderNodeMath", 500, -400, operation="GREATER_THAN")
            gt.inputs[1].default_value = cutoff - 1e-4
            self.link(alpha, gt.inputs[0])
            return gt.outputs[0]
        return alpha

    def build(self, mid, data, lightmap=None, avatar=False):
        pbr = data.get("pbr", {})
        props = Props(data)
        shader = (data.get("shader") or "").lower()
        name = data.get("name", f"Material {mid}") + (f" [LM{lightmap[0]}]" if lightmap else "")
        mat = bpy.data.materials.new(name)
        mat["basis_shader"] = data.get("shader", "")
        mat["basis_material_id"] = mid
        for p in data.get("properties", []):
            v = p.get("value")
            if isinstance(v, (int, float)) or (isinstance(v, list) and len(v) <= 4):
                mat["u" + p["name"]] = v
        mat.use_nodes = True
        self.nt = mat.node_tree
        for n in list(self.nt.nodes):
            self.nt.nodes.remove(n)
        out = self.node("ShaderNodeOutputMaterial", 900, 0)
        toon = avatar and any(k in shader for k in TOON_SHADERS)
        if toon:
            toon_surface, alpha = self.toon(props, pbr)
            pbr_surface, _ = self.standard(props, pbr, None)
            switch = self.node("ShaderNodeMixShader", 400, 200)
            self.link(pbr_surface, switch.inputs[1])
            self.link(toon_surface, switch.inputs[2])
            switch.inputs[0].default_value = 1.0
            drive(switch.inputs[0], self.imp.scene, "basis_avatar_toon")
            surface = switch.outputs[0]
        else:
            surface, alpha = self.standard(props, pbr, lightmap)
        mode = alpha_mode_of(shader, props, pbr)
        cutoff = float(pbr.get("alphaCutoff", 0.5))
        if mode != "OPAQUE":
            a = self.final_alpha(alpha, mode, cutoff)
            if not (isinstance(a, (int, float)) and a >= 0.999):
                transparent = self.node("ShaderNodeBsdfTransparent", 500, -250)
                mix = self.node("ShaderNodeMixShader", 700, 0)
                if isinstance(a, (int, float)):
                    mix.inputs[0].default_value = a
                else:
                    self.link(a, mix.inputs[0])
                self.link(transparent.outputs[0], mix.inputs[1])
                self.link(surface, mix.inputs[2])
                surface = mix.outputs[0]
        mat["basis_alpha_mode"] = mode
        self.link(surface, out.inputs["Surface"])
        self.alpha_mode(mat, mode, cutoff)
        mat.use_backface_culling = int(pbr.get("cull", 2)) == 2
        return mat

    def base_color(self, props, pbr, x, y, vector):
        tint = lin(pbr.get("baseColor", [1, 1, 1, 1]))
        tex = self.texture(pbr.get("baseMap", -1), x, y, vector, non_color=False)
        if tex is None:
            return tint, float(tint[3]), None
        color = tex.outputs["Color"]
        if any(abs(c - 1.0) > 1e-4 for c in tint[:3]):
            color = self.mix("MULTIPLY", tint, color, x + 300, y)
        alpha = tex.outputs["Alpha"]
        if props.f("_MainIgnoreTexAlpha", default=0.0) > 0.5:
            alpha = float(tint[3])
        elif tint[3] < 0.999:
            m = self.node("ShaderNodeMath", x + 300, y - 150, operation="MULTIPLY")
            m.inputs[1].default_value = float(tint[3])
            self.link(alpha, m.inputs[0])
            alpha = m.outputs[0]
        return color, alpha, tex

    def standard(self, props, pbr, lightmap):
        st = props.st.get("_MainTex") or props.st.get("_BaseMap") or (pbr.get("baseMapScale"), pbr.get("baseMapOffset"))
        vector = self.mapping(st, -1100, 0)
        bsdf = self.node("ShaderNodeBsdfPrincipled", 300, 0)
        color, alpha, base_tex = self.base_color(props, pbr, -700, 300, vector)
        metallic = float(pbr.get("metallic", 0.0))
        smooth = float(pbr.get("smoothness", 0.5))
        gloss_scale = props.f("_GlossMapScale", default=1.0)
        from_albedo = props.f("_SmoothnessTextureChannel", default=0.0) > 0.5
        mtex = self.texture(pbr.get("metallicMap", -1), -700, 0, vector, non_color=True)
        if mtex is not None:
            sep = self.node("ShaderNodeSeparateColor", -450, 0)
            self.link(mtex.outputs["Color"], sep.inputs[0])
            self.link(sep.outputs[0], bsdf.inputs["Metallic"])
            src = alpha if (from_albedo and alpha is not None) else mtex.outputs["Alpha"]
            mul = self.node("ShaderNodeMath", -250, -80, operation="MULTIPLY")
            mul.inputs[1].default_value = gloss_scale
            if isinstance(src, (int, float)):
                mul.inputs[0].default_value = float(src)
            else:
                self.link(src, mul.inputs[0])
            inv = self.node("ShaderNodeMath", -80, -80, operation="SUBTRACT")
            inv.inputs[0].default_value = 1.0
            self.link(mul.outputs[0], inv.inputs[1])
            self.link(inv.outputs[0], bsdf.inputs["Roughness"])
        else:
            bsdf.inputs["Metallic"].default_value = metallic
            bsdf.inputs["Roughness"].default_value = 1.0 - smooth
        ntex = self.texture(pbr.get("normalMap", -1), -700, -350, vector, non_color=True)
        if ntex is not None:
            nm = self.node("ShaderNodeNormalMap", -400, -350)
            nm.uv_map = "UV0"
            nm.inputs["Strength"].default_value = float(pbr.get("normalScale", 1.0))
            self.link(ntex.outputs["Color"], nm.inputs["Color"])
            self.link(nm.outputs["Normal"], bsdf.inputs["Normal"])
        emission = self.emission(props, pbr, vector, color, -700, -650)
        occlusion = None
        otex = self.texture(pbr.get("occlusionMap", -1), -700, -950, vector, non_color=True)
        if otex is not None:
            strength = props.f("_OcclusionStrength", default=1.0)
            sep = self.node("ShaderNodeSeparateColor", -450, -950)
            self.link(otex.outputs["Color"], sep.inputs[0])
            ao = self.node("ShaderNodeMapRange", -250, -950)
            ao.inputs["To Min"].default_value = 1.0 - strength
            self.link(sep.outputs[1], ao.inputs["Value"])
            occlusion = ao.outputs["Result"]
        if lightmap is not None:
            lm = self.lightmap(lightmap, -700, 650, "_BAKERY_MONOSH" in props.keywords)
            if lm is not None:
                strength = self.node("ShaderNodeValue", -400, 800)
                strength.outputs[0].default_value = 1.0
                drive(strength.outputs[0], self.imp.scene, "basis_baked_strength")
                gray = self.node("ShaderNodeCombineColor", -250, 800)
                for i in range(3):
                    self.link(strength.outputs[0], gray.inputs[i])
                lm = self.mix("MULTIPLY", lm, gray.outputs[0], -150, 700)
                lit = self.mix("MULTIPLY", color, lm, -150, 550)
                if occlusion is not None:
                    occ = self.node("ShaderNodeCombineColor", -150, 400)
                    for i in range(3):
                        self.link(occlusion, occ.inputs[i])
                    lit = self.mix("MULTIPLY", lit, occ.outputs[0], 50, 550)
                emission = lit if emission is None else self.mix("ADD", lit, emission, 150, 450)
                bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
                color = None
        if color is not None:
            if isinstance(color, list):
                bsdf.inputs["Base Color"].default_value = color
            else:
                self.link(color, bsdf.inputs["Base Color"])
        if emission is not None:
            target = bsdf.inputs.get("Emission Color") or bsdf.inputs.get("Emission")
            self.link(emission, target)
            if "Emission Strength" in bsdf.inputs:
                bsdf.inputs["Emission Strength"].default_value = 1.0
        return bsdf.outputs[0], alpha

    def emission(self, props, pbr, vector, base, x, y):
        if not pbr.get("emissive"):
            return None
        strength = float(pbr.get("emissionStrength", 1.0))
        ec = lin(pbr.get("emissionColor", [0, 0, 0, 1]))
        ec = [ec[0] * strength, ec[1] * strength, ec[2] * strength, 1.0]
        if max(ec[:3]) <= 0:
            return None
        etex = self.texture(pbr.get("emissionMap", -1), x, y, vector, non_color=False)
        if etex is not None:
            return self.mix("MULTIPLY", ec, etex.outputs["Color"], x + 300, y)
        if props.f("_EmissionBaseColorAsMap", default=0.0) > 0.5 and not isinstance(base, list):
            return self.mix("MULTIPLY", ec, base, x + 300, y)
        mask, _ = props.t("_EmissionMask")
        mtex = self.texture(mask, x, y - 250, vector, non_color=True) if mask >= 0 else None
        if mtex is not None:
            return self.mix("MULTIPLY", ec, mtex.outputs["Color"], x + 300, y)
        return ec

    def lightmap(self, lightmap, x, y, monosh=False):
        index, tex_id, dir_id = lightmap
        base = self.lightmap_color(tex_id, x, y)
        if base is None or not monosh or dir_id is None or dir_id < 0:
            return base
        dtex = self.texture(dir_id, x, y - 400, uv_map="Lightmap", non_color=True)
        if dtex is None:
            return base
        nl1 = self.node("ShaderNodeVectorMath", x + 250, y - 400, operation="MULTIPLY_ADD")
        nl1.inputs[1].default_value = (2.0, 2.0, 2.0)
        nl1.inputs[2].default_value = (-1.0, -1.0, -1.0)
        self.link(dtex.outputs["Color"], nl1.inputs[0])
        geo = self.node("ShaderNodeNewGeometry", x, y - 650)
        sep = self.node("ShaderNodeSeparateXYZ", x + 200, y - 650)
        self.link(geo.outputs["Normal"], sep.inputs[0])
        neg_x = self.node("ShaderNodeMath", x + 350, y - 600, operation="MULTIPLY")
        neg_x.inputs[1].default_value = -1.0
        self.link(sep.outputs["X"], neg_x.inputs[0])
        neg_y = self.node("ShaderNodeMath", x + 350, y - 700, operation="MULTIPLY")
        neg_y.inputs[1].default_value = -1.0
        self.link(sep.outputs["Y"], neg_y.inputs[0])
        unity_n = self.node("ShaderNodeCombineXYZ", x + 500, y - 650)
        self.link(neg_x.outputs[0], unity_n.inputs["X"])
        self.link(sep.outputs["Z"], unity_n.inputs["Y"])
        self.link(neg_y.outputs[0], unity_n.inputs["Z"])
        dot = self.node("ShaderNodeVectorMath", x + 650, y - 500, operation="DOT_PRODUCT")
        self.link(unity_n.outputs[0], dot.inputs[0])
        self.link(nl1.outputs[0], dot.inputs[1])
        k = self.node("ShaderNodeMath", x + 800, y - 500, operation="MULTIPLY_ADD")
        k.inputs[1].default_value = 2.0
        k.inputs[2].default_value = 1.0
        self.link(dot.outputs["Value"], k.inputs[0])
        k.use_clamp = False
        floor = self.node("ShaderNodeMath", x + 950, y - 500, operation="MAXIMUM")
        floor.inputs[1].default_value = 0.0
        self.link(k.outputs[0], floor.inputs[0])
        gray = self.node("ShaderNodeCombineColor", x + 1100, y - 500)
        for i in range(3):
            self.link(floor.outputs[0], gray.inputs[i])
        return self.mix("MULTIPLY", base, gray.outputs[0], x + 1250, y - 300)

    def lightmap_color(self, tex_id, x, y):
        img_tex = self.texture(tex_id, x, y, uv_map="Lightmap", non_color=True)
        if img_tex is None:
            return None
        path = (img_tex.image.filepath or "").lower()
        info = self.imp.cap.texture_info(tex_id) or {}
        fmt = (info.get("format") or "").upper()
        if path.endswith(".exr") or "BC6H" in fmt or "HALF" in fmt or "FLOAT" in fmt:
            return img_tex.outputs["Color"]
        a = self.node("ShaderNodeMath", x + 250, y - 150, operation="POWER")
        a.inputs[1].default_value = 2.2
        self.link(img_tex.outputs["Alpha"], a.inputs[0])
        k = self.node("ShaderNodeMath", x + 420, y - 150, operation="MULTIPLY")
        k.inputs[1].default_value = 34.493242
        self.link(a.outputs[0], k.inputs[0])
        comb = self.node("ShaderNodeCombineColor", x + 580, y - 150)
        for i in range(3):
            self.link(k.outputs[0], comb.inputs[i])
        return self.mix("MULTIPLY", img_tex.outputs["Color"], comb.outputs[0], x + 750, y)

    def toon(self, props, pbr):
        st = props.st.get("_MainTex")
        vector = self.mapping(st, -1300, 0)
        color, alpha, _ = self.base_color(props, pbr, -1000, 300, vector)
        diffuse = self.node("ShaderNodeBsdfDiffuse", -1000, -100)
        diffuse.inputs["Color"].default_value = (1, 1, 1, 1)
        ntex = self.texture(pbr.get("normalMap", -1), -1300, -300, vector, non_color=True)
        if ntex is not None:
            nm = self.node("ShaderNodeNormalMap", -1100, -300)
            nm.uv_map = "UV0"
            nm.inputs["Strength"].default_value = float(pbr.get("normalScale", 1.0))
            self.link(ntex.outputs["Color"], nm.inputs["Color"])
            self.link(nm.outputs["Normal"], diffuse.inputs["Normal"])
        to_rgb = self.node("ShaderNodeShaderToRGB", -800, -100)
        self.link(diffuse.outputs[0], to_rgb.inputs[0])
        cap = props.f("_LightingCap", default=1.0)
        cap = cap if cap > 0 else 1.0
        clamp = self.node("ShaderNodeVectorMath", -600, -100, operation="MINIMUM")
        clamp.inputs[1].default_value = (cap, cap, cap)
        self.link(to_rgb.outputs["Color"], clamp.inputs[0])
        floor = props.f("_LightingMinLightBrightness", default=0.0)
        lifted = self.node("ShaderNodeVectorMath", -450, -100, operation="MAXIMUM")
        lifted.inputs[1].default_value = (floor, floor, floor)
        self.link(clamp.outputs[0], lifted.inputs[0])
        light = lifted.outputs[0]
        mode = int(props.f("_LightingMode", default=1.0))
        if mode not in (5, 6):
            bw = self.node("ShaderNodeRGBToBW", -450, -300)
            self.link(to_rgb.outputs["Color"], bw.inputs[0])
            ramp = self.node("ShaderNodeValToRGB", -280, -300)
            softness = max(0.02, props.f("_LightingShadowSoftness", "_ShadowBlur", default=0.0) * 0.5 + 0.03)
            border = 0.18
            ramp.color_ramp.elements[0].position = max(0.0, border - softness)
            ramp.color_ramp.elements[1].position = min(1.0, border + softness)
            self.link(bw.outputs[0], ramp.inputs["Fac"])
            shadow = lin(props.c("_LightingShadowColor", "_ShadowColor", "_Shadow1stColor", default=(0.7, 0.7, 0.75, 1)))
            strength = props.f("_ShadowStrength", default=1.0)
            shadow = [1 - strength + strength * shadow[i] for i in range(3)] + [1.0]
            tinted = self.mix("MULTIPLY", light, shadow, -100, -100)
            lit = self.mix("MIX", tinted, light, 80, -200)
            m = self.nt.nodes[-1]
            self.link(ramp.outputs["Color"], m.inputs[0])
            light = lit
        shaded = self.mix("MULTIPLY", color, light, 250, 0)
        if props.f("_EnableRimLighting", "_UseRim", default=0.0) > 0.5:
            rim_color = lin(props.c("_RimLightColor", "_RimColor", default=(1, 1, 1, 1)))
            rim_strength = props.f("_RimStrength", "_RimLightColorBias", default=0.4)
            lw = self.node("ShaderNodeLayerWeight", -100, -450)
            lw.inputs["Blend"].default_value = 0.35
            ramp = self.node("ShaderNodeValToRGB", 80, -450)
            ramp.color_ramp.elements[0].position = 0.55
            ramp.color_ramp.elements[1].position = 0.75
            self.link(lw.outputs["Facing"], ramp.inputs["Fac"])
            rim = self.mix("MULTIPLY", ramp.outputs["Color"], [c * rim_strength for c in rim_color[:3]] + [1], 250, -450)
            rim = self.mix("MULTIPLY", rim, light, 400, -450)
            shaded = self.mix("ADD", shaded, rim, 450, -100)
        emission = self.emission(props, pbr, vector, color, -1000, -700)
        if emission is not None:
            shaded = self.mix("ADD", shaded, emission, 600, -150)
        emit = self.node("ShaderNodeEmission", 700, 0)
        self.link(shaded, emit.inputs["Color"])
        return emit.outputs[0], alpha


def build_world(importer):
    scene = importer.scene
    env = importer.cap.manifest.get("environment") or {}
    probe = env.get("lightProbe") or env.get("ambientProbe")
    world = scene.world or bpy.data.worlds.new("Basis World")
    scene.world = world
    world.use_nodes = True
    nt = world.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputWorld")
    out.location = (600, 0)
    bg = nt.nodes.new("ShaderNodeBackground")
    bg.location = (400, 0)
    nt.links.new(bg.outputs[0], out.inputs[0])
    hdri = env.get("hdri") or {}
    hdri_path = os.path.join(importer.folder, hdri["file"]) if hdri.get("file") else None
    if hdri_path and os.path.exists(hdri_path):
        tex = nt.nodes.new("ShaderNodeTexEnvironment")
        tex.location = (0, 0)
        tex.image = bpy.data.images.load(hdri_path, check_existing=True)
        try:
            tex.image.colorspace_settings.name = "Linear Rec.709"
        except TypeError:
            pass
        color = tex.outputs["Color"]
        sky_path = os.path.join(importer.folder, hdri["sky"]) if hdri.get("sky") else None
        if sky_path and os.path.exists(sky_path):
            sky = nt.nodes.new("ShaderNodeTexEnvironment")
            sky.location = (0, -300)
            sky.image = bpy.data.images.load(sky_path, check_existing=True)
            try:
                sky.image.colorspace_settings.name = "Linear Rec.709"
            except TypeError:
                pass
            path = nt.nodes.new("ShaderNodeLightPath")
            path.location = (0, 300)
            mix = nt.nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            mix.location = (200, 0)
            nt.links.new(path.outputs["Is Camera Ray"], mix.inputs[0])
            cols = [i for i in mix.inputs if i.type == "RGBA" and i.enabled]
            nt.links.new(tex.outputs["Color"], cols[0])
            nt.links.new(sky.outputs["Color"], cols[1])
            color = next(o for o in mix.outputs if o.type == "RGBA" and o.enabled)
        nt.links.new(color, bg.inputs["Color"])
        bg.inputs["Strength"].default_value = 1.0
        return f"world: environment captured in Basis at {hdri.get('position')}" + (" with its sky" if sky_path else "")
    if not probe or not any(max(probe[k][:3]) > 0 for k in ("up", "down", "forward", "back", "left", "right")):
        flat = env.get("ambientSkyColor") or [0.2, 0.2, 0.25, 1]
        bg.inputs[0].default_value = lin(flat)
        return "world: no probe data, flat ambient"
    up, down = probe["up"], probe["down"]
    side = [(probe["forward"][i] + probe["back"][i] + probe["left"][i] + probe["right"][i]) / 4.0 for i in range(3)]
    coord = nt.nodes.new("ShaderNodeTexCoord")
    coord.location = (-400, 0)
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    sep.location = (-200, 0)
    nt.links.new(coord.outputs["Generated"], sep.inputs[0])
    remap = nt.nodes.new("ShaderNodeMapRange")
    remap.location = (-100, 150)
    remap.inputs["From Min"].default_value = -1.0
    nt.links.new(sep.outputs["Z"], remap.inputs["Value"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.location = (0, 0)
    nt.links.new(remap.outputs["Result"], ramp.inputs["Fac"])
    els = ramp.color_ramp.elements
    els[0].position, els[0].color = 0.0, [max(0.0, v) for v in down[:3]] + [1]
    els[1].position, els[1].color = 1.0, [max(0.0, v) for v in up[:3]] + [1]
    mid = els.new(0.5)
    mid.color = [max(0.0, v) for v in side] + [1]
    nt.links.new(ramp.outputs["Color"], bg.inputs["Color"])
    bg.inputs["Strength"].default_value = 1.0
    return f"world: light probe ambient at {env.get('probePosition')}"


VIEW_TRANSFORMS = {"None": ["Standard"], "AgX": ["AgX", "Standard"], "Neutral": ["Khronos PBR Neutral", "Standard"], "ACES": ["ACES 2.0", "ACES 1.3", "Filmic", "Standard"]}


def apply_post(importer):
    scene = importer.scene
    env = importer.cap.manifest.get("environment") or {}
    post = env.get("post") or {}
    vs = scene.view_settings
    mode = post.get("tonemapping", "None") if post.get("enabled", True) else "None"
    for name in VIEW_TRANSFORMS.get(mode, ["Standard"]):
        try:
            vs.view_transform = name
            break
        except TypeError:
            continue
    vs.look = "None"
    vs.exposure = float(post.get("exposure", 0.0)) if post.get("enabled", True) else 0.0
    try:
        scene.eevee.shadow_pool_size = "1024"
    except (AttributeError, TypeError):
        pass
    if "bloomIntensity" in post:
        scene["basis_bloom"] = [post.get("bloomIntensity", 0.0), post.get("bloomThreshold", 0.9), post.get("bloomScatter", 0.7)]
    return f"view: {vs.view_transform}, exposure {vs.exposure:+.2f} (Unity tonemapping {mode})"
