// Editor/BasisLoadableConfigurationWindow.cs
// Unity Editor window to create/read BasisLoadableConfiguration XML with comments preserved.
using UnityEditor;
using UnityEngine;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

public class BasisLoadableConfigurationWindow : EditorWindow
{
    // Fields
    int mode = 0;
    string loadedNetID = "";
    string unlockPassword = "";
    string combinedURL = "";
    bool isLocalLoad = false;

    Vector3 Selectedposition = new Vector3(0, 0f, 0);
    Quaternion rotation = new Quaternion(0f, 0f, 0f, 0);
    Vector3 scale = Vector3.one;

    bool persist = true;

    // UI
    [MenuItem("Basis/Loadable Config Editor")]
    public static void ShowWindow()
    {
        var win = GetWindow<BasisLoadableConfigurationWindow>("Basis Config");
        win.minSize = new Vector2(420, 520);
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Basis Loadable Configuration", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        using (new EditorGUILayout.VerticalScope("box"))
        {
            mode = EditorGUILayout.IntField(new GUIContent("Mode", "Mode of the configuration"), mode);
            loadedNetID = EditorGUILayout.TextField(new GUIContent("LoadedNetID", "Network ID"), loadedNetID);
            unlockPassword = EditorGUILayout.TextField(new GUIContent("UnlockPassword", "Unlock password (hash)"), unlockPassword);
            combinedURL = EditorGUILayout.TextField(new GUIContent("CombinedURL", "Combined URL"), combinedURL);
            isLocalLoad = EditorGUILayout.Toggle(new GUIContent("IsLocalLoad", "Local load flag"), isLocalLoad);
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Transform", EditorStyles.boldLabel);
            Selectedposition = EditorGUILayout.Vector3Field("Position (X,Y,Z)", Selectedposition);

            // Quaternion fields (explicit)
            var qx = rotation.x; var qy = rotation.y; var qz = rotation.z; var qw = rotation.w;
            qx = EditorGUILayout.FloatField("QuaternionX", qx);
            qy = EditorGUILayout.FloatField("QuaternionY", qy);
            qz = EditorGUILayout.FloatField("QuaternionZ", qz);
            qw = EditorGUILayout.FloatField("QuaternionW", qw);
            rotation = new Quaternion(qx, qy, qz, qw);

            scale = EditorGUILayout.Vector3Field("Scale (X,Y,Z)", scale);
        }

        EditorGUILayout.Space();
        persist = EditorGUILayout.Toggle(new GUIContent("Persist", "Persist flag"), persist);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Load XML..."))
            {
                LoadXML();
            }
            if (GUILayout.Button("Save XML..."))
            {
                SaveXML();
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Tip: This window writes the XML with the same comments and element order as your example.", MessageType.Info);
    }

    void SaveXML()
    {
        var path = EditorUtility.SaveFilePanel("Save BasisLoadableConfiguration XML", "", "BasisLoadableConfiguration.xml", "xml");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // Use invariant culture to match decimal formatting like 2.801 (dot)
            var inv = CultureInfo.InvariantCulture;

            // Build XML with comments exactly like sample
            var doc =
                new XDocument(
                    new XDeclaration("1.0", "utf-8", "yes"),
                    new XElement("BasisLoadableConfiguration",
                        new XComment(" Mode of the configuration "),
                        new XElement("Mode", mode),

                        new XComment(" Network ID "),
                        new XElement("LoadedNetID", loadedNetID ?? string.Empty),

                        new XComment(" Unlock password "),
                        new XElement("UnlockPassword", unlockPassword ?? string.Empty),

                        new XComment(" Combined URl "),
                        new XElement("CombinedURL", combinedURL ?? string.Empty),

                        new XComment(" Local load flag "),
                        new XElement("IsLocalLoad", isLocalLoad.ToString().ToLowerInvariant()),

                        new XText("\n\n    "),
                        new XComment(" Position values "),
                        new XElement("PositionX", Selectedposition.x.ToString(inv)),
                        new XElement("PositionY", Selectedposition.y.ToString(inv)),
                        new XElement("PositionZ", Selectedposition.z.ToString(inv)),

                        new XText("\n\n    "),
                        new XComment(" Quaternion values "),
                        new XElement("QuaternionX", rotation.x.ToString(inv)),
                        new XElement("QuaternionY", rotation.y.ToString(inv)),
                        new XElement("QuaternionZ", rotation.z.ToString(inv)),
                        new XElement("QuaternionW", rotation.w.ToString(inv)),

                        new XText("\n\n    "),
                        new XComment(" Scale values "),
                        new XElement("ScaleX", scale.x.ToString(inv)),
                        new XElement("ScaleY", scale.y.ToString(inv)),
                        new XElement("ScaleZ", scale.z.ToString(inv)),

                        new XText("\n\n    "),
                        new XComment(" Persist flag "),
                        new XElement("Persist", persist.ToString().ToLowerInvariant())
                    )
                );

            // Pretty print with indentation
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                doc.Save(fs);
            }

            // Unity likes to refresh
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Saved", $"Wrote XML:\n{path}", "OK");
        }
        catch (System.Exception ex)
        {
            Debug.LogError(ex);
            EditorUtility.DisplayDialog("Error", "Failed to save XML:\n" + ex.Message, "OK");
        }
    }

    void LoadXML()
    {
        var path = EditorUtility.OpenFilePanel("Open BasisLoadableConfiguration XML", "", "xml");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Element("BasisLoadableConfiguration");
            if (root == null) throw new System.Exception("Missing root element 'BasisLoadableConfiguration'.");

            int TryInt(string name, int def = 0)
            {
                var e = root.Element(name);
                return e != null && int.TryParse(e.Value, out var v) ? v : def;
            }
            bool TryBool(string name, bool def = false)
            {
                var e = root.Element(name);
                return e != null && bool.TryParse(e.Value, out var v) ? v : def;
            }
            float TryFloat(string name, float def = 0f)
            {
                var e = root.Element(name);
                if (e == null) return def;
                if (float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                if (float.TryParse(e.Value, out v)) return v;
                return def;
            }
            string TryString(string name, string def = "")
            {
                var e = root.Element(name);
                return e != null ? e.Value : def;
            }

            mode = TryInt("Mode", 0);
            loadedNetID = TryString("LoadedNetID", "");
            unlockPassword = TryString("UnlockPassword", unlockPassword);
            combinedURL = TryString("CombinedURL", combinedURL);
            isLocalLoad = TryBool("IsLocalLoad", false);

            Selectedposition = new Vector3(
                TryFloat("PositionX", Selectedposition.x),
                TryFloat("PositionY", Selectedposition.y),
                TryFloat("PositionZ", Selectedposition.z)
            );

            rotation = new Quaternion(
                TryFloat("QuaternionX", rotation.x),
                TryFloat("QuaternionY", rotation.y),
                TryFloat("QuaternionZ", rotation.z),
                TryFloat("QuaternionW", rotation.w)
            );

            scale = new Vector3(
                TryFloat("ScaleX", scale.x),
                TryFloat("ScaleY", scale.y),
                TryFloat("ScaleZ", scale.z)
            );

            persist = TryBool("Persist", true);

            Repaint();
            EditorUtility.DisplayDialog("Loaded", $"Parsed XML:\n{path}", "OK");
        }
        catch (System.Exception ex)
        {
            Debug.LogError(ex);
            EditorUtility.DisplayDialog("Error", "Failed to load XML:\n" + ex.Message, "OK");
        }
    }
}
