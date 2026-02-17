using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;

public class ShaderVariantFinderWindow : EditorWindow
{
    private bool includeInactive = true;
    private bool showKeywords = true;

    private string search = "";
    private Vector2 scroll;

    // Foldouts (persist during window lifetime)
    private readonly Dictionary<string, bool> shaderFoldout = new();
    private readonly Dictionary<string, bool> variantFoldout = new();
    private readonly Dictionary<int, bool> materialFoldout = new();

    // Data
    private readonly List<ShaderGroup> shaderGroups = new();

    private class ShaderGroup
    {
        public string shaderName;
        public List<VariantGroup> variants = new();
    }

    private class VariantGroup
    {
        public string shaderName;
        public string keywords;      // display string (sorted)
        public string key;           // unique key
        public HashSet<Material> materials = new();
        public HashSet<Renderer> renderers = new(); // renderers referencing those materials
    }

    // Material -> renderers that use it
    private readonly Dictionary<Material, HashSet<Renderer>> materialToRenderers = new();

    [MenuItem("Tools/Rendering/Shader Variant Browser")]
    public static void Open()
    {
        GetWindow<ShaderVariantFinderWindow>("Shader Variant Browser");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(4);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Scan settings", EditorStyles.boldLabel);
            includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive);
            showKeywords = EditorGUILayout.ToggleLeft("Show keywords (variants)", showKeywords);

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan Open Scenes", GUILayout.Height(24)))
                    Scan();

                if (GUILayout.Button("Collapse All", GUILayout.Height(24), GUILayout.Width(110)))
                    CollapseAll();

                if (GUILayout.Button("Clear", GUILayout.Height(24), GUILayout.Width(70)))
                    ClearAll();
            }
        }

        EditorGUILayout.Space(6);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Search", GUILayout.Width(50));
            search = EditorGUILayout.TextField(search);
        }

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField($"Shaders: {shaderGroups.Count}  |  Variants: {shaderGroups.Sum(g => g.variants.Count)}");

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawBrowser();
        EditorGUILayout.EndScrollView();
    }

    private void DrawBrowser()
    {
        foreach (var sg in shaderGroups)
        {
            if (!ShaderMatchesSearch(sg)) continue;

            bool open = GetFoldout(shaderFoldout, sg.shaderName);
            open = EditorGUILayout.Foldout(open, $"{sg.shaderName}  ({sg.variants.Count})", true);
            shaderFoldout[sg.shaderName] = open;

            if (!open) continue;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var vg in sg.variants)
                {
                    if (!VariantMatchesSearch(vg)) continue;

                    string variantLabel = showKeywords
                        ? (string.IsNullOrEmpty(vg.keywords) ? "<no keywords>" : vg.keywords)
                        : "<shader>";

                    string header = $"{variantLabel}   | Mats: {vg.materials.Count}  Renderers: {vg.renderers.Count}";

                    bool vOpen = GetFoldout(variantFoldout, vg.key);
                    vOpen = EditorGUILayout.Foldout(vOpen, header, true);
                    variantFoldout[vg.key] = vOpen;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18);

                        if (GUILayout.Button("Select Materials", GUILayout.Width(120)))
                            SelectObjects(vg.materials.Cast<Object>().ToArray());

                        if (GUILayout.Button("Select Renderers", GUILayout.Width(120)))
                            SelectObjects(vg.renderers.Cast<Object>().ToArray());

                        if (GUILayout.Button("Ping Shader", GUILayout.Width(95)))
                        {
                            // Try to find shader asset (works if it's an asset, not generated)
                            var shader = Shader.Find(vg.shaderName);
                            if (shader != null)
                                EditorGUIUtility.PingObject(shader);
                        }
                    }

                    if (!vOpen) continue;

                    // Materials list
                    foreach (var mat in vg.materials.OrderBy(m => m.name))
                    {
                        if (mat == null) continue;

                        int id = mat.GetInstanceID();
                        bool mOpen = GetFoldout(materialFoldout, id);
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                mOpen = EditorGUILayout.Foldout(mOpen, $"{mat.name}", true);
                                materialFoldout[id] = mOpen;

                                GUILayout.FlexibleSpace();

                                if (GUILayout.Button("Select", GUILayout.Width(60)))
                                    SelectObjects(new Object[] { mat });

                                if (GUILayout.Button("Ping", GUILayout.Width(50)))
                                    EditorGUIUtility.PingObject(mat);
                            }

                            // Show the material field so user can click/inspect it
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(18);
                                EditorGUILayout.ObjectField("Material", mat, typeof(Material), false);
                            }

                            if (!mOpen) continue;

                            // Renderers using this material
                            if (materialToRenderers.TryGetValue(mat, out var rs) && rs.Count > 0)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(18);
                                    EditorGUILayout.LabelField($"Used by {rs.Count} renderer(s):", EditorStyles.miniBoldLabel);
                                }

                                foreach (var r in rs.OrderBy(x => x.name))
                                {
                                    if (r == null) continue;
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(28);

                                        // Renderer field (pingable/selectable)
                                        EditorGUILayout.ObjectField(r, typeof(Renderer), true);

                                        if (GUILayout.Button("Select", GUILayout.Width(60)))
                                            SelectObjects(new Object[] { r.gameObject });

                                        if (GUILayout.Button("Ping", GUILayout.Width(50)))
                                            EditorGUIUtility.PingObject(r.gameObject);
                                    }
                                }
                            }
                            else
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(18);
                                    EditorGUILayout.LabelField("No renderers found (material might be unused in current open scenes).", EditorStyles.miniLabel);
                                }
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(4);
        }
    }

    private void Scan()
    {
        shaderGroups.Clear();
        materialToRenderers.Clear();
        // keep foldouts; they’ll naturally update with new keys

        // Collect renderers across all loaded scenes
        var allRenderers = new List<Renderer>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (var root in scene.GetRootGameObjects())
            {
                var rs = root.GetComponentsInChildren<Renderer>(includeInactive);
                if (rs != null && rs.Length > 0)
                    allRenderers.AddRange(rs);
            }
        }

        // Accumulate variants
        var variantMap = new Dictionary<string, VariantGroup>();
        // group by shader
        var shaderMap = new Dictionary<string, ShaderGroup>();

        foreach (var r in allRenderers)
        {
            if (r == null) continue;
            var mats = r.sharedMaterials;
            if (mats == null) continue;

            foreach (var mat in mats)
            {
                if (mat == null) continue;
                var shader = mat.shader;
                if (shader == null) continue;

                string shaderName = shader.name;

                // Compute keyword signature
                string keywordsText = "";
                if (showKeywords)
                {
                    var kws = mat.shaderKeywords;
                    if (kws != null && kws.Length > 0)
                    {
                        System.Array.Sort(kws);
                        keywordsText = string.Join(", ", kws);
                    }
                }

                // Variant key
                string key = showKeywords ? (shaderName + "|" + keywordsText) : shaderName;

                if (!variantMap.TryGetValue(key, out var vg))
                {
                    vg = new VariantGroup
                    {
                        shaderName = shaderName,
                        keywords = keywordsText,
                        key = key,
                    };
                    variantMap[key] = vg;
                }

                vg.materials.Add(mat);
                vg.renderers.Add(r);

                // Material -> renderers map
                if (!materialToRenderers.TryGetValue(mat, out var rset))
                {
                    rset = new HashSet<Renderer>();
                    materialToRenderers[mat] = rset;
                }
                rset.Add(r);

                // Shader group
                if (!shaderMap.TryGetValue(shaderName, out var sg))
                {
                    sg = new ShaderGroup { shaderName = shaderName };
                    shaderMap[shaderName] = sg;
                }
            }
        }

        // Build final grouped list
        foreach (var sg in shaderMap.Values)
        {
            sg.variants = variantMap.Values
                .Where(v => v.shaderName == sg.shaderName)
                .OrderBy(v => v.keywords)
                .ToList();

            shaderGroups.Add(sg);
        }

        shaderGroups.Sort((a, b) => string.Compare(a.shaderName, b.shaderName, System.StringComparison.OrdinalIgnoreCase));

        Repaint();
        Debug.Log($"Shader Variant Browser: scanned {allRenderers.Count} renderers. " +
                  $"Shaders: {shaderGroups.Count}, Variants: {shaderGroups.Sum(g => g.variants.Count)}.");
    }

    private void SelectObjects(Object[] objects)
    {
        if (objects == null || objects.Length == 0) return;
        Selection.objects = objects.Where(o => o != null).ToArray();
        if (Selection.activeObject != null)
            EditorGUIUtility.PingObject(Selection.activeObject);
    }

    private void CollapseAll()
    {
        var keys1 = shaderFoldout.Keys.ToList();
        foreach (var k in keys1) shaderFoldout[k] = false;

        var keys2 = variantFoldout.Keys.ToList();
        foreach (var k in keys2) variantFoldout[k] = false;

        var keys3 = materialFoldout.Keys.ToList();
        foreach (var k in keys3) materialFoldout[k] = false;

        Repaint();
    }

    private void ClearAll()
    {
        shaderGroups.Clear();
        materialToRenderers.Clear();
        shaderFoldout.Clear();
        variantFoldout.Clear();
        materialFoldout.Clear();
        Repaint();
    }

    private bool ShaderMatchesSearch(ShaderGroup sg)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        if (sg.shaderName.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // also match any variant keywords/material names
        return sg.variants.Any(VariantMatchesSearch);
    }

    private bool VariantMatchesSearch(VariantGroup vg)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        if (vg.shaderName.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (!string.IsNullOrEmpty(vg.keywords) &&
            vg.keywords.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // match material names too
        return vg.materials.Any(m => m != null && m.name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private bool GetFoldout(Dictionary<string, bool> map, string key)
    {
        if (map.TryGetValue(key, out var v)) return v;
        map[key] = false;
        return false;
    }

    private bool GetFoldout(Dictionary<int, bool> map, int key)
    {
        if (map.TryGetValue(key, out var v)) return v;
        map[key] = false;
        return false;
    }
}
