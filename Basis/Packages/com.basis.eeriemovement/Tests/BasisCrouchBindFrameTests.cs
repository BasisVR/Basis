using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisCrouchBindFrameTests
    {
        // Same spread of hips BIND conventions as the spine bind-frame suite.
        static readonly Quaternion[] Binds =
        {
            Quaternion.identity,
            Quaternion.AngleAxis(90f, Vector3.forward),
            Quaternion.AngleAxis(-90f, Vector3.right),   // Blender export -- collapsed the slide entirely pre-fix
            Quaternion.Euler(20f, -35f, 110f),
        };
        const float StandH = 1.60f, Rest = 0.55f;
        const float Depth = 0.40f; // dhat 0.25: mid squat, well past the deadzone, below the lean cap
        // Anatomical scene: hips face forward (hipsAnat = identity, so HipsRot = the bind itself), head dropped
        // Depth below standing, hips arriving as the LockHead stage leaves them (Rest straight below the head).
        // Anatomical forward is +Z, so the hips must slide toward −Z and onto the Rest sphere.
        static BasisCrouchOffsetResult SolveCrouch(Quaternion bind)
        {
            Vector3 head = new Vector3(0f, StandH - Depth, 0f);
            BasisCrouchOffsetInput i;
            i.HeadTargetPos = head;
            i.HipsPos = head - Vector3.up * Rest;
            i.HipsRot = Quaternion.identity * bind;
            i.Bind = bind;
            i.PlayerUp = Vector3.up;
            i.Factor = 1f;
            i.RestDist = Rest;
            i.CrouchDepth = Depth;
            i.StandingHeadHeight = StandH;
            i.Fade = 1f;
            BasisCrouchOffsetCore.Solve(i, out BasisCrouchOffsetResult r);
            return r;
        }
        [Test]
        public void CrouchSlide_IsInvariant_ToTheHipsBindConvention([ValueSource(nameof(Binds))] Quaternion bind)
        {
            var reference = SolveCrouch(Quaternion.identity);
            Assert.That(reference.Applied, Is.True, "reference crouch did not fire -- test would be vacuous.");

            var r = SolveCrouch(bind);
            Assert.That(r.Applied, Is.True, $"crouch did not fire on bind {bind} (the Blender-rig collapse).");
            Assert.That((r.HipsPos - reference.HipsPos).magnitude, Is.LessThan(1e-4f), $"crouch placed the hips differently on bind {bind}: {r.HipsPos} vs {reference.HipsPos}.");

            // The slide is anatomically backward (−Z) with no sideways leak.
            Vector3 head = new Vector3(0f, StandH - Depth, 0f), fromHead = r.HipsPos - head;
            Assert.That(fromHead.z, Is.LessThan(-1e-3f), $"crouch did not slide backward on bind {bind} ({fromHead}).");
            Assert.That(Mathf.Abs(fromHead.x), Is.LessThan(1e-4f), $"crouch slid sideways on bind {bind} ({fromHead}).");

            // And the hips landed on the rest-length sphere -- the spine neither compresses nor stretches,
            // the lean IS the sit-back (measured: real 3D head-hips distance stays ~0.95-1.0 of standing).
            Assert.That(Mathf.Abs(fromHead.magnitude - Rest), Is.LessThan(1e-4f), $"hips left the rest sphere on bind {bind}: |head->hips| {fromHead.magnitude} vs {Rest}.");
        }
        [Test]
        public void DegenerateBind_FallsBackToHipsRotForward_Unchanged()
        {
            // The sweep and the equivariance test leave Bind at its default (zero quaternion). That must mean
            // "HipsRot is already anatomical" -- the exact pre-fix behaviour -- not a divide-by-a-zero-quat.
            Quaternion hipsRot = Quaternion.Euler(0f, 25f, 0f);
            Vector3 head = new Vector3(0f, StandH - Depth, 0f);
            BasisCrouchOffsetInput i;
            i.HeadTargetPos = head;
            i.HipsPos = head - Vector3.up * Rest;
            i.HipsRot = hipsRot;
            i.Bind = default;                    // zero quaternion
            i.PlayerUp = Vector3.up;
            i.Factor = 1f;
            i.RestDist = Rest;
            i.CrouchDepth = Depth;
            i.StandingHeadHeight = StandH;
            i.Fade = 1f;
            BasisCrouchOffsetCore.Solve(i, out BasisCrouchOffsetResult r);

            float s = BasisCrouchOffsetCore.EvaluateSetback(Depth, StandH, 1f, 1f, Rest);
            Vector3 fwd = hipsRot * Vector3.forward;
            fwd -= Vector3.up * Vector3.Dot(fwd, Vector3.up);
            Vector3 horizontal = -fwd.normalized * s;
            Vector3 expected = head + horizontal - Vector3.up * Mathf.Sqrt(Rest * Rest - s * s);
            Assert.That(r.Applied, Is.True);
            Assert.That((r.HipsPos - expected).magnitude, Is.LessThan(1e-4f),"degenerate bind did not reproduce the raw HipsRot-forward slide.");
        }
    }
}
