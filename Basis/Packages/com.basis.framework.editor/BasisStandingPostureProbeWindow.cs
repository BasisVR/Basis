using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Device_Management.Devices.Desktop;
using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Basis ▸ Debug ▸ IK ▸ Standing Posture Probe.
    //
    // Answers "why is the body standing where it is standing" with numbers instead of eyeballs: every stage
    // of the standing chain -- device eye, composed head, virtual-spine hips, FBIK hips target, final bones,
    // planted feet -- read live and projected onto the body's forward axis in PLAYSPACE-LOCAL space, where
    // the character capsule column is exactly (0, 0). A positive "fwd" is in front of the capsule.
    //
    // Built for the "hips are in front of the player" family of reports (third of its kind): the chain has
    // half a dozen candidate terms and they compose sign-by-sign, so the only reliable diagnosis is reading
    // each link in the running scene. Enter Play, stand still and level, open this, Copy Report.
    public class BasisStandingPostureProbeWindow : EditorWindow
    {
        Vector2 _scroll;
        string _lastReport;

        [MenuItem("Basis/Debug/IK/Standing Posture Probe")]
        public static void ShowWindow()
        {
            var w = GetWindow<BasisStandingPostureProbeWindow>("Standing Posture Probe");
            w.minSize = new Vector2(430, 460);
        }

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            if (!Application.isPlaying || BasisLocalPlayer.Instance == null)
            {
                EditorGUILayout.HelpBox("Enter Play with a local player, stand still and level, then read.", MessageType.Info);
                return;
            }

            var sb = new StringBuilder(2048);
            BuildReport(sb);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(sb.ToString(), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Copy Report", GUILayout.Height(28)))
            {
                _lastReport = sb.ToString();
                EditorGUIUtility.systemCopyBuffer = _lastReport;
                Debug.Log("[StandingPostureProbe]\n" + _lastReport);
            }
        }

        static void BuildReport(StringBuilder sb)
        {
            Matrix4x4 worldToLocal = BasisLocalPlayer.localToWorldMatrix.inverse;

            var eye = BasisLocalBoneDriver.EyeControl;
            var head = BasisLocalBoneDriver.HeadControl;
            var neck = BasisLocalBoneDriver.NeckControl;
            var hips = BasisLocalBoneDriver.HipsControl;
            var lf = BasisLocalBoneDriver.LeftFootControl;
            var rf = BasisLocalBoneDriver.RightFootControl;
            if (eye == null || head == null || hips == null)
            {
                sb.AppendLine("bone controls not ready");
                return;
            }

            // Forward axis from the hips yaw, in local space; the capsule column is local (0,0).
            Vector3 fwd = hips.OutGoingData.rotation * Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;

            void Row(string name, Vector3 localPos)
            {
                float f = Vector3.Dot(new Vector3(localPos.x, 0f, localPos.z), fwd) * 100f;
                float lat = Vector3.Dot(new Vector3(localPos.x, 0f, localPos.z), new Vector3(fwd.z, 0f, -fwd.x)) * 100f;
                sb.AppendLine($"{name,-26} fwd {f,+7:F1} cm   lat {lat,+6:F1} cm   y {localPos.y,5:F2}");
            }

            sb.AppendLine("== live bones (playspace-local; capsule column = 0,0; +fwd = in front) ==");
            Row("eye control", eye.OutGoingData.position);
            Row("head control", head.OutGoingData.position);
            if (neck != null) Row("neck control", neck.OutGoingData.position);
            Row("hips control", hips.OutGoingData.position);
            if (lf != null) Row("left foot", lf.OutGoingData.position);
            if (rf != null) Row("right foot", rf.OutGoingData.position);
            if (lf != null && rf != null) Row("feet midpoint", 0.5f * (lf.OutGoingData.position + rf.OutGoingData.position));

            sb.AppendLine();
            sb.AppendLine("== T-pose (scaled) references ==");
            Row("tpose eye", eye.TposeLocalScaled.position);
            Row("tpose head", head.TposeLocalScaled.position);
            Row("tpose hips", hips.TposeLocalScaled.position);
            if (lf != null) Row("tpose left foot", lf.TposeLocalScaled.position);
            Vector3 dzOff = head.TposeLocalScaled.position - eye.TposeLocalScaled.position;
            sb.AppendLine($"head-minus-eye tpose offset  x {dzOff.x:+0.000} y {dzOff.y:+0.000} z {dzOff.z:+0.000}  (head.ScaledOffset z {head.ScaledOffset.z:+0.000})");

            // THE quantity the no-hips-tracker anchor actually consumes: BasisLocalVirtualSpineDriver's
            // _hipsFromEyeTposeXZ, hung off the leashed eye as desiredHipsXZ += rotate(torsoYaw, this).
            // Printed derived rather than left to be subtracted by hand, because its SIGN is the whole
            // diagnosis: z must be NEGATIVE (pelvis behind the viewpoint) by roughly the eye-forward
            // distance. Near zero means AvatarEyePosition is authored at the head bone rather than at the
            // eyes, and the pelvis then stands under the HMD -- which reads as "hips too far forward".
            Vector3 hipsArm = hips.TposeLocalScaled.position - eye.TposeLocalScaled.position;
            sb.AppendLine($"hips-minus-eye tpose offset  x {hipsArm.x:+0.000} z {hipsArm.z:+0.000}  <- the anchor arm (z should be negative)");
            Vector3 hipsFromFeet = lf != null && rf != null
                ? hips.TposeLocalScaled.position - 0.5f * (lf.TposeLocalScaled.position + rf.TposeLocalScaled.position)
                : Vector3.zero;
            sb.AppendLine($"hips-minus-feet tpose offset  x {hipsFromFeet.x:+0.000} z {hipsFromFeet.z:+0.000}  <- how far forward THIS avatar authors its own pelvis");

            var rig = BasisLocalPlayer.Instance.LocalRigDriver;
            if (rig != null && rig.IKDataReady)
            {
                sb.AppendLine();
                sb.AppendLine("== FBIK job (world -> local) ==");
                Row("IK head target", worldToLocal.MultiplyPoint3x4(rig.IKJob.targetPositionHead));
                Row("IK hips target", worldToLocal.MultiplyPoint3x4(rig.IKJob.targetPositionHips));
                sb.AppendLine($"crouchDepth {rig.IKJob.crouchDepth:F3} m   standingHeadHeight {rig.IKJob.standingHeadHeight:F3} m   " +
                              $"hipsTracker {rig.IKJob.hasHipsTracker} chestTracker {rig.IKJob.hasChestTracker}");
            }

            var de = BasisDesktopEye.Instance;
            if (de != null)
            {
                sb.AppendLine();
                sb.AppendLine("== desktop eye ==");
                sb.AppendLine($"pin X {de.X:F3}  Z {de.Z:F3}   pitch {de.rotationPitch:F1} deg   " +
                              $"crouchBlend {BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchBlend:F2}   " +
                              $"headSwing {Basis.BasisUI.BasisSettingsDefaults.DesktopHeadSwingEnabled.RawValue}");
            }

            // POST-IK: what actually renders. Everything above is the FBIK job's INPUT side (the job runs
            // on a struct copy, so rig.IKJob.targetPositionHips never reflects the solve; OutGoingData is
            // the virtual-spine output). The solved pose lives in the avatar's bones.
            var animator = BasisLocalPlayer.Instance.BasisAvatar != null ? BasisLocalPlayer.Instance.BasisAvatar.Animator : null;
            Vector3 bHips = default, bFeetMid = default;
            bool haveBones = false;
            if (animator != null)
            {
                Transform tHips = animator.GetBoneTransform(HumanBodyBones.Hips);
                Transform tHead = animator.GetBoneTransform(HumanBodyBones.Head);
                Transform tLf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform tRf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                if (tHips != null && tHead != null && tLf != null && tRf != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("== POST-IK rendered bones (the pose you actually see) ==");
                    bHips = worldToLocal.MultiplyPoint3x4(tHips.position);
                    Vector3 bHead = worldToLocal.MultiplyPoint3x4(tHead.position);
                    Vector3 bLf = worldToLocal.MultiplyPoint3x4(tLf.position);
                    Vector3 bRf = worldToLocal.MultiplyPoint3x4(tRf.position);
                    bFeetMid = 0.5f * (bLf + bRf);
                    haveBones = true;
                    Row("bone head", bHead);
                    Row("bone hips", bHips);
                    Row("bone left foot", bLf);
                    Row("bone right foot", bRf);
                    Transform tKnee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                    Transform tKneeR = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                    if (tKnee != null) Row("bone left knee", worldToLocal.MultiplyPoint3x4(tKnee.position));
                    if (tKneeR != null) Row("bone right knee", worldToLocal.MultiplyPoint3x4(tKneeR.position));
                }
            }

            // The verdict material: where does forward travel enter the chain?
            sb.AppendLine();
            sb.AppendLine("== stage deltas (fwd cm; the biggest positive jump names the culprit) ==");
            float F(Vector3 p) => Vector3.Dot(new Vector3(p.x, 0f, p.z), fwd) * 100f;
            float eyeF = F(eye.OutGoingData.position);
            float headF = F(head.OutGoingData.position);
            float hipsF = F(hips.OutGoingData.position);
            sb.AppendLine($"device eye vs capsule   {eyeF,+7:F1}");
            sb.AppendLine($"head vs eye             {headF - eyeF,+7:F1}");
            sb.AppendLine($"hips vs head            {hipsF - headF,+7:F1}");
            if (lf != null && rf != null)
            {
                float feetF = F(0.5f * (lf.OutGoingData.position + rf.OutGoingData.position));
                sb.AppendLine($"hips vs feet midpoint   {hipsF - feetF,+7:F1}   (IK input side)");
            }
            if (haveBones)
            {
                sb.AppendLine($"RENDERED hips vs IK-in  {F(bHips) - hipsF,+7:F1}   <- what FBIK added (sit-back shows here when crouched)");
                sb.AppendLine($"RENDERED hips vs feet   {F(bHips) - F(bFeetMid),+7:F1}   <- what a viewer reads as 'hips in front'");
            }
        }
    }
}
