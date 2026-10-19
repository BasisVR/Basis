#if UNITY_EDITOR
using Basis.Editor.Localization;
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public sealed class BasisOneEuroFilterTab : BasisEditorTabPage
{
        public override string Title => BasisEditorLocalization.Get("editor.oneEuro.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.oneEuro.subtitle");

    // --- UI state ---
    private float _minCutoff = 1.0f;
    private float _beta = 0.0f;
    private float _derivativeCutoff = 1.0f;
    private bool _resetState = true;

    private float? _runtimeMinCutoff;
    private float? _runtimeBeta;
    private float? _runtimeDerivativeCutoff;

    private string _status = BasisEditorLocalization.Get("editor.oneEuro.statusLooking");
    private MessageType _statusType = MessageType.Info;

    // --- Reflection state ---
    private Type _runtimeType;
    private MethodInfo _miUpdateParams;
    private FieldInfo _fiMinCutoff;
    private FieldInfo _fiBeta;
    private FieldInfo _fiDerivativeCutoff;

    // Optional: If auto-discovery fails, you can type a class name here and click "Find By Name".
    private string _fallbackQualifiedTypeName = "";

    public override void OnEnable()
    {
        TryDiscoverRuntimeType();
        PullRuntimeValuesIntoUI();
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    public override void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // Refresh runtime values when entering/exiting play mode
        PullRuntimeValuesIntoUI();
        Host.Repaint();
    }

    public override void Draw()
    {
        GUILayout.Space(6);

        // Status box
        EditorGUILayout.HelpBox(_status, _statusType);

        // Discovery / fallback controls
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.oneEuro.discoverySection"));

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.oneEuro.detectedType"), _runtimeType != null ? _runtimeType.FullName : BasisEditorLocalization.Get("editor.oneEuro.notFound"));
            }

            _fallbackQualifiedTypeName = EditorGUILayout.TextField(
                new GUIContent(BasisEditorLocalization.Get("editor.oneEuro.searchByName"),
                    BasisEditorLocalization.Get("editor.oneEuro.searchByName.tooltip")),
                _fallbackQualifiedTypeName);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.oneEuro.rescanAssemblies")))
                {
                    TryDiscoverRuntimeType();
                    PullRuntimeValuesIntoUI();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_fallbackQualifiedTypeName)))
                {
                    if (GUILayout.Button(BasisEditorLocalization.Get("editor.oneEuro.findByName")))
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
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.oneEuro.currentValuesSection"));

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("MinCutoff (Hz)", _runtimeMinCutoff ?? float.NaN);
                EditorGUILayout.FloatField("Beta", _runtimeBeta ?? float.NaN);
                EditorGUILayout.FloatField("DerivativeCutoff (Hz)", _runtimeDerivativeCutoff ?? float.NaN);
            }

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.oneEuro.reloadFromRuntime")))
            {
                PullRuntimeValuesIntoUI();
            }
        }

        GUILayout.Space(6);

        // Editable values to push
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.oneEuro.newParametersSection"));

            _minCutoff = EditorGUILayout.FloatField(new GUIContent("MinCutoff (Hz)", BasisEditorLocalization.Get("editor.oneEuro.minCutoff.tooltip")), _minCutoff);
            _beta = EditorGUILayout.FloatField(new GUIContent("Beta", BasisEditorLocalization.Get("editor.oneEuro.beta.tooltip")), _beta);
            _derivativeCutoff = EditorGUILayout.FloatField(new GUIContent("DerivativeCutoff (Hz)", BasisEditorLocalization.Get("editor.oneEuro.derivativeCutoff.tooltip")), _derivativeCutoff);

            _resetState = EditorGUILayout.Toggle(
                new GUIContent(BasisEditorLocalization.Get("editor.oneEuro.resetState"),
                    BasisEditorLocalization.Get("editor.oneEuro.resetState.tooltip")),
                _resetState);

            GUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_miUpdateParams == null))
                {
                    if (GUILayout.Button(new GUIContent(BasisEditorLocalization.Get("editor.common.apply"), BasisEditorLocalization.Get("editor.oneEuro.apply.tooltip"))))
                    {
                        ApplyParameters(_minCutoff, _beta, _derivativeCutoff, _resetState);
                    }

                    if (GUILayout.Button(new GUIContent(BasisEditorLocalization.Get("editor.oneEuro.applyNoReset"), BasisEditorLocalization.Get("editor.oneEuro.applyNoReset.tooltip"))))
                    {
                        ApplyParameters(_minCutoff, _beta, _derivativeCutoff, false);
                    }
                }

                if (GUILayout.Button(new GUIContent(BasisEditorLocalization.Get("editor.oneEuro.copyFromCurrent"), BasisEditorLocalization.Get("editor.oneEuro.copyFromCurrent.tooltip"))))
                {
                    if (_runtimeMinCutoff.HasValue) _minCutoff = _runtimeMinCutoff.Value;
                    if (_runtimeBeta.HasValue) _beta = _runtimeBeta.Value;
                    if (_runtimeDerivativeCutoff.HasValue) _derivativeCutoff = _runtimeDerivativeCutoff.Value;
                }
            }
        }

        GUILayout.FlexibleSpace();

        // Footer
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.oneEuro.tip"), EditorStyles.centeredGreyMiniLabel);
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
            var all = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies()
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

                        SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusFound", _runtimeType.FullName), MessageType.Info);
                        return;
                    }
                }
            }

            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusNotDiscovered"), MessageType.Warning);
        }
        catch (Exception ex)
        {
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusDiscoveryError", ex.Message), MessageType.Error);
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
                foreach (var asm in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
                {
                    var t = asm.GetType(qualifiedTypeName, throwOnError: false);
                    if (t != null) { _runtimeType = t; break; }
                }
            }

            if (_runtimeType == null)
            {
                SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusTypeNotFound", qualifiedTypeName), MessageType.Error);
                return;
            }

            _miUpdateParams = _runtimeType.GetMethod("UpdateOneEuroParameters",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(float), typeof(float), typeof(float), typeof(bool) },
                modifiers: null);

            if (_miUpdateParams == null)
            {
                SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusMissingMethod", _runtimeType.FullName), MessageType.Error);
                return;
            }

            _fiMinCutoff = FindFloatField(_runtimeType, "MinCutoff");
            _fiBeta = FindFloatField(_runtimeType, "Beta");
            _fiDerivativeCutoff = FindFloatField(_runtimeType, "DerivativeCutoff");

            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusBound", _runtimeType.FullName), MessageType.Info);
        }
        catch (Exception ex)
        {
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusFindByNameError", ex.Message), MessageType.Error);
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
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusPullFailed", ex.Message), MessageType.Warning);
        }
    }

    private void ApplyParameters(float minCutoff, float beta, float derivativeCutoff, bool resetState)
    {
        if (_miUpdateParams == null)
        {
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusCannotApply"), MessageType.Error);
            return;
        }

        try
        {
            // Call: UpdateOneEuroParameters(float minCutoff, float beta, float derivativeCutoff, bool resetState)
            _miUpdateParams.Invoke(null, new object[] { minCutoff, beta, derivativeCutoff, resetState });

            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusApplied", minCutoff, beta, derivativeCutoff, resetState), MessageType.Info);

            // Refresh view of current values
            PullRuntimeValuesIntoUI();
        }
        catch (TargetInvocationException tie)
        {
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusRuntimeThrew", tie.InnerException != null ? tie.InnerException.Message : tie.Message), MessageType.Error);
        }
        catch (Exception ex)
        {
            SetStatus(BasisEditorLocalization.Get("editor.oneEuro.statusApplyFailed", ex.Message), MessageType.Error);
        }
    }

    private void SetStatus(string msg, MessageType type)
    {
        _status = msg;
        _statusType = type;
    }
}
#endif
