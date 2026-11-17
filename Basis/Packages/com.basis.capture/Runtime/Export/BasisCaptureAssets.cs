using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis.Capture
{
    public sealed class BasisCaptureAssets
    {
        private sealed class MeshEntry
        {
            public int id, subStart, subCount;
            public Mesh mesh;
            public string name, file, warning;
            public bool worldSpace, written, failed;
            public BasisCaptureMeshReader reader;
            public Task writeTask;
            public int vertexCount, shapeCount;
        }
        private sealed class TextureEntry
        {
            public int id;
            public Texture texture;
            public bool normalMap;
            public string file, warning;
            public BasisCaptureTextureReader reader;
        }
        private readonly string folder;
        private readonly bool exportGeometry;
        private readonly Dictionary<(Mesh, int, int), MeshEntry> meshLookup = new Dictionary<(Mesh, int, int), MeshEntry>();
        private readonly List<MeshEntry> meshes = new List<MeshEntry>();
        private readonly Dictionary<Material, int> materialLookup = new Dictionary<Material, int>();
        private readonly List<Material> materials = new List<Material>();
        private readonly List<string> materialJson = new List<string>();
        private readonly Dictionary<(Texture, bool), TextureEntry> textureLookup = new Dictionary<(Texture, bool), TextureEntry>();
        private readonly List<TextureEntry> textures = new List<TextureEntry>();
        private int meshCursor, textureCursor, meshesInFlight, texturesInFlight;
        public int MaxMeshesInFlight = 4, MaxTexturesInFlight = 4;
        public BasisCaptureAssets(string folder, bool exportGeometry)
        {
            this.folder = folder;
            this.exportGeometry = exportGeometry;
            Directory.CreateDirectory(Path.Combine(folder, BasisCaptureFormat.AssetsFolder));
        }
        public bool Busy
        {
            get
            {
                if (!exportGeometry) return false;
                if (meshCursor < meshes.Count || textureCursor < textures.Count || meshesInFlight > 0 || texturesInFlight > 0) return true;
                foreach (MeshEntry m in meshes) if (m.writeTask != null && !m.writeTask.IsCompleted) return true;
                return false;
            }
        }
        public float Progress
        {
            get
            {
                int total = meshes.Count + textures.Count;
                if (total == 0) return 1f;
                int complete = 0;
                foreach (MeshEntry m in meshes) if (m.written || m.failed) complete++;
                foreach (TextureEntry t in textures) if (t.reader != null && t.reader.Done) complete++;
                return complete / (float)total;
            }
        }
        public int MeshCount => meshes.Count;
        public int TextureCount => textures.Count;
        public int MeshFor(Renderer r)
        {
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
            if (mesh == null) return -1;
            int subStart = 0, subCount = mesh.subMeshCount;
            bool world = false;
            if (r.isPartOfStaticBatch && r is MeshRenderer mr)
            {
                subStart = mr.subMeshStartIndex;
                subCount = r.sharedMaterials.Length;
                world = true;
            }
            (Mesh, int, int) key = (mesh, subStart, subCount);
            if (meshLookup.TryGetValue(key, out MeshEntry e)) return e.id;
            e = new MeshEntry { id = meshes.Count, mesh = mesh, name = mesh.name, subStart = subStart, subCount = subCount, worldSpace = world };
            e.file = $"{BasisCaptureFormat.AssetsFolder}/mesh_{e.id}.bin";
            meshLookup[key] = e;
            meshes.Add(e);
            return e.id;
        }
        public int MaterialFor(Material m)
        {
            if (m == null) return -1;
            if (materialLookup.TryGetValue(m, out int id)) return id;
            id = materials.Count;
            materialLookup[m] = id;
            materials.Add(m);
            materialJson.Add(BuildMaterial(id, m));
            return id;
        }
        public int TextureFor(Texture t, bool normalMap)
        {
            if (!BasisCaptureTextureReader.Supported(t)) return -1;
            if (textureLookup.TryGetValue((t, normalMap), out TextureEntry e)) return e.id;
            e = new TextureEntry { id = textures.Count, texture = t, normalMap = normalMap };
            string stem = Path.Combine(folder, BasisCaptureFormat.AssetsFolder, $"tex_{e.id}");
            e.reader = exportGeometry ? new BasisCaptureTextureReader(t, normalMap, stem) : null;
            e.file = $"{BasisCaptureFormat.AssetsFolder}/tex_{e.id}{(BasisCaptureTextureReader.IsHdr(t) ? ".exr" : ".png")}";
            textureLookup[(t, normalMap)] = e;
            textures.Add(e);
            return e.id;
        }
        public void Process()
        {
            if (!exportGeometry) return;
            for (int i = 0; i < meshes.Count; i++)
            {
                MeshEntry m = meshes[i];
                if (m.reader == null || m.writeTask != null || !m.reader.Done) continue;
                meshesInFlight--;
                BasisCaptureMeshData data = m.reader.data;
                m.warning = data.warning;
                if (data.positions == null || data.positions.Length != data.vertexCount) { m.failed = true; m.warning ??= "no vertex positions"; m.reader = null; continue; }
                string path = Path.Combine(folder, m.file);
                m.writeTask = Task.Run(() =>
                {
                    try
                    {
                        data.worldSpace = m.worldSpace;
                        data.Compact(m.subStart, m.subCount);
                        m.vertexCount = data.vertexCount;
                        m.shapeCount = data.shapes.Count;
                        data.Write(path);
                        m.written = true;
                    }
                    catch (Exception e) { m.failed = true; m.warning = e.Message; }
                });
                m.reader = null;
            }
            while (meshCursor < meshes.Count && meshesInFlight < MaxMeshesInFlight)
            {
                MeshEntry m = meshes[meshCursor++];
                if (m.mesh == null) { m.failed = true; m.warning = "mesh destroyed before export"; continue; }
                m.reader = new BasisCaptureMeshReader(m.mesh);
                meshesInFlight++;
                m.reader.Begin();
            }
            texturesInFlight = 0;
            for (int i = 0; i < textureCursor; i++) if (textures[i].reader != null && !textures[i].reader.Done) texturesInFlight++;
            while (textureCursor < textures.Count && texturesInFlight < MaxTexturesInFlight)
            {
                TextureEntry t = textures[textureCursor++];
                if (t.texture == null) { t.warning = "texture destroyed before export"; continue; }
                t.reader.Begin();
                texturesInFlight++;
            }
        }
        public void WriteManifest(BasisCaptureJson j)
        {
            j.BeginArray("meshes");
            foreach (MeshEntry m in meshes) j.BeginObject().Prop("id", m.id).Prop("name", m.name).Prop("file", m.written ? m.file : null).Prop("worldSpace", m.worldSpace).Prop("subStart", m.subStart).Prop("subCount", m.subCount).Prop("vertices", m.vertexCount).Prop("shapes", m.shapeCount).Prop("warning", m.warning).EndObject();
            j.EndArray();
            j.BeginArray("textures");
            foreach (TextureEntry t in textures)
            {
                bool ok = t.reader != null && t.reader.Done && t.reader.warning == null && File.Exists(Path.Combine(folder, t.file));
                j.BeginObject().Prop("id", t.id).Prop("name", t.texture != null ? t.texture.name : "").Prop("file", ok ? t.file : null).Prop("normalMap", t.normalMap).Prop("srgb", t.texture != null && t.texture.isDataSRGB && !t.normalMap).Prop("format", t.texture is Texture2D t2 ? t2.format.ToString() : t.texture != null ? t.texture.graphicsFormat.ToString() : null).Prop("width", t.texture != null ? t.texture.width : 0).Prop("height", t.texture != null ? t.texture.height : 0).Prop("warning", t.reader?.warning ?? t.warning).EndObject();
            }
            j.EndArray();
        }
        public void WriteMaterials()
        {
            using StreamWriter w = new StreamWriter(Path.Combine(folder, BasisCaptureFormat.MaterialsFile), false, new System.Text.UTF8Encoding(false));
            w.Write("{\"materials\":[");
            for (int i = 0; i < materialJson.Count; i++) { if (i > 0) w.Write(','); w.Write(materialJson[i]); }
            w.Write("]}");
        }
        private static readonly string[] BaseColorNames = { "_BaseColor", "_Color", "_MainColor" }, BaseMapNames = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo" }, NormalNames = { "_BumpMap", "_NormalMap", "_NormalTex", "_BumpTex" };
        private static readonly string[] MetallicMapNames = { "_MetallicGlossMap", "_MetallicMap", "_MetallicTex" }, SmoothnessNames = { "_Smoothness", "_Glossiness", "_Glossy" };
        private string BuildMaterial(int id, Material m)
        {
            BasisCaptureJson j = new BasisCaptureJson();
            Shader shader = m.shader;
            j.BeginObject().Prop("id", id).Prop("name", m.name).Prop("shader", shader != null ? shader.name : "").Prop("renderQueue", m.renderQueue).Name("keywords").Strings(m.shaderKeywords);
            j.BeginObject("pbr");
            string baseMapProp = FirstTexture(m, BaseMapNames);
            j.Prop("baseColor", FirstColor(m, BaseColorNames, Color.white));
            j.Prop("baseMap", baseMapProp != null ? TextureFor(m.GetTexture(baseMapProp), false) : -1);
            if (baseMapProp != null) { j.Prop("baseMapScale", m.GetTextureScale(baseMapProp)); j.Prop("baseMapOffset", m.GetTextureOffset(baseMapProp)); }
            string normalProp = FirstTexture(m, NormalNames);
            j.Prop("normalMap", normalProp != null ? TextureFor(m.GetTexture(normalProp), true) : -1);
            j.Prop("normalScale", FirstFloat(m, new[] { "_BumpScale", "_NormalScale", "_BumpMapScale" }, 1f));
            j.Prop("metallic", FirstFloat(m, new[] { "_Metallic" }, 0f));
            j.Prop("smoothness", FirstFloat(m, SmoothnessNames, 0.5f));
            string metalProp = FirstTexture(m, MetallicMapNames);
            j.Prop("metallicMap", metalProp != null ? TextureFor(m.GetTexture(metalProp), false) : -1);
            string occlusionProp = FirstTexture(m, new[] { "_OcclusionMap" });
            j.Prop("occlusionMap", occlusionProp != null ? TextureFor(m.GetTexture(occlusionProp), false) : -1);
            string emissionMapProp = FirstTexture(m, new[] { "_EmissionMap", "_EmissiveMap", "_EmissionTex" });
            Color emission = FirstColor(m, new[] { "_EmissionColor", "_EmissiveColor" }, Color.black);
            bool emissive = m.IsKeywordEnabled("_EMISSION") || FirstFloat(m, new[] { "_EnableEmission", "_UseEmission", "_EmissionEnabled", "_Toggle_EMISSION" }, 0f) > 0.5f || (emission.maxColorComponent > 0f && shader != null && !shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal));
            j.Prop("emissive", emissive);
            j.Prop("emissionColor", emission);
            j.Prop("emissionStrength", FirstFloat(m, new[] { "_EmissionStrength", "_EmissionIntensity" }, 1f));
            j.Prop("emissionMap", emissionMapProp != null ? TextureFor(m.GetTexture(emissionMapProp), false) : -1);
            bool clip = m.IsKeywordEnabled("_ALPHATEST_ON") || FirstFloat(m, new[] { "_AlphaClip", "_AlphaTest" }, 0f) > 0.5f || (m.renderQueue >= 2450 && m.renderQueue < 3000);
            bool blend = m.renderQueue >= 3000 || FirstFloat(m, new[] { "_Surface" }, 0f) > 0.5f;
            j.Prop("alphaMode", blend ? "BLEND" : clip ? "CLIP" : "OPAQUE");
            j.Prop("alphaCutoff", FirstFloat(m, new[] { "_Cutoff", "_AlphaCutoff", "_Clip" }, 0.5f));
            j.Prop("cull", (int)FirstFloat(m, new[] { "_Cull", "_CullMode", "_Culling" }, 2f));
            j.EndObject();
            j.BeginArray("properties");
            int count = shader != null ? shader.GetPropertyCount() : 0;
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                ShaderPropertyType type = shader.GetPropertyType(i);
                j.BeginObject().Prop("name", name).Prop("type", type.ToString());
                try
                {
                    switch (type)
                    {
                        case ShaderPropertyType.Color: j.Prop("value", m.GetColor(name)); break;
                        case ShaderPropertyType.Vector: j.Prop("value", m.GetVector(name)); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: j.Prop("value", m.GetFloat(name)); break;
                        case ShaderPropertyType.Int: j.Prop("value", m.GetInteger(name)); break;
                        case ShaderPropertyType.Texture:
                            Texture t = m.GetTexture(name);
                            bool isNormal = Array.IndexOf(NormalNames, name) >= 0 || name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0;
                            j.Prop("value", t != null ? TextureFor(t, isNormal) : -1);
                            j.Prop("textureName", t != null ? t.name : null);
                            j.Prop("scale", m.GetTextureScale(name));
                            j.Prop("offset", m.GetTextureOffset(name));
                            break;
                    }
                }
                catch (Exception e) { j.Prop("error", e.Message); }
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();
            return j.ToString();
        }
        private static string FirstTexture(Material m, string[] names)
        {
            foreach (string n in names) if (m.HasTexture(n) && m.GetTexture(n) != null) return n;
            return null;
        }
        private static Color FirstColor(Material m, string[] names, Color fallback)
        {
            foreach (string n in names) if (m.HasColor(n)) return m.GetColor(n);
            return fallback;
        }
        private static float FirstFloat(Material m, string[] names, float fallback)
        {
            foreach (string n in names) if (m.HasFloat(n)) return m.GetFloat(n); else if (m.HasInteger(n)) return m.GetInteger(n);
            return fallback;
        }
    }
}
