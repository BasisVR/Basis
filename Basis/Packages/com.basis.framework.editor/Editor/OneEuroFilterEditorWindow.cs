#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public class OneEuroFilterEditorWindow : EditorWindow
{
    // --- UI state ---
    private float _minCutoff = 1.0f;
    private float _beta = 0.0f;
    private float _derivativeCutoff = 1.0f;
    private bool _resetState = true;

    private float? _runtimeMinCutoff;
    private float? _runtimeBeta;
    private float? _runtimeDerivativeCutoff;

    private string _status = "Looking for UpdateOneEuroParameters(float,float,float,bool)...";
    private MessageType _statusType = MessageType.Info;

    // --- Reflection state ---
    private Type _runtimeType;
    private MethodInfo _miUpdateParams;
    private FieldInfo _fiMinCutoff;
    private FieldInfo _fiBeta;
    private FieldInfo _fiDerivativeCutoff;

    // Optional: If auto-discovery fails, you can type a class name here and click "Find By Name".
    private string _fallbackQualifiedTypeName = "";

    [MenuItem("Basis/Remote One Euro Filter Settings")]
    public static void ShowWindow()
    {
        var wnd = GetWindow<OneEuroFilterEditorWindow>();
        wnd.titleContent = new GUIContent("Basis One Euro Filter");
        wnd.minSize = new Vector2(420, 320);
        wnd.Show();
    }

    private void OnEnable()
    {
        TryDiscoverRuntimeType();
        PullRuntimeValuesIntoUI();
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // Refresh runtime values when entering/exiting play mode
        PullRuntimeValuesIntoUI();
        Repaint();
    }

    private void OnGUI()
    {
        GUILayout.Space(6);

        // Status box
        EditorGUILayout.HelpBox(_status, _statusType);

        // Discovery / fallback controls
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Runtime Class Discovery", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Detected Type", _runtimeType != null ? _runtimeType.FullName : "(not found)");
            }

            _fallbackQualifiedTypeName = EditorGUILayout.TextField(
                new GUIContent("Or search by name",
                    "Fully qualified type name if auto-discovery fails. Example: MyGame.Runtime.SharedNetwork"),
                _fallbackQualifiedTypeName);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-scan Assemblies"))
                {
                    TryDiscoverRuntimeType();
                    PullRuntimeValuesIntoUI();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_fallbackQualifiedTypeName)))
                {
                    if (GUILayout.Button("Find By Name"))
                    {
                        TryFindByQualifiedName(_fallbackQualifiedTypeName.Trim());
                        PullRuntimeValuesIntoUI();
                    }
                }
            }
        }

        GUILayout.Space(6);

        // Show current runtime values (if fields discovered)
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Current Runtime Values", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("MinCutoff (Hz)", _runtimeMinCutoff ?? float.NaN);
                EditorGUILayout.FloatField("Beta", _runtimeBeta ?? float.NaN);
                EditorGUILayout.FloatField("DerivativeCutoff (Hz)", _runtimeDerivativeCutoff ?? float.NaN);
            }

            if (GUILayout.Button("Reload From Runtime"))
            {
                PullRuntimeValuesIntoUI();
            }
        }

        GUILayout.Space(6);

        // Editable values to push
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("New Parameters", EditorStyles.boldLabel);

            _minCutoff = EditorGUILayout.FloatField(new GUIContent("MinCutoff (Hz)", "New MinCutoff (Hz)."), _minCutoff);
            _beta = EditorGUILayout.FloatField(new GUIContent("Beta", "New Beta (cutoff slope vs speed)."), _beta);
            _derivativeCutoff = EditorGUILayout.FloatField(new GUIContent("DerivativeCutoff (Hz)", "New DerivativeCutoff (Hz)."), _derivativeCutoff);

            _resetState = EditorGUILayout.Toggle(
                new GUIContent("Reset State",
                    "If true, clears PositionFilters, DerivativeFilters, and euroValuesOutput so the filter re-converges."),
                _resetState);

            GUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_miUpdateParams == null))
                {
                    if (GUILayout.Button(new GUIContent("Apply", "Call UpdateOneEuroParameters(minCutoff, beta, derivativeCutoff, resetState)")))
                    {
                        ApplyParameters(_minCutoff, _beta, _derivativeCutoff, _resetState);
                    }

                    if (GUILayout.Button(new GUIContent("Apply (No Reset)", "Apply with resetState = false")))
                    {
                        ApplyParameters(_minCutoff, _beta, _derivativeCutoff, false);
                    }
                }

                if (GUILayout.Button(new GUIContent("Copy From Current", "Copy currently detected runtime values into the editable fields")))
                {
                    if (_runtimeMinCutoff.HasValue) _minCutoff = _runtimeMinCutoff.Value;
                    if (_runtimeBeta.HasValue) _beta = _runtimeBeta.Value;
                    if (_runtimeDerivativeCutoff.HasValue) _derivativeCutoff = _runtimeDerivativeCutoff.Value;
                }
            }
        }

        GUILayout.FlexibleSpace();

        // Footer
        EditorGUILayout.LabelField("Tip: Keep this window open during Play Mode to live-tune smoothing.", EditorStyles.centeredGreyMiniLabel);
    }

    // ------------------------------------------------------------
    // Discovery & reflection helpers
    // ------------------------------------------------------------

    private void TryDiscoverRuntimeType()
    {
        _runtimeType = null;
        _miUpdateParams = null;
        _fiMinCutoff = null;
        _fiBeta = null;
        _fiDerivativeCutoff = null;

        try
        {
            // Heuristic 1: Find a type that exposes the exact static method:
            // public static void UpdateOneEuroParameters(float, float, float, bool)
            var all = AppDomain.CurrentDomain.GetAssemblies()
                // Exclude dynamic or editor-only system assemblies to speed up
                .Where(a =>
                {
                    string n = a.GetName().Name;
                    return !(a.IsDynamic ||
                             n.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                             n.StartsWith("Unity.", StringComparison.Ordinal) ||
                             n.StartsWith("System", StringComparison.Ordinal) ||
                             n.StartsWith("mscorlib", StringComparison.Ordinal) ||
                             n.StartsWith("netstandard", StringComparison.Ordinal));
                });

            foreach (var asm in all)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    var mi = t.GetMethod("UpdateOneEuroParameters",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(float), typeof(float), typeof(float), typeof(bool) },
                        modifiers: null);

                    if (mi != null)
                    {
                        _runtimeType = t;
                        _miUpdateParams = mi;
                        // Best-effort to find the known static fields
                        _fiMinCutoff = FindFloatField(t, "MinCutoff");
                        _fiBeta = FindFloatField(t, "Beta");
                        _fiDerivativeCutoff = FindFloatField(t, "DerivativeCutoff");

                        SetStatus($"Found {_runtimeType.FullName}", MessageType.Info);
                        return;
                    }
                }
            }

            SetStatus("Could not auto-discover the runtime class. Enter its full type name and click 'Find By Name'.", MessageType.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("Discovery error: " + ex.Message, MessageType.Error);
        }
    }

    private void TryFindByQualifiedName(string qualifiedTypeName)
    {
        _runtimeType = null;
        _miUpdateParams = null;
        _fiMinCutoff = null;
        _fiBeta = null;
        _fiDerivativeCutoff = null;

        try
        {
            // Try Type.GetType first (works when assembly-qualified)
            _runtimeType = Type.GetType(qualifiedTypeName, throwOnError: false);
            if (_runtimeType == null)
            {
                // Scan all assemblies for short names
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(qualifiedTypeName, throwOnError: false);
                    if (t != null) { _runtimeType = t; break; }
                }
            }

            if (_runtimeType == null)
            {
                SetStatus($"Type '{qualifiedTypeName}' not found.", MessageType.Error);
                return;
            }

            _miUpdateParams = _runtimeType.GetMethod("UpdateOneEuroParameters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(float), typeof(float), typeof(float), typeof(bool) },
                modifiers: null);

            if (_miUpdateParams == null)
            {
                SetStatus($"Type found ({_runtimeType.FullName}) but it does not contain the expected static method signature.", MessageType.Error);
                return;
            }

            _fiMinCutoff = FindFloatField(_runtimeType, "MinCutoff");
            _fiBeta = FindFloatField(_runtimeType, "Beta");
            _fiDerivativeCutoff = FindFloatField(_runtimeType, "DerivativeCutoff");

            SetStatus($"Bound to {_runtimeType.FullName}", MessageType.Info);
        }
        catch (Exception ex)
        {
            SetStatus("Find-by-name error: " + ex.Message, MessageType.Error);
        }
    }

    private static FieldInfo FindFloatField(Type t, string name)
    {
        // Find a static float (or double) field with the given name, public or non-public
        var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (fi != null && (fi.FieldType == typeof(float) || fi.FieldType == typeof(double)))
            return fi;
        return null;
    }

    // ------------------------------------------------------------
    // Runtime value sync
    // ------------------------------------------------------------

    private void PullRuntimeValuesIntoUI()
    {
        // Read runtime (static) fields if available, update UI defaults
        try
        {
            if (_fiMinCutoff != null)
            {
                _runtimeMinCutoff = Convert.ToSingle(_fiMinCutoff.GetValue(null));
                _minCutoff = _runtimeMinCutoff.Value;
            }
            else _runtimeMinCutoff = null;

            if (_fiBeta != null)
            {
                _runtimeBeta = Convert.ToSingle(_fiBeta.GetValue(null));
                _beta = _runtimeBeta.Value;
            }
            else _runtimeBeta = null;

            if (_fiDerivativeCutoff != null)
            {
                _runtimeDerivativeCutoff = Convert.ToSingle(_fiDerivativeCutoff.GetValue(null));
                _derivativeCutoff = _runtimeDerivativeCutoff.Value;
            }
            else _runtimeDerivativeCutoff = null;
        }
        catch (Exception ex)
        {
            SetStatus("Failed to pull current values: " + ex.Message, MessageType.Warning);
        }
    }

    private void ApplyParameters(float minCutoff, float beta, float derivativeCutoff, bool resetState)
    {
        if (_miUpdateParams == null)
        {
            SetStatus("Cannot apply: method not found.", MessageType.Error);
            return;
        }

        try
        {
            // Call: UpdateOneEuroParameters(float minCutoff, float beta, float derivativeCutoff, bool resetState)
            _miUpdateParams.Invoke(null, new object[] { minCutoff, beta, derivativeCutoff, resetState });

            SetStatus($"Applied MinCutoff={minCutoff}, Beta={beta}, DerivativeCutoff={derivativeCutoff}, Reset={resetState}", MessageType.Info);

            // Refresh view of current values
            PullRuntimeValuesIntoUI();
        }
        catch (TargetInvocationException tie)
        {
            SetStatus("Runtime threw: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message), MessageType.Error);
        }
        catch (Exception ex)
        {
            SetStatus("Failed to apply parameters: " + ex.Message, MessageType.Error);
        }
    }

    private void SetStatus(string msg, MessageType type)
    {
        _status = msg;
        _statusType = type;
    }
}
#endif
