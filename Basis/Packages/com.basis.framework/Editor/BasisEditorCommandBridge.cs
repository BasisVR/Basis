using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Basis;
using Basis.BasisUI;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class BasisEditorCommandBridge
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "BasisEditorCommands");
    private static readonly Queue<string> pending = new Queue<string>();
    private static double nextPoll;
    private static int waitFrames;
    private static string captureName;
    private static int captureCount, captureIndex;
    private static readonly List<string> log = new List<string>();
    private static Camera pinned;
    private static Vector3 pinnedPosition;
    private static Quaternion pinnedRotation;
    private static float spinRate;
    private static int spinFrame = -1;

    static BasisEditorCommandBridge()
    {
        Directory.CreateDirectory(Folder);
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= Pin;
        UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += Pin;
    }

    private static void Pin(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
    {
        if (pinned == null || camera != pinned) { return; }
        if (spinRate != 0f && spinFrame != Time.frameCount) { spinFrame = Time.frameCount; pinnedRotation = Quaternion.Euler(0f, spinRate, 0f) * pinnedRotation; }
        camera.transform.SetPositionAndRotation(pinnedPosition, pinnedRotation);
    }

    private static void Write(string line)
    {
        log.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
        try { File.WriteAllLines(Path.Combine(Folder, "status.txt"), log); } catch { }
    }

    private static void Tick()
    {
        if (pending.Count == 0 && captureCount == 0 && waitFrames == 0 && EditorApplication.timeSinceStartup >= nextPoll)
        {
            nextPoll = EditorApplication.timeSinceStartup + 0.2;
            string file = Path.Combine(Folder, "commands.txt");
            if (!File.Exists(file)) { return; }
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); } catch { return; }
            log.Clear();
            foreach (string line in lines) { if (!string.IsNullOrWhiteSpace(line)) { pending.Enqueue(line.Trim()); } }
            Write("received " + pending.Count + " commands");
        }
        if (captureCount > 0)
        {
            string path = Path.Combine(Folder, captureName + "_" + captureIndex.ToString("D2") + ".png");
            ScreenCapture.CaptureScreenshot(path);
            captureIndex++;
            if (captureIndex >= captureCount) { Write("captured " + captureCount + " frames as " + captureName); captureCount = 0; }
            return;
        }
        if (waitFrames > 0) { waitFrames--; return; }
        while (pending.Count > 0 && captureCount == 0 && waitFrames == 0)
        {
            string command = pending.Dequeue();
            try { Execute(command); }
            catch (Exception e) { Write("error in '" + command + "': " + e.Message); }
        }
        if (pending.Count == 0 && captureCount == 0 && waitFrames == 0 && log.Count > 0 && !log[log.Count - 1].EndsWith("done")) { Write("done"); }
    }

    private static void Execute(string command)
    {
        string[] parts = command.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "set":
                SetBinding(parts[1], parts.Length > 2 ? parts[2] : string.Empty);
                break;
            case "get":
                Write(parts[1] + " = " + GetBinding(parts[1]));
                break;
            case "wait":
                waitFrames = int.Parse(parts[1], CultureInfo.InvariantCulture);
                Write("waiting " + waitFrames + " frames");
                break;
            case "capture":
                captureName = parts[1];
                captureCount = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 1;
                captureIndex = 0;
                break;
            case "play":
                EditorApplication.EnterPlaymode();
                Write("entering play mode");
                break;
            case "stop":
                EditorApplication.ExitPlaymode();
                Write("exiting play mode");
                break;
            case "state":
                Write("playing " + EditorApplication.isPlaying + ", compiling " + EditorApplication.isCompiling + ", frame " + Time.frameCount + ", fps " + (1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)).ToString("F1", CultureInfo.InvariantCulture));
                break;
            case "pin":
                pinned = Camera.main;
                if (pinned == null) { foreach (Camera c in Camera.allCameras) { if (c.cameraType == CameraType.Game && c.targetTexture == null) { pinned = c; break; } } }
                if (pinned != null)
                {
                    pinnedPosition = pinned.transform.position;
                    pinnedRotation = pinned.transform.rotation;
                    if (parts.Length > 1)
                    {
                        string[] v = (parts[1] + (parts.Length > 2 ? " " + parts[2] : string.Empty)).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (v.Length >= 6)
                        {
                            pinnedPosition = new Vector3(float.Parse(v[0], CultureInfo.InvariantCulture), float.Parse(v[1], CultureInfo.InvariantCulture), float.Parse(v[2], CultureInfo.InvariantCulture));
                            pinnedRotation = Quaternion.Euler(float.Parse(v[3], CultureInfo.InvariantCulture), float.Parse(v[4], CultureInfo.InvariantCulture), float.Parse(v[5], CultureInfo.InvariantCulture));
                        }
                    }
                }
                Write(pinned != null ? "pinned " + pinned.name + " at " + pinnedPosition.ToString("F2") + " " + pinnedRotation.eulerAngles.ToString("F1") : "no camera to pin");
                break;
            case "spin":
                spinRate = float.Parse(parts[1], CultureInfo.InvariantCulture);
                Write("spin " + spinRate + " degrees per frame");
                break;
            case "unpin":
                pinned = null;
                spinRate = 0f;
                Write("unpinned");
                break;
            case "skincheck":
                SkinCheck();
                break;
            case "matcheck":
                MatCheck();
                break;
            case "hideui":
                {
                    int ui = 0;
                    foreach (string layer in new[] { "UI", "OverlayUI", "HandHeldCameraUI" }) { int index = LayerMask.NameToLayer(layer); if (index >= 0) { ui |= 1 << index; } }
                    foreach (Camera c in Camera.allCameras) { if (c.cameraType == CameraType.Game) { c.cullingMask &= ~ui; } }
                    Write("hid ui layers on " + Camera.allCamerasCount + " cameras");
                }
                break;
            case "focus":
                EditorApplication.ExecuteMenuItem("Window/General/Game");
                Write("focused game view");
                break;
            case "quit":
                Write("quitting");
                EditorApplication.Exit(0);
                break;
            case "avatarfallback":
                {
                    object player = FindType("BasisLocalPlayer")?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    player?.GetType().GetMethod("LoadFallbackAvatar")?.Invoke(player, null);
                    Write(player != null ? "loading fallback avatar" : "no local player");
                }
                break;
            case "avatarrestore":
                {
                    Type local = FindType("BasisLocalPlayer");
                    object player = local?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    MethodInfo load = FindType("BasisDataStore")?.GetMethod("LoadAvatar", BindingFlags.Public | BindingFlags.Static);
                    if (player == null || load == null) { Write("cannot restore avatar"); break; }
                    object[] args = { local.GetField("LoadFileNameAndExtension").GetValue(null), FindType("BasisBeeConstants").GetField("DefaultAvatar").GetValue(null), (byte)1, null };
                    bool loaded = (bool)load.Invoke(null, args);
                    if (loaded) { local.GetMethod("LoadInitialAvatar").Invoke(player, new[] { args[3], true }); }
                    Write(loaded ? "restoring saved avatar" : "no saved avatar");
                }
                break;
            case "call":
                {
                    int dot = parts[1].LastIndexOf('.');
                    MethodInfo method = FindType(parts[1].Substring(0, dot))?.GetMethod(parts[1].Substring(dot + 1), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method == null) { Write("no static method " + parts[1]); break; }
                    method.Invoke(null, null);
                    Write("called " + parts[1]);
                }
                break;
            case "setfield":
                {
                    int dot = parts[1].LastIndexOf('.');
                    FieldInfo field = FindType(parts[1].Substring(0, dot))?.GetField(parts[1].Substring(dot + 1), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field == null) { Write("no static field " + parts[1]); break; }
                    field.SetValue(null, Convert.ChangeType(parts[2], field.FieldType, CultureInfo.InvariantCulture));
                    Write("set " + parts[1] + " = " + field.GetValue(null));
                }
                break;
            case "lightmaps":
                {
                    LightmapData[] maps = LightmapSettings.lightmaps;
                    string line = "lightmaps " + maps.Length + " mode " + LightmapSettings.lightmapsMode;
                    for (int i = 0; i < maps.Length && i < 6; i++) { Texture2D t = maps[i].lightmapColor; line += " | " + (t != null ? t.name + " " + t.format + " " + t.graphicsFormat + " " + t.width + "x" + t.height : "null"); }
                    Write(line);
                }
                break;
            case "rtstats":
                RayTracerStats();
                break;
            case "eval":
                Write(parts[1] + " = " + Evaluate(parts[1]));
                break;
            default:
                Write("unknown command: " + command);
                break;
        }
    }

    private static void MatCheck()
    {
        foreach (SkinnedMeshRenderer smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude))
        {
            Material[] mats = smr.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) { continue; }
                System.Text.StringBuilder line = new System.Text.StringBuilder(smr.name + "[" + i + "] " + m.shader.name + " q" + m.renderQueue + " kw:" + string.Join(",", m.shaderKeywords));
                Shader sh = m.shader;
                for (int p = 0; p < sh.GetPropertyCount(); p++)
                {
                    string n = sh.GetPropertyName(p);
                    if (n.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (i == 0 && smr.name == "Body" && n.IndexOf("GlobalMask", StringComparison.OrdinalIgnoreCase) < 0 && (n.StartsWith("_EmissionMask") || n.StartsWith("_EmissionMap") || n.StartsWith("_EmissionUV") || n.StartsWith("_EmissionStrength") || n.StartsWith("_EmissionBaseColorAsMap") || n.StartsWith("_EmissionReplace") || n.StartsWith("_EnableEmission"))) { Write("attr " + n + " : " + string.Join(" | ", sh.GetPropertyAttributes(p))); }
                    switch (sh.GetPropertyType(p))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: line.Append(" " + n + "=" + m.GetColor(n).ToString("F2")); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range: { float v = m.GetFloat(n); if (v != 0f) { line.Append(" " + n + "=" + v.ToString("G3", CultureInfo.InvariantCulture)); } break; }
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: { Texture t = m.GetTexture(n); if (t != null) { line.Append(" " + n + "=" + t.name + "(" + t.width + ") st" + m.GetTextureScale(n).ToString("F2") + m.GetTextureOffset(n).ToString("F2") + " " + TextureStats(t)); } break; }
                    }
                }
                Write(line.ToString());
            }
        }
    }

    private static string TextureStats(Texture t)
    {
        RenderTexture rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        Graphics.Blit(t, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D read = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Color[] px = read.GetPixels();
        UnityEngine.Object.DestroyImmediate(read);
        Vector4 mean = Vector4.zero, max = Vector4.zero, lit = Vector4.zero;
        foreach (Color c in px) { Vector4 v = c; mean += v; max = Vector4.Max(max, v); lit += new Vector4(c.r > 0.1f ? 1 : 0, c.g > 0.1f ? 1 : 0, c.b > 0.1f ? 1 : 0, c.a > 0.1f ? 1 : 0); }
        mean /= px.Length; lit /= px.Length;
        return "mean" + mean.ToString("F3") + " max" + max.ToString("F2") + " cover" + lit.ToString("F3");
    }

    private static void SkinCheck()
    {
        foreach (SkinnedMeshRenderer smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude))
        {
            if (!smr.enabled || smr.sharedMesh == null) { continue; }
            smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            using (GraphicsBuffer buffer = smr.GetVertexBuffer())
            {
                if (buffer == null) { Write(smr.name + ": no skinned buffer yet"); continue; }
                int stride = buffer.stride, count = Mathf.Min(smr.sharedMesh.vertexCount, 20000);
                byte[] raw = new byte[stride * count];
                buffer.GetData(raw, 0, 0, raw.Length);
                Matrix4x4[] candidates = { smr.rootBone != null ? smr.rootBone.localToWorldMatrix : Matrix4x4.identity, smr.transform.localToWorldMatrix, smr.localToWorldMatrix, smr.rootBone != null ? Matrix4x4.TRS(smr.rootBone.position, smr.rootBone.rotation, Vector3.one) : Matrix4x4.identity };
                string[] names = { "rootBone", "transform", "renderer", "rootBoneNoScale" };
                Bounds truth = smr.bounds;
                string result = smr.name + " truth c" + truth.center.ToString("F2") + " s" + truth.size.ToString("F2");
                for (int c = 0; c < candidates.Length; c++)
                {
                    Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 p = new Vector3(BitConverter.ToSingle(raw, i * stride), BitConverter.ToSingle(raw, i * stride + 4), BitConverter.ToSingle(raw, i * stride + 8));
                        Vector3 w = candidates[c].MultiplyPoint3x4(p);
                        min = Vector3.Min(min, w);
                        max = Vector3.Max(max, w);
                    }
                    Vector3 centre = (min + max) * 0.5f, size = max - min;
                    result += " | " + names[c] + " c" + centre.ToString("F2") + " s" + size.ToString("F2") + " err " + ((centre - truth.center).magnitude + (size - truth.size).magnitude).ToString("F2");
                }
                Write(result);
            }
        }
    }

    private static object FindBinding(string key)
    {
        foreach (FieldInfo field in typeof(BasisSettingsDefaults).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object value = field.GetValue(null);
            if (value is IBasisSettingsBinding binding && string.Equals(binding.BindingKey, key, StringComparison.OrdinalIgnoreCase)) { return value; }
            if (string.Equals(field.Name, key, StringComparison.OrdinalIgnoreCase)) { return value; }
        }
        return null;
    }

    private static string GetBinding(string key)
    {
        object binding = FindBinding(key);
        if (binding == null) { return "<no binding>"; }
        return Convert.ToString(binding.GetType().GetField("RawValue").GetValue(binding), CultureInfo.InvariantCulture);
    }

    private static void SetBinding(string key, string text)
    {
        object binding = FindBinding(key);
        if (binding == null) { Write("no binding " + key); return; }
        Type type = binding.GetType().GetGenericArguments()[0];
        object value = type == typeof(string) ? text : Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        binding.GetType().GetMethod("SetValue").Invoke(binding, new[] { value });
        Write("set " + key + " = " + text);
    }

    private static Type FindType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) { foreach (Type t in assembly.GetTypes()) { if (t.Name == name) { return t; } } }
        return null;
    }

    private static void RayTracerStats()
    {
        Type type = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) { type = assembly.GetType("BasisExternalRayTracer"); if (type != null) { break; } }
        MethodInfo stat = type?.GetMethod("Stat", BindingFlags.Public | BindingFlags.Static);
        if (stat == null) { Write("no external tracer"); return; }
        string[] names = { "total", "scene", "update", "resolve", "probes", "gather", "temporal", "wavelet" };
        string line = "rt gpu us:";
        for (int i = 0; i < names.Length; i++) { line += " " + names[i] + " " + stat.Invoke(null, new object[] { 10 + i }); }
        for (int i = 0; i < 10; i++) { line += " s" + i + "=" + stat.Invoke(null, new object[] { i }); }
        Write(line);
    }

    private static string Evaluate(string expression)
    {
        string[] path = expression.Split('.');
        Type type = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(path[0]);
            if (type != null) { break; }
        }
        if (type == null) { return "<no type>"; }
        object current = null;
        for (int i = 1; i < path.Length; i++)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | (current == null ? BindingFlags.Static : BindingFlags.Instance);
            Type owner = current == null ? type : current.GetType();
            PropertyInfo property = owner.GetProperty(path[i], flags);
            FieldInfo field = owner.GetField(path[i], flags);
            current = property != null ? property.GetValue(current) : field?.GetValue(current);
            if (current == null) { return "null"; }
        }
        return Convert.ToString(current, CultureInfo.InvariantCulture);
    }
}
