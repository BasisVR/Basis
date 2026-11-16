using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class BasisVrmDocument
{
    public struct Bind
    {
        public int Node, Mesh, Index;
        public float Weight;
    }
    public struct Spring
    {
        public int[] Roots;
        public int[] ColliderGroups;
        public float Stiffness, Drag, Gravity, HitRadius;
    }
    public struct Collider
    {
        public int Node;
        public bool Capsule;
        public Vector3 Offset, Tail;
        public float Radius;
    }
    public struct Annotation
    {
        public int Node, Mesh;
        public bool ThirdPersonOnly, FirstPersonOnly;
    }
    public enum AvatarUse
    {
        Unknown,
        OnlyAuthor,
        LicensedOnly,
        Everyone,
    }
    public sealed class MToon
    {
        public string AlphaMode = "OPAQUE";
        public float Cutoff = 0.5f;
        public bool DoubleSided, TransparentWithZWrite;
        public int QueueOffset;
        public Color BaseColor = Color.white, ShadeColor = Color.white, Emission = Color.black, Matcap = Color.black, Rim = Color.black, OutlineColor = Color.black;
        public int BaseTex = -1, ShadeTex = -1, NormalTex = -1, ShadingShiftTex = -1, EmissionTex = -1, MatcapTex = -1, RimTex = -1, OutlineWidthTex = -1, UvMaskTex = -1;
        public float NormalScale = 1f, ShadingShift = -0.05f, ShadingToony = 0.95f, ShadingShiftScale = 1f, GiEqualization = 0.9f, RimPower = 5f, RimLift, RimMix = 1f, OutlineWidth, OutlineMix = 1f, ScrollX, ScrollY, Rotation;
        public int OutlineMode;
        public Vector4 MainST = new Vector4(1f, 1f, 0f, 0f);
    }
    public int SpecVersion;
    public string Title, Author, Contact, LicenseText, Credits;
    public AvatarUse Use = AvatarUse.Unknown;
    public bool RedistributionAllowed = true;
    public int ThumbnailImage = -1;
    public long ThumbnailOffset = -1;
    public int ThumbnailLength;
    public readonly Dictionary<HumanBodyBones, int> Bones = new Dictionary<HumanBodyBones, int>();
    public readonly Dictionary<string, List<Bind>> Expressions = new Dictionary<string, List<Bind>>(StringComparer.OrdinalIgnoreCase);
    public readonly List<Spring> Springs = new List<Spring>();
    public readonly List<Collider> Colliders = new List<Collider>();
    public readonly List<int[]> ColliderGroups = new List<int[]>();
    public readonly List<Annotation> FirstPerson = new List<Annotation>();
    public readonly Dictionary<int, MToon> Materials = new Dictionary<int, MToon>();
    public Vector3 EyeOffset;
    public bool HasEyeOffset;
    public Vector3 ConvertLocal(Vector3 vrm) => SpecVersion == 0 ? new Vector3(-vrm.x, vrm.y, -vrm.z) : new Vector3(-vrm.x, vrm.y, vrm.z);
    public static bool LooksLikeGlb(byte[] d) => d != null && d.Length >= 20 && d[0] == (byte)'g' && d[1] == (byte)'l' && d[2] == (byte)'T' && d[3] == (byte)'F' && BitConverter.ToUInt32(d, 4) == 2;
    public static bool TryGetJsonChunk(byte[] header, long available, out int length, out string error)
    {
        length = 0;
        error = null;
        if (!LooksLikeGlb(header))
        {
            error = "The file is not a binary glTF (VRM) file.";
            return false;
        }
        uint total = BitConverter.ToUInt32(header, 8), chunk = BitConverter.ToUInt32(header, 12), type = BitConverter.ToUInt32(header, 16);
        if (total > available || type != 0x4E4F534A || 20L + chunk > total)
        {
            error = "The file's glTF header is damaged.";
            return false;
        }
        length = (int)chunk;
        return true;
    }
    public static bool TryParse(byte[] glb, out BasisVrmDocument document, out string error)
    {
        document = null;
        if (!TryGetJsonChunk(glb, glb?.LongLength ?? 0, out int length, out error)) return false;
        return TryParseJson(Encoding.UTF8.GetString(glb, 20, length), length, out document, out error);
    }
    public static bool TryParseJson(string json, int jsonChunkLength, out BasisVrmDocument document, out string error)
    {
        document = null;
        error = null;
        try
        {
            JObject root = JObject.Parse(json);
            JObject extensions = root["extensions"] as JObject;
            document = new BasisVrmDocument();
            if (extensions?["VRMC_vrm"] is JObject vrm1)
            {
                document.ReadVrm1(root, vrm1, extensions["VRMC_springBone"] as JObject);
            }
            else if (extensions?["VRM"] is JObject vrm0)
            {
                document.ReadVrm0(root, vrm0);
            }
            else
            {
                document = null;
                error = "The file has no VRM data.";
                return false;
            }
            if (!document.Bones.ContainsKey(HumanBodyBones.Hips) || !document.Bones.ContainsKey(HumanBodyBones.Head))
            {
                document = null;
                error = "The VRM humanoid is missing its hips or head.";
                return false;
            }
            document.ResolveThumbnail(root, jsonChunkLength);
            return true;
        }
        catch (Exception exception)
        {
            document = null;
            error = "The VRM data could not be read: " + exception.Message;
            return false;
        }
    }
    private void ResolveThumbnail(JObject root, int jsonChunkLength)
    {
        if (ThumbnailImage < 0 || !(root["images"] is JArray images) || ThumbnailImage >= images.Count) return;
        int view = Int(images[ThumbnailImage]["bufferView"], -1);
        if (view < 0 || !(root["bufferViews"] is JArray views) || view >= views.Count || Int(views[view]["buffer"], 0) != 0) return;
        long offset = Long(views[view]["byteOffset"], 0), length = Long(views[view]["byteLength"], 0);
        if (offset < 0 || length <= 0 || length > 4 * 1024 * 1024) return;
        ThumbnailOffset = 20L + jsonChunkLength + 8L + offset;
        ThumbnailLength = (int)length;
    }
    private void ReadVrm1(JObject root, JObject vrm, JObject springBone)
    {
        SpecVersion = 1;
        JObject meta = vrm["meta"] as JObject;
        Title = (string)meta?["name"];
        if (meta?["authors"] is JArray authors && authors.Count > 0) Author = string.Join(", ", authors.Values<string>());
        Contact = (string)meta?["contactInformation"];
        ThumbnailImage = Int(meta?["thumbnailImage"], -1);
        string permission = (string)meta?["avatarPermission"];
        Use = permission == "onlyAuthor" ? AvatarUse.OnlyAuthor : permission == "onlySeparatelyLicensedPerson" ? AvatarUse.LicensedOnly : permission == "everyone" ? AvatarUse.Everyone : AvatarUse.Unknown;
        RedistributionAllowed = meta?["allowRedistribution"] is JValue redistribution && redistribution.Type == JTokenType.Boolean && (bool)redistribution;
        List<string> licence = new List<string>();
        AddLicence(licence, "Avatar use", permission);
        AddLicence(licence, "Commercial use", (string)meta?["commercialUsage"]);
        AddLicence(licence, "Redistribution", RedistributionAllowed ? "allowed" : "not allowed");
        AddLicence(licence, "Modification", (string)meta?["modification"]);
        AddLicence(licence, "Credit", (string)meta?["creditNotation"]);
        AddLicence(licence, "Violent use", Flag(meta?["allowExcessivelyViolentUsage"]));
        AddLicence(licence, "Sexual use", Flag(meta?["allowExcessivelySexualUsage"]));
        AddLicence(licence, "Political or religious use", Flag(meta?["allowPoliticalOrReligiousUsage"]));
        AddLicence(licence, "Antisocial or hate use", Flag(meta?["allowAntisocialOrHateUsage"]));
        AddLicence(licence, "License", (string)meta?["licenseUrl"]);
        AddLicence(licence, "Other license", (string)meta?["otherLicenseUrl"]);
        LicenseText = licence.Count > 0 ? string.Join("\n", licence) : null;
        Credits = (string)meta?["copyrightInformation"];
        if (vrm["humanoid"]?["humanBones"] is JObject bones)
        {
            foreach (KeyValuePair<string, JToken> bone in bones)
            {
                int node = Int(bone.Value?["node"], -1);
                if (node >= 0 && TryMapBone(bone.Key, true, out HumanBodyBones human)) Bones[human] = node;
            }
        }
        foreach (string group in new[] { "preset", "custom" })
        {
            if (!(vrm["expressions"]?[group] is JObject expressions)) continue;
            foreach (KeyValuePair<string, JToken> expression in expressions)
            {
                if (!(expression.Value?["morphTargetBinds"] is JArray binds)) continue;
                List<Bind> list = new List<Bind>();
                foreach (JToken bind in binds) list.Add(new Bind { Node = Int(bind["node"], -1), Mesh = -1, Index = Int(bind["index"], -1), Weight = Float(bind["weight"], 1f) });
                if (!Expressions.ContainsKey(expression.Key)) Expressions[expression.Key] = list;
            }
        }
        if (vrm["lookAt"]?["offsetFromHeadBone"] is JArray offset && offset.Count == 3)
        {
            EyeOffset = new Vector3(Float(offset[0], 0f), Float(offset[1], 0f), Float(offset[2], 0f));
            HasEyeOffset = true;
        }
        if (vrm["firstPerson"]?["meshAnnotations"] is JArray annotations)
        {
            foreach (JToken annotation in annotations)
            {
                string type = (string)annotation["type"];
                FirstPerson.Add(new Annotation { Node = Int(annotation["node"], -1), Mesh = -1, ThirdPersonOnly = type == "thirdPersonOnly", FirstPersonOnly = type == "firstPersonOnly" });
            }
        }
        if (springBone?["colliders"] is JArray colliders)
        {
            foreach (JToken collider in colliders)
            {
                JToken shape = collider["shape"];
                JToken capsule = shape?["capsule"], sphere = shape?["sphere"];
                JToken body = capsule ?? sphere;
                if (body == null) continue;
                Colliders.Add(new Collider { Node = Int(collider["node"], -1), Capsule = capsule != null, Offset = Vec(body["offset"]), Tail = Vec(body["tail"]), Radius = Float(body["radius"], 0f) });
            }
        }
        if (springBone?["colliderGroups"] is JArray groups)
        {
            foreach (JToken group in groups) ColliderGroups.Add(Ints(group["colliders"]));
        }
        if (springBone?["springs"] is JArray springs)
        {
            foreach (JToken spring in springs)
            {
                if (!(spring["joints"] is JArray joints) || joints.Count < 2) continue;
                JToken first = joints[0];
                Springs.Add(new Spring { Roots = new[] { Int(first["node"], -1) }, ColliderGroups = Ints(spring["colliderGroups"]), Stiffness = Float(first["stiffness"], 1f), Drag = Float(first["dragForce"], 0.5f), Gravity = Float(first["gravityPower"], 0f), HitRadius = Float(first["hitRadius"], 0f) });
            }
        }
        if (root["materials"] is JArray materials)
        {
            for (int Index = 0; Index < materials.Count; Index++)
            {
                JObject material = materials[Index] as JObject;
                if (material?["extensions"]?["VRMC_materials_mtoon"] is JObject mtoon) Materials[Index] = ReadMToon1(root, material, mtoon);
            }
        }
    }
    private static MToon ReadMToon1(JObject root, JObject material, JObject mtoon)
    {
        MToon m = new MToon();
        m.AlphaMode = ((string)material["alphaMode"] ?? "OPAQUE").ToUpperInvariant();
        m.Cutoff = Float(material["alphaCutoff"], 0.5f);
        m.DoubleSided = material["doubleSided"] is JValue doubleSided && doubleSided.Type == JTokenType.Boolean && (bool)doubleSided;
        m.TransparentWithZWrite = mtoon["transparentWithZWrite"] is JValue zwrite && zwrite.Type == JTokenType.Boolean && (bool)zwrite;
        m.QueueOffset = Int(mtoon["renderQueueOffsetNumber"], 0);
        JToken pbr = material["pbrMetallicRoughness"];
        m.BaseColor = LinearColor(pbr?["baseColorFactor"], Color.white);
        m.BaseTex = TexIndex(pbr?["baseColorTexture"]);
        JToken transform = pbr?["baseColorTexture"]?["extensions"]?["KHR_texture_transform"];
        if (transform != null)
        {
            Vector3 scale = Vec(transform["scale"], Vector3.one), offset = Vec(transform["offset"], Vector3.zero);
            m.MainST = new Vector4(scale.x, scale.y, offset.x, 1f - offset.y - scale.y);
        }
        m.NormalTex = TexIndex(material["normalTexture"]);
        m.NormalScale = Float(material["normalTexture"]?["scale"], 1f);
        float emissiveStrength = Float(material["extensions"]?["KHR_materials_emissive_strength"]?["emissiveStrength"], 1f);
        m.Emission = LinearColor(material["emissiveFactor"], Color.black, emissiveStrength);
        m.EmissionTex = TexIndex(material["emissiveTexture"]);
        m.ShadeColor = LinearColor(mtoon["shadeColorFactor"], Color.white);
        m.ShadeTex = TexIndex(mtoon["shadeMultiplyTexture"]);
        m.ShadingShift = Float(mtoon["shadingShiftFactor"], 0f);
        m.ShadingShiftTex = TexIndex(mtoon["shadingShiftTexture"]);
        m.ShadingShiftScale = Float(mtoon["shadingShiftTexture"]?["scale"], 1f);
        m.ShadingToony = Float(mtoon["shadingToonyFactor"], 0.9f);
        m.GiEqualization = Float(mtoon["giEqualizationFactor"], 0.9f);
        m.Matcap = LinearColor(mtoon["matcapFactor"], Color.white);
        m.MatcapTex = TexIndex(mtoon["matcapTexture"]);
        if (m.MatcapTex < 0) m.Matcap = Color.black;
        m.Rim = LinearColor(mtoon["parametricRimColorFactor"], Color.black);
        m.RimPower = Float(mtoon["parametricRimFresnelPowerFactor"], 5f);
        m.RimLift = Float(mtoon["parametricRimLiftFactor"], 0f);
        m.RimTex = TexIndex(mtoon["rimMultiplyTexture"]);
        m.RimMix = Float(mtoon["rimLightingMixFactor"], 1f);
        string outlineMode = (string)mtoon["outlineWidthMode"];
        m.OutlineMode = outlineMode == "worldCoordinates" ? 1 : outlineMode == "screenCoordinates" ? 2 : 0;
        m.OutlineWidth = Float(mtoon["outlineWidthFactor"], 0f);
        m.OutlineWidthTex = TexIndex(mtoon["outlineWidthMultiplyTexture"]);
        m.OutlineColor = LinearColor(mtoon["outlineColorFactor"], Color.black);
        m.OutlineMix = Float(mtoon["outlineLightingMixFactor"], 1f);
        m.UvMaskTex = TexIndex(mtoon["uvAnimationMaskTexture"]);
        m.ScrollX = Float(mtoon["uvAnimationScrollXSpeedFactor"], 0f);
        m.ScrollY = -Float(mtoon["uvAnimationScrollYSpeedFactor"], 0f);
        m.Rotation = -Float(mtoon["uvAnimationRotationSpeedFactor"], 0f);
        return m;
    }
    private void ReadVrm0(JObject root, JObject vrm)
    {
        SpecVersion = 0;
        JObject meta = vrm["meta"] as JObject;
        Title = (string)meta?["title"];
        Author = (string)meta?["author"];
        Contact = (string)meta?["contactInformation"];
        int thumbnailTexture = Int(meta?["texture"], -1);
        if (thumbnailTexture >= 0 && root["textures"] is JArray textures && thumbnailTexture < textures.Count) ThumbnailImage = Int(textures[thumbnailTexture]["source"], -1);
        string allowed = (string)meta?["allowedUserName"];
        Use = allowed == "OnlyAuthor" ? AvatarUse.OnlyAuthor : allowed == "ExplicitlyLicensedPerson" ? AvatarUse.LicensedOnly : allowed == "Everyone" ? AvatarUse.Everyone : AvatarUse.Unknown;
        string licenceName = (string)meta?["licenseName"];
        RedistributionAllowed = licenceName != "Redistribution_Prohibited";
        List<string> licence = new List<string>();
        AddLicence(licence, "Avatar use", allowed);
        AddLicence(licence, "Commercial use", (string)meta?["commercialUssageName"]);
        AddLicence(licence, "Violent use", (string)meta?["violentUssageName"]);
        AddLicence(licence, "Sexual use", (string)meta?["sexualUssageName"]);
        AddLicence(licence, "License", licenceName);
        AddLicence(licence, "Other permissions", (string)meta?["otherPermissionUrl"]);
        AddLicence(licence, "Other license", (string)meta?["otherLicenseUrl"]);
        LicenseText = licence.Count > 0 ? string.Join("\n", licence) : null;
        Credits = (string)meta?["reference"];
        if (vrm["humanoid"]?["humanBones"] is JArray bones)
        {
            foreach (JToken bone in bones)
            {
                int node = Int(bone["node"], -1);
                if (node >= 0 && TryMapBone((string)bone["bone"], false, out HumanBodyBones human)) Bones[human] = node;
            }
        }
        if (vrm["blendShapeMaster"]?["blendShapeGroups"] is JArray groups)
        {
            foreach (JToken group in groups)
            {
                string name = Vrm0Preset((string)group["presetName"]) ?? (string)group["name"];
                if (string.IsNullOrEmpty(name) || !(group["binds"] is JArray binds)) continue;
                List<Bind> list = new List<Bind>();
                foreach (JToken bind in binds) list.Add(new Bind { Node = -1, Mesh = Int(bind["mesh"], -1), Index = Int(bind["index"], -1), Weight = Float(bind["weight"], 100f) / 100f });
                if (!Expressions.ContainsKey(name)) Expressions[name] = list;
            }
        }
        JToken firstPerson = vrm["firstPerson"];
        if (firstPerson?["firstPersonBoneOffset"] is JObject eyeOffset)
        {
            EyeOffset = Vec(eyeOffset);
            HasEyeOffset = EyeOffset != Vector3.zero;
        }
        if (firstPerson?["meshAnnotations"] is JArray annotations)
        {
            foreach (JToken annotation in annotations)
            {
                string flag = (string)annotation["firstPersonFlag"];
                FirstPerson.Add(new Annotation { Node = -1, Mesh = Int(annotation["mesh"], -1), ThirdPersonOnly = flag == "ThirdPersonOnly", FirstPersonOnly = flag == "FirstPersonOnly" });
            }
        }
        if (vrm["secondaryAnimation"]?["colliderGroups"] is JArray colliderGroups)
        {
            foreach (JToken group in colliderGroups)
            {
                int node = Int(group["node"], -1);
                List<int> members = new List<int>();
                if (group["colliders"] is JArray spheres)
                {
                    foreach (JToken sphere in spheres)
                    {
                        members.Add(Colliders.Count);
                        Colliders.Add(new Collider { Node = node, Capsule = false, Offset = Vec(sphere["offset"]), Radius = Float(sphere["radius"], 0f) });
                    }
                }
                ColliderGroups.Add(members.ToArray());
            }
        }
        if (vrm["secondaryAnimation"]?["boneGroups"] is JArray boneGroups)
        {
            foreach (JToken boneGroup in boneGroups)
            {
                if (!(boneGroup["bones"] is JArray roots) || roots.Count == 0) continue;
                Springs.Add(new Spring { Roots = Ints(roots), ColliderGroups = Ints(boneGroup["colliderGroups"]), Stiffness = Float(boneGroup["stiffiness"], 1f), Drag = Float(boneGroup["dragForce"], 0.4f), Gravity = Float(boneGroup["gravityPower"], 0f), HitRadius = Float(boneGroup["hitRadius"], 0.02f) });
            }
        }
        if (vrm["materialProperties"] is JArray properties)
        {
            for (int Index = 0; Index < properties.Count; Index++)
            {
                JToken property = properties[Index];
                if ((string)property["shader"] == "VRM/MToon") Materials[Index] = ReadMToon0(property);
            }
        }
    }
    private static MToon ReadMToon0(JToken property)
    {
        JToken floats = property["floatProperties"], vectors = property["vectorProperties"], textures = property["textureProperties"];
        float F(string name, float fallback) => Float(floats?[name], fallback);
        int T(string name) => Int(textures?[name], -1);
        Color C(string name, Color fallback) => vectors?[name] is JArray v && v.Count >= 3 ? new Color(Float(v[0], 0f), Float(v[1], 0f), Float(v[2], 0f), v.Count > 3 ? Float(v[3], 1f) : 1f) : fallback;
        MToon m = new MToon();
        int blend = (int)F("_BlendMode", 0f);
        m.AlphaMode = blend == 1 ? "MASK" : blend >= 2 ? "BLEND" : "OPAQUE";
        m.TransparentWithZWrite = blend == 3;
        int queue = Int(property["renderQueue"], -1);
        if (queue > 0) m.QueueOffset = m.TransparentWithZWrite ? queue - 2501 : queue - 3000;
        m.Cutoff = F("_Cutoff", 0.5f);
        m.DoubleSided = (int)F("_CullMode", 2f) == 0;
        m.BaseColor = C("_Color", Color.white);
        m.BaseTex = T("_MainTex");
        if (vectors?["_MainTex"] is JArray st && st.Count == 4) m.MainST = new Vector4(Float(st[2], 1f), Float(st[3], 1f), Float(st[0], 0f), Float(st[1], 0f));
        m.ShadeColor = C("_ShadeColor", Color.white);
        m.ShadeTex = T("_ShadeTexture");
        m.NormalTex = T("_BumpMap");
        m.NormalScale = F("_BumpScale", 1f);
        float shift = F("_ShadeShift", 0f), toony = F("_ShadeToony", 0.9f);
        m.ShadingToony = Mathf.Clamp01((1f + toony) * 0.5f);
        m.ShadingShift = Mathf.Clamp(-(shift + (1f - toony) * 0.5f), -1f, 1f);
        m.GiEqualization = Mathf.Clamp01(1f - F("_IndirectLightIntensity", 0.1f));
        m.Emission = C("_EmissionColor", Color.black);
        m.EmissionTex = T("_EmissionMap");
        m.MatcapTex = T("_SphereAdd");
        m.Matcap = m.MatcapTex >= 0 ? Color.white : Color.black;
        m.Rim = C("_RimColor", Color.black);
        m.RimPower = F("_RimFresnelPower", 1f);
        m.RimLift = F("_RimLift", 0f);
        m.RimTex = T("_RimTexture");
        m.RimMix = F("_RimLightingMix", 0f);
        m.OutlineMode = Mathf.Clamp((int)F("_OutlineWidthMode", 0f), 0, 2);
        m.OutlineWidth = F("_OutlineWidth", 0f) * 0.01f;
        m.OutlineWidthTex = T("_OutlineWidthTexture");
        m.OutlineColor = C("_OutlineColor", Color.black);
        m.OutlineMix = (int)F("_OutlineColorMode", 0f) == 0 ? 0f : F("_OutlineLightingMix", 1f);
        m.UvMaskTex = T("_UvAnimMaskTexture");
        m.ScrollX = F("_UvAnimScrollX", 0f);
        m.ScrollY = F("_UvAnimScrollY", 0f);
        m.Rotation = F("_UvAnimRotation", 0f) * Mathf.PI * 2f;
        return m;
    }
    private static void AddLicence(List<string> lines, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add(label + ": " + value.Trim());
    }
    private static string Flag(JToken token) => token is JValue value && value.Type == JTokenType.Boolean ? ((bool)value ? "allowed" : "not allowed") : null;
    private static string Vrm0Preset(string preset)
    {
        switch (preset?.ToLowerInvariant())
        {
            case "a": return "aa";
            case "i": return "ih";
            case "u": return "ou";
            case "e": return "ee";
            case "o": return "oh";
            case "blink": return "blink";
            case "blink_l": return "blinkLeft";
            case "blink_r": return "blinkRight";
            case "joy": return "happy";
            case "angry": return "angry";
            case "sorrow": return "sad";
            case "fun": return "relaxed";
            default: return null;
        }
    }
    public static bool TryMapBone(string vrmName, bool vrm1, out HumanBodyBones bone)
    {
        bone = HumanBodyBones.LastBone;
        if (string.IsNullOrEmpty(vrmName)) return false;
        if (vrm1)
        {
            if (vrmName.EndsWith("ThumbMetacarpal", StringComparison.Ordinal)) vrmName = vrmName.Replace("ThumbMetacarpal", "ThumbProximal");
            else if (vrmName.EndsWith("ThumbProximal", StringComparison.Ordinal)) vrmName = vrmName.Replace("ThumbProximal", "ThumbIntermediate");
        }
        string pascal = char.ToUpperInvariant(vrmName[0]) + vrmName.Substring(1);
        return Enum.TryParse(pascal, false, out bone) && bone != HumanBodyBones.LastBone;
    }
    private static int TexIndex(JToken info) => Int(info?["index"], -1);
    private static Color LinearColor(JToken token, Color fallback, float multiplier = 1f)
    {
        if (!(token is JArray array) || array.Count < 3) return fallback;
        Color linear = new Color(Float(array[0], 0f) * multiplier, Float(array[1], 0f) * multiplier, Float(array[2], 0f) * multiplier, array.Count > 3 ? Float(array[3], 1f) : 1f);
        return linear.gamma;
    }
    private static Vector3 Vec(JToken token, Vector3 fallback = default)
    {
        if (token is JArray array && array.Count >= 2) return new Vector3(Float(array[0], fallback.x), Float(array[1], fallback.y), array.Count > 2 ? Float(array[2], fallback.z) : fallback.z);
        if (token is JObject obj) return new Vector3(Float(obj["x"], fallback.x), Float(obj["y"], fallback.y), Float(obj["z"], fallback.z));
        return fallback;
    }
    private static int[] Ints(JToken token)
    {
        if (!(token is JArray array)) return Array.Empty<int>();
        int[] values = new int[array.Count];
        for (int Index = 0; Index < values.Length; Index++) values[Index] = Int(array[Index], -1);
        return values;
    }
    private static int Int(JToken token, int fallback) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (int)token : fallback;
    private static long Long(JToken token, long fallback) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (long)token : fallback;
    private static float Float(JToken token, float fallback)
    {
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return fallback;
        float value = (float)token;
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }
    private static readonly ConcurrentDictionary<string, bool> sniffedLocations = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    public static bool IsVrmLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        string extension = ExtensionOf(location);
        if (string.Equals(extension, ".vrm", StringComparison.OrdinalIgnoreCase)) return true;
        return sniffedLocations.TryGetValue(location, out bool vrm) && vrm;
    }
    public static async Task<bool> IsVrmLocationAsync(string location, CancellationToken cancellationToken)
    {
        if (IsVrmLocation(location)) return true;
        if (string.IsNullOrWhiteSpace(location) || sniffedLocations.ContainsKey(location)) return false;
        if (string.Equals(ExtensionOf(location), BasisBeeConstants.BasisEncryptedExtension, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri uri)) return false;
        byte[] header = null;
        if (uri.IsFile)
        {
            if (!string.IsNullOrEmpty(uri.Host)) return false;
            try
            {
                if (!File.Exists(uri.LocalPath)) return false;
                using FileStream stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                header = new byte[20];
                if (stream.Read(header, 0, header.Length) != header.Length) header = null;
            }
            catch (Exception)
            {
                return false;
            }
        }
        else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            (header, _) = await BasisIOManagement.DownloadRangeAsync(location, 0, 20, cancellationToken);
            if (header == null) return false;
        }
        else return false;
        bool isVrm = LooksLikeGlb(header);
        sniffedLocations[location] = isVrm;
        return isVrm;
    }
    private static string ExtensionOf(string location)
    {
        string path = location;
        if (Uri.TryCreate(location, UriKind.Absolute, out Uri uri)) path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        try
        {
            return Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
