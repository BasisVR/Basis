using System.Collections.Generic;
using Basis.IK;
using Basis.Scripts.Drivers;
using Unity.Collections;
using UnityEngine;
namespace Basis.Scripts.Debugging
{
    public static class BasisIKSolveGizmos
    {
        const float LineWidthBase = 0.003f;
        const float PointSizeBase = 0.014f;
        const float AxisLengthBase = 0.06f;
        const float LabelScaleBase = 0.016f;
        const int IdleCapacity = 1;
        static readonly List<int> lineIds = new List<int>();
        static readonly List<int> sphereIds = new List<int>();
        static readonly List<int> labelIds = new List<int>();
        static readonly List<FixedString64Bytes> labelText = new List<FixedString64Bytes>();
        static readonly List<string> labelStrings = new List<string>();
        static int linesShown;
        static int spheresShown;
        static int labelsShown;
        static bool registered;
        static bool overflowWarned;
        public static int LineCount => linesShown;
        public static int SphereCount => spheresShown;
        public static int LabelCount => labelsShown;
        public static void Prepare(ref BasisEerieMovement job)
        {
            EnsureMasterToggleHook();

            int mask = BasisIKSolveGizmoStages.Mask();
            bool wantFull = mask != 0;
            bool isFull = job.gizmos.IsCreated && job.gizmos.Draws.Capacity >= BasisIKSolveGizmoStages.DrawCapacity;

            if (!job.gizmos.IsCreated || isFull != wantFull)
            {
                job.gizmos.Create( wantFull ? BasisIKSolveGizmoStages.DrawCapacity : IdleCapacity, wantFull ? BasisIKSolveGizmoStages.LabelCapacity : IdleCapacity);
                job.gizmos.StageColors.Length = BasisIKGizmoRecorder.StageCount;
            }

            job.gizmos.StageMask = mask;
            job.gizmos.Clear();

            if (!wantFull)
            {
                job.gizmos.WantLabels = false;
                return;
            }

            for (int i = 0; i < BasisIKSolveGizmoStages.All.Length; i++)
            {
                int index = BasisIKGizmoRecorder.StageIndex(BasisIKSolveGizmoStages.All[i].Stage);
                if ((uint)index < (uint)job.gizmos.StageColors.Length)
                {
                    job.gizmos.StageColors[index] = BasisIKSolveGizmoStages.PackedColor(i);
                }
            }

            float scale = WorldScale();
            job.gizmos.LineWidth = LineWidthBase * scale;
            job.gizmos.PointSize = PointSizeBase * scale;
            job.gizmos.AxisLength = AxisLengthBase * scale;
            job.gizmos.WantLabels = BasisIKSolveGizmoStages.Labels.RawValue;
        }
        public static void Drain(ref BasisEerieMovement job, Vector3 cameraPosition)
        {
            EnsureMasterToggleHook();

            if (Trace)
            {
                TraceChain(ref job);
            }

            if (!job.gizmos.IsCreated || job.gizmos.StageMask == 0)
            {
                Hide();
                return;
            }

            NativeList<BasisIKGizmoDraw> draws = job.gizmos.Draws;
            int lines = 0;
            int spheres = 0;

            for (int i = 0; i < draws.Length; i++)
            {
                BasisIKGizmoDraw draw = draws[i];
                Color32 color = Unpack(draw.Color);
                if (draw.Kind == BasisIKGizmoKind.Line)
                {
                    BasisGizmoManager.UpdateLineGizmo(RentLine(lines++), draw.A, draw.B, draw.Size, color);
                }
                else
                {
                    BasisGizmoManager.UpdateSphereGizmo(RentSphere(spheres++), draw.A, draw.Size, color);
                }
            }

            HideTail(lineIds, lines, ref linesShown);
            HideTail(sphereIds, spheres, ref spheresShown);

            DrainLabels(ref job, cameraPosition);
            ReportOverflow(ref job);
        }
        static void DrainLabels(ref BasisEerieMovement job, Vector3 cameraPosition)
        {
            if (!job.gizmos.Labels.IsCreated || !BasisIKSolveGizmoStages.Labels.RawValue)
            {
                HideTail(labelIds, 0, ref labelsShown);
                return;
            }

            float labelScale = LabelScaleBase * WorldScale();
            NativeList<BasisIKGizmoLabel> labels = job.gizmos.Labels;
            for (int i = 0; i < labels.Length; i++)
            {
                BasisIKGizmoLabel label = labels[i];
                Color32 color = Unpack(label.Color);
                int id = RentLabel(i, label.Position, color);
                if (id <= 0)
                {
                    continue;
                }

                if (labelStrings[i] == null || !labelText[i].Equals(label.Text))
                {
                    labelText[i] = label.Text;
                    labelStrings[i] = label.Text.ToString();
                }

                Quaternion rotation = BasisGizmoManager.BillboardRotation(label.Position, cameraPosition);
                BasisGizmoManager.UpdateTextGizmo(id, label.Position, rotation, labelScale, labelStrings[i], color);
            }

            HideTail(labelIds, labels.Length, ref labelsShown);
        }
        static void ReportOverflow(ref BasisEerieMovement job)
        {
            if (overflowWarned || !job.gizmos.Overflow.IsCreated)
            {
                return;
            }
            int droppedDraws = job.gizmos.Overflow[BasisIKGizmoRecorder.OverflowDraws];
            int droppedLabels = job.gizmos.Overflow[BasisIKGizmoRecorder.OverflowLabels];
            if (droppedDraws == 0 && droppedLabels == 0)
            {
                return;
            }
            overflowWarned = true;
            BasisDebug.LogWarning( $"IK solve gizmos dropped {droppedDraws} draws and {droppedLabels} labels this frame " + $"(caps {BasisIKSolveGizmoStages.DrawCapacity}/{BasisIKSolveGizmoStages.LabelCapacity}). " + "The picture is incomplete; disable a stage or raise the caps.", BasisDebug.LogTag.Gizmo);
        }
        static float WorldScale()
        {
            float avatar = BasisHeightDriver.ScaledToMatchValue;
            if (!(avatar > 0f))
            {
                avatar = 1f;
            }
            float user = Mathf.Clamp(BasisIKSolveGizmoStages.Scale.RawValue, BasisIKSolveGizmoStages.ScaleMin, BasisIKSolveGizmoStages.ScaleMax);
            return avatar * user;
        }
        static Color32 Unpack(uint packed)
        {
            return new Color32( BasisIKGizmoPalette.R(packed), BasisIKGizmoPalette.G(packed), BasisIKGizmoPalette.B(packed), BasisIKGizmoPalette.A(packed));
        }
        static int RentLine(int index)
        {
            while (lineIds.Count <= index)
            {
                BasisGizmoManager.CreateLineGizmo($"IKSolve_Line{lineIds.Count}", out int created, Vector3.zero, Vector3.zero, LineWidthBase, Color.white);
                lineIds.Add(created);
            }
            if (index >= linesShown)
            {
                BasisGizmoManager.SetGizmoActive(lineIds[index], true);
                linesShown = index + 1;
            }
            return lineIds[index];
        }
        static int RentSphere(int index)
        {
            while (sphereIds.Count <= index)
            {
                BasisGizmoManager.CreateSphereGizmo($"IKSolve_Point{sphereIds.Count}", out int created, Vector3.zero, PointSizeBase, Color.white);
                sphereIds.Add(created);
            }
            if (index >= spheresShown)
            {
                BasisGizmoManager.SetGizmoActive(sphereIds[index], true);
                spheresShown = index + 1;
            }
            return sphereIds[index];
        }
        static int RentLabel(int index, Vector3 position, Color color)
        {
            while (labelIds.Count <= index)
            {
                if (!BasisGizmoManager.CreateTextGizmo($"IKSolve_Label{labelIds.Count}", out int created, position, string.Empty, color))
                {
                    return -1;
                }
                labelIds.Add(created);
                labelText.Add(default);
                labelStrings.Add(null);
            }
            if (index >= labelsShown)
            {
                BasisGizmoManager.SetGizmoActive(labelIds[index], true);
                labelsShown = index + 1;
            }
            return labelIds[index];
        }
        static void HideTail(List<int> ids, int used, ref int shown)
        {
            for (int i = used; i < shown && i < ids.Count; i++)
            {
                BasisGizmoManager.SetGizmoActive(ids[i], false);
            }
            shown = Mathf.Min(used, ids.Count);
        }
        public static void Hide()
        {
            HideTail(lineIds, 0, ref linesShown);
            HideTail(sphereIds, 0, ref spheresShown);
            HideTail(labelIds, 0, ref labelsShown);
        }
        public static void Shutdown()
        {
            for (int i = 0; i < lineIds.Count; i++)
            {
                BasisGizmoManager.DestroyGizmo(lineIds[i]);
            }
            for (int i = 0; i < sphereIds.Count; i++)
            {
                BasisGizmoManager.DestroyGizmo(sphereIds[i]);
            }
            for (int i = 0; i < labelIds.Count; i++)
            {
                BasisGizmoManager.DestroyGizmo(labelIds[i]);
            }
            ResetState();
        }
        /// <summary>
        /// One line per second naming which link of the gizmo chain is empty. The chain is
        /// mask -> recorder -> job records -> Drain renders -> BasisGizmoManager slots, and every
        /// link fails the same way from the outside: nothing on screen. This prints all of them.
        /// </summary>
        public static bool Trace => BasisIKSolveGizmoStages.Diagnose.RawValue;
        static float nextTrace;
        static void TraceChain(ref BasisEerieMovement job)
        {
            if (Time.realtimeSinceStartup < nextTrace)
            {
                return;
            }
            nextTrace = Time.realtimeSinceStartup + 1f;

            DrawSelfTestLine();

            int mask = BasisIKSolveGizmoStages.Mask();
            bool scratchOn = (mask & (int)BasisIKGizmoStage.Scratch) != 0;
            int recorded = job.gizmos.Draws.IsCreated ? job.gizmos.Draws.Length : -1;
            int capacity = job.gizmos.Draws.IsCreated ? job.gizmos.Draws.Capacity : -1;
            int scratchRecorded = 0;
            if (job.gizmos.Draws.IsCreated)
            {
                int scratchIndex = BasisIKGizmoRecorder.StageIndex(BasisIKGizmoStage.Scratch);
                for (int i = 0; i < job.gizmos.Draws.Length; i++)
                {
                    if (job.gizmos.Draws[i].Stage == scratchIndex)
                    {
                        scratchRecorded++;
                    }
                }
            }
            int dropped = job.gizmos.Overflow.IsCreated ? job.gizmos.Overflow[BasisIKGizmoRecorder.OverflowDraws] : -1;

            BasisDebug.Log(
                $"[IKSolveGizmos] enabled={BasisIKSolveGizmoStages.Enabled.RawValue} mask=0x{mask:X} scratchStage={scratchOn} "
                + $"| recorder created={job.gizmos.IsCreated} jobMask=0x{job.gizmos.StageMask:X} lineWidth={job.gizmos.LineWidth:F4} "
                + $"| recorded={recorded}/{capacity} (scratch={scratchRecorded}, dropped={dropped}) "
                + $"| rendered lines={linesShown} spheres={spheresShown} "
                + $"| manager maxDrawDistance={BasisGizmoManager.MaxDrawDistance}",
                BasisDebug.LogTag.Gizmo);
        }

        /// <summary>
        /// A line submitted straight to BasisGizmoManager, one metre in front of the camera, with
        /// none of the IK gizmo layer in the way -- no stage mask, no recorder, no job, no Drain.
        /// It bisects the problem in one look: if this magenta line is missing too, nothing above
        /// BasisGizmoManager is at fault and the answer is the manager, the camera or the layer. If
        /// it draws and the recorded ones do not, the break is in the IK gizmo path.
        /// </summary>
        public static bool TraceSelfTest => BasisIKSolveGizmoStages.DiagnoseSelfTest.RawValue;
        static int selfTestLine = -1;
        static void DrawSelfTestLine()
        {
            if (!TraceSelfTest)
            {
                if (selfTestLine > 0)
                {
                    BasisGizmoManager.SetGizmoActive(selfTestLine, false);
                }
                return;
            }
            Vector3 eye = BasisLocalCameraDriver.Position;
            Vector3 forward = BasisLocalCameraDriver.Instance != null
                ? BasisLocalCameraDriver.Instance.transform.forward
                : Vector3.forward;
            Vector3 from = eye + forward * 1f;
            Vector3 to = from + Vector3.up * 0.5f;
            if (selfTestLine <= 0)
            {
                BasisGizmoManager.CreateLineGizmo("IKSolve_SelfTest", out selfTestLine, from, to, LineWidthBase, Color.magenta);
            }
            BasisGizmoManager.SetGizmoActive(selfTestLine, true);
            BasisGizmoManager.UpdateLineGizmo(selfTestLine, from, to, LineWidthBase, (Color32)Color.magenta);
        }

        static void EnsureMasterToggleHook()
        {
            if (registered)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            registered = true;
        }
        static void OnMasterToggleChanged(bool state)
        {
            if (!state)
            {
                ResetState();
            }
        }
        static void ResetState()
        {
            lineIds.Clear();
            sphereIds.Clear();
            labelIds.Clear();
            labelText.Clear();
            labelStrings.Clear();
            linesShown = 0;
            spheresShown = 0;
            labelsShown = 0;
            overflowWarned = false;
        }
    }
}
