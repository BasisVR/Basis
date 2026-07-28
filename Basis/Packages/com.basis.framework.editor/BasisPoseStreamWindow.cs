using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEditor;
using UnityEngine;
using Basis.IK;

public class BasisPoseStreamWindow : EditorWindow
{
    struct Row
    {
        public int Index;
        public int Parent;
        public string Name;
        public bool Writable;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 StreamWorldPosition;
        public Quaternion StreamWorldRotation;
        public Vector3 ActualWorldPosition;
        public Quaternion ActualWorldRotation;
        public float BindLength;
        public float CurrentLength;
        public bool TranslationFree;
        public float PositionErrorMm;
        public float RotationErrorDeg;
    }

    readonly List<Row> _rows = new List<Row>();
    Vector2 _scroll;
    bool _autoRefresh = true;
    string _status = "Enter play mode and load an avatar.";
    string _anchorInfo = "";
    string _calibInfo = "";

    [MenuItem("Basis/Debug/Pose Stream")]
    public static void Open()
    {
        GetWindow<BasisPoseStreamWindow>("Pose Stream").Show();
    }

    void OnInspectorUpdate()
    {
        if (_autoRefresh && Application.isPlaying)
        {
            Capture();
            Repaint();
        }
    }

    static BasisPoseSkeleton GetSkeleton(out string status)
    {
        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            status = "No BasisLocalPlayer.";
            return null;
        }
        var driver = player.LocalRigDriver;
        if (driver == null)
        {
            status = "No LocalRigDriver.";
            return null;
        }
        if (driver.PoseSkeleton == null || !driver.PoseSkeleton.IsCreated)
        {
            status = "PoseSkeleton not built (no avatar loaded yet?).";
            return null;
        }
        status = $"RigLayerActive={driver.RigLayerActive} IKJobCreated={driver.IKJobCreated}";
        return driver.PoseSkeleton;
    }

    void Capture()
    {
        _rows.Clear();
        var skeleton = GetSkeleton(out _status);
        if (skeleton == null)
        {
            _anchorInfo = "";
            return;
        }

        Transform anchor = skeleton.Anchor;
        _anchorInfo = anchor != null
            ? $"anchor={anchor.name}  pos={anchor.position}  rot={anchor.rotation.eulerAngles}  lossyScale={anchor.lossyScale}"
            : "anchor=<none>  (treated as identity)";

        var rig = BasisLocalPlayer.Instance.LocalRigDriver;
        var d = rig.IKDataReady ? rig.IKJob : default;
        var calib = new StringBuilder();
        calib.AppendLine($"HasRecalibratedRotationOffsets={BasisLocalRigDriver.HasRecalibratedRotationOffsets}");
        calib.AppendLine($"  LeftFoot  live={Fmt(d.offsetRotationLeftFoot.eulerAngles)}   static={Fmt(BasisLocalRigDriver.RecalibratedLeftFoot.eulerAngles)}   |q|={QLen(d.offsetRotationLeftFoot):F4}");
        calib.AppendLine($"  RightFoot live={Fmt(d.offsetRotationRightFoot.eulerAngles)}   static={Fmt(BasisLocalRigDriver.RecalibratedRightFoot.eulerAngles)}   |q|={QLen(d.offsetRotationRightFoot):F4}");
        calib.AppendLine($"  boneSim   L={Fmt(BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData.rotation.eulerAngles)}   R={Fmt(BasisLocalBoneDriver.RightFootControl.OutgoingWorldData.rotation.eulerAngles)}");
        calib.AppendLine($"SOLVE-W leg L={d.enabledLeftLowerLeg:F2} R={d.enabledRightLowerLeg:F2}   HINT-W knee L={d.hintWeightLeftLowerLeg:F2} R={d.hintWeightRightLowerLeg:F2}   toe L={d.leftToeEnabled} R={d.rightToeEnabled}   (hint>0 = foot-driver pole, 0 = swivel model)");
        calib.Append($"HasRigLayer  LFoot={BasisLocalBoneDriver.LeftFootControl.HasRigLayer} RFoot={BasisLocalBoneDriver.RightFootControl.HasRigLayer}" +
                     $"  LLowerLeg={BasisLocalBoneDriver.LeftLowerLegControl.HasRigLayer} RLowerLeg={BasisLocalBoneDriver.RightLowerLegControl.HasRigLayer}");
        for (int leg = 0; leg < 2; leg++)
        {
            if (rig.TryGetLegDiagnostics(leg, out Basis.IK.BasisLegDiagnostics dg))
            {
                string name = leg == 0 ? "L" : "R";
                calib.AppendLine();
                calib.Append($"LEG {name}  reach={dg.ReachRatio:F3} knee={dg.KneeAngleDeg:F1}deg  axisSrc={dg.AxisSource:F0} " +
                             $"hintApplied={dg.HintApplied:F0} modelUsed={dg.ModelHintUsed:F0} conf={dg.ModelConfidence:F2} distrust={dg.HintDistrust:F2}  " +
                             $"raw={dg.RawSwivelDeg:F1} smooth={dg.SmoothSwivelDeg:F1} cond={dg.Conditioning:F3} hold={dg.HoldGate:F2} " +
                             $"antGuard={dg.AnteriorGuardApplied:F0} seeded={dg.Seeded:F0}");
            }
        }
        _calibInfo = calib.ToString();

        Transform[] nodes = skeleton.DebugNodes;
        var stream = skeleton.Stream;
        for (int i = 0; i < nodes.Length; i++)
        {
            Transform t = nodes[i];
            stream.GetWorldPositionAndRotation(i, out Vector3 worldPosition, out Quaternion worldRotation);
            t.GetPositionAndRotation(out Vector3 actualPosition, out Quaternion actualRotation);

            _rows.Add(new Row
            {
                Index = i,
                Parent = stream.Parent[i],
                Name = t.name,
                Writable = skeleton.IsWritable(i),
                BindLength = skeleton.BindLengthOf(i),
                CurrentLength = ((Vector3)stream.LocalPosition[i]).magnitude,
                TranslationFree = skeleton.TranslationFreeOf(i),
                LocalPosition = stream.LocalPosition[i],
                LocalRotation = stream.LocalRotation[i],
                StreamWorldPosition = worldPosition,
                StreamWorldRotation = worldRotation,
                ActualWorldPosition = actualPosition,
                ActualWorldRotation = actualRotation,
                PositionErrorMm = Vector3.Distance(worldPosition, actualPosition) * 1000f,
                RotationErrorDeg = Quaternion.Angle(worldRotation, actualRotation),
            });
        }
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh", GUILayout.Width(80)))
            {
                Capture();
            }
            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto refresh", GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(_rows.Count == 0))
            {
                if (GUILayout.Button("Export CSV", GUILayout.Width(100)))
                {
                    ExportCsv();
                }
            }
        }

        EditorGUILayout.HelpBox(_status, MessageType.None);
        if (!string.IsNullOrEmpty(_anchorInfo))
        {
            EditorGUILayout.LabelField(_anchorInfo, EditorStyles.miniLabel);
        }
        if (!string.IsNullOrEmpty(_calibInfo))
        {
            EditorGUILayout.TextArea(_calibInfo, EditorStyles.miniLabel);
        }

        if (_rows.Count == 0)
        {
            return;
        }

        float worstPosition = 0f;
        float worstRotation = 0f;
        string worstPositionName = "";
        string worstRotationName = "";
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].PositionErrorMm > worstPosition)
            {
                worstPosition = _rows[i].PositionErrorMm;
                worstPositionName = _rows[i].Name;
            }
            if (_rows[i].RotationErrorDeg > worstRotation)
            {
                worstRotation = _rows[i].RotationErrorDeg;
                worstRotationName = _rows[i].Name;
            }
        }
        EditorGUILayout.LabelField(
            $"worst pos {worstPosition:F3} mm ({worstPositionName})    worst rot {worstRotation:F3}° ({worstRotationName})",
            EditorStyles.boldLabel);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("#", GUILayout.Width(28));
            GUILayout.Label("par", GUILayout.Width(28));
            GUILayout.Label("bone", GUILayout.Width(150));
            GUILayout.Label("W", GUILayout.Width(20));
            GUILayout.Label("bindLen", GUILayout.Width(70));
            GUILayout.Label("curLen", GUILayout.Width(70));
            GUILayout.Label("stretch%", GUILayout.Width(70));
            GUILayout.Label("posErr mm", GUILayout.Width(80));
            GUILayout.Label("rotErr deg", GUILayout.Width(80));
            GUILayout.Label("stream world pos", GUILayout.Width(190));
            GUILayout.Label("actual world pos", GUILayout.Width(190));
            GUILayout.Label("stream world euler", GUILayout.Width(190));
            GUILayout.Label("actual world euler", GUILayout.Width(190));
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            bool bad = row.PositionErrorMm > 0.5f || row.RotationErrorDeg > 0.05f;
            var style = new GUIStyle(EditorStyles.label);
            if (bad)
            {
                style.normal.textColor = Color.red;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(row.Index.ToString(), style, GUILayout.Width(28));
                GUILayout.Label(row.Parent.ToString(), style, GUILayout.Width(28));
                GUILayout.Label(row.Name, style, GUILayout.Width(150));
                GUILayout.Label(row.Writable ? "*" : "", style, GUILayout.Width(20));
                float stretch = row.BindLength > 1e-6f ? (row.CurrentLength / row.BindLength - 1f) * 100f : 0f;
                var stretchStyle = new GUIStyle(style);
                if (!row.TranslationFree && stretch > 0.5f) { stretchStyle.normal.textColor = new Color(1f, 0.5f, 0f); }
                GUILayout.Label(row.BindLength.ToString("F4"), style, GUILayout.Width(70));
                GUILayout.Label(row.CurrentLength.ToString("F4"), style, GUILayout.Width(70));
                GUILayout.Label(row.TranslationFree ? "free" : stretch.ToString("F2"), stretchStyle, GUILayout.Width(70));
                GUILayout.Label(row.PositionErrorMm.ToString("F3"), style, GUILayout.Width(80));
                GUILayout.Label(row.RotationErrorDeg.ToString("F3"), style, GUILayout.Width(80));
                GUILayout.Label(Fmt(row.StreamWorldPosition), style, GUILayout.Width(190));
                GUILayout.Label(Fmt(row.ActualWorldPosition), style, GUILayout.Width(190));
                GUILayout.Label(Fmt(row.StreamWorldRotation.eulerAngles), style, GUILayout.Width(190));
                GUILayout.Label(Fmt(row.ActualWorldRotation.eulerAngles), style, GUILayout.Width(190));
            }
        }
        EditorGUILayout.EndScrollView();
    }

    static float QLen(Quaternion q) => Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);

    static string Fmt(Vector3 v) => $"{v.x:F4}, {v.y:F4}, {v.z:F4}";

    void ExportCsv()
    {
        string path = EditorUtility.SaveFilePanel("Export Pose Stream", "", "posestream.csv", "csv");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# " + _status);
        sb.AppendLine("# " + _anchorInfo);
        foreach (string line in _calibInfo.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            sb.AppendLine("# " + line);
        }
        sb.AppendLine("index,parent,bone,writable,bindLen,curLen,translationFree,posErrMm,rotErrDeg," +
                      "localPosX,localPosY,localPosZ,localRotX,localRotY,localRotZ,localRotW," +
                      "streamWorldPosX,streamWorldPosY,streamWorldPosZ," +
                      "actualWorldPosX,actualWorldPosY,actualWorldPosZ," +
                      "streamWorldRotX,streamWorldRotY,streamWorldRotZ,streamWorldRotW," +
                      "actualWorldRotX,actualWorldRotY,actualWorldRotZ,actualWorldRotW");

        var c = CultureInfo.InvariantCulture;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            sb.AppendLine(string.Join(",", new string[]
            {
                r.Index.ToString(c), r.Parent.ToString(c), r.Name, r.Writable ? "1" : "0",
                r.BindLength.ToString("F6", c), r.CurrentLength.ToString("F6", c), r.TranslationFree ? "1" : "0",
                r.PositionErrorMm.ToString("F6", c), r.RotationErrorDeg.ToString("F6", c),
                r.LocalPosition.x.ToString("F6", c), r.LocalPosition.y.ToString("F6", c), r.LocalPosition.z.ToString("F6", c),
                r.LocalRotation.x.ToString("F6", c), r.LocalRotation.y.ToString("F6", c), r.LocalRotation.z.ToString("F6", c), r.LocalRotation.w.ToString("F6", c),
                r.StreamWorldPosition.x.ToString("F6", c), r.StreamWorldPosition.y.ToString("F6", c), r.StreamWorldPosition.z.ToString("F6", c),
                r.ActualWorldPosition.x.ToString("F6", c), r.ActualWorldPosition.y.ToString("F6", c), r.ActualWorldPosition.z.ToString("F6", c),
                r.StreamWorldRotation.x.ToString("F6", c), r.StreamWorldRotation.y.ToString("F6", c), r.StreamWorldRotation.z.ToString("F6", c), r.StreamWorldRotation.w.ToString("F6", c),
                r.ActualWorldRotation.x.ToString("F6", c), r.ActualWorldRotation.y.ToString("F6", c), r.ActualWorldRotation.z.ToString("F6", c), r.ActualWorldRotation.w.ToString("F6", c),
            }));
        }

        File.WriteAllText(path, sb.ToString());
        BasisDebug.Log($"Pose stream exported to {path}");
    }
}
