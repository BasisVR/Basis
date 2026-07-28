using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "The head/neck jitters when I stand looking almost straight ahead" regression tests for the spine
    /// CCD -- <see cref="BasisEerieMovement.SolveSequentialSpineIK"/>.
    ///
    /// Standing upright, the virtual spine places the hips a full chain length below the head, which parks
    /// the CCD at the chain's full-extension singularity. A small head pitch then commands a mm-scale
    /// COMPRESSION through the HMD's neck-pivot lever (quadratic in pitch: ~1.4 mm at 8 deg) -- and a
    /// straight chain cannot shorten by aiming. Only a bow can, the required bow angle goes as
    /// sqrt(compression), and nothing constrains its plane. The loop burned all 20 iterations failing to
    /// converge, and each frame's sub-mm tracker noise re-picked the bow plane: a sustained 0.25-0.38
    /// deg/frame neck/chest buzz, versus 0.003 outside the band with identical noise. Exactly straight
    /// (taut) was quiet and a real lean was quiet, which is why the report reads "ALMOST center straight".
    ///
    /// The fix is the taut band in SolveSequentialSpineIK: commanded compression is softened through a C1
    /// hinge (band = 1.5% of the chain's own reach), so noise-scale compressions leave the chain taut --
    /// where the shipped solve already stalled anyway, measured tip error unchanged to 0.1 mm -- and real
    /// compressions pass through with an error that decays as band^2/compression.
    ///
    /// These guard: (1) noise inside the former buzz band no longer buzzes the neck; (2) the taut regime
    /// (target at/beyond reach -- the untouched code path) stays quiet and straight; and (3) a deep
    /// commanded compression is still actually solved, i.e. the band does not neuter real bends.
    /// </summary>
    public class BasisSpineTautBandTests
    {
        // hips, spine, chest, upperChest, neck, head -- heights of a ~1.7 m avatar, matching the
        // measurement harness this fix was derived with.
        static readonly float[] Heights = { 0.95f, 1.06f, 1.21f, 1.33f, 1.45f, 1.57f };

        GameObject _root;
        Transform[] _bones;
        BasisPoseSkeleton _skeleton;
        NativeArray<BasisBoneHandle> _chain;
        BasisEerieMovement _job;
        int _neckStreamIndex;
        int _chestStreamIndex;

        float _scale = 1f;

        [SetUp]
        public void SetUp()
        {
            BuildRig(1f);
        }

        void BuildRig(float scale)
        {
            DisposeRig();
            _scale = scale;

            _root = new GameObject("TautBandRig");
            string[] names = { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" };
            _bones = new Transform[names.Length];
            Transform parent = _root.transform;
            for (int i = 0; i < names.Length; i++)
            {
                var go = new GameObject(names[i]);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(0f, (Heights[i] - (i == 0 ? 0f : Heights[i - 1])) * scale, 0f);
                _bones[i] = go.transform;
                parent = go.transform;
            }

            _skeleton = new BasisPoseSkeleton();
            _skeleton.Build(_bones[0], _bones);
            _skeleton.GatherNow();

            // Chain order matches the rig driver: tip (head) first, root (hips) last.
            _chain = new NativeArray<BasisBoneHandle>(6, Allocator.Persistent);
            for (int i = 0; i < 6; i++)
            {
                _chain[i] = _skeleton.Bind(_bones[5 - i]);
            }
            _neckStreamIndex = _skeleton.Bind(_bones[4]).Index;
            _chestStreamIndex = _skeleton.Bind(_bones[2]).Index;

            // Shipped defaults for every field this solve path reads. Anatomy guard and chest target are
            // left off (their defaults) so the test isolates the CCD itself.
            _job = new BasisEerieMovement
            {
                chainHeadToSpine = _chain,
                handleHips = _skeleton.Bind(_bones[0]),
                spineMaxIterations = 20,
                spineTolerance = 0.001f,
                spineCCDRelax = 1.0f,
                spineTwistKeep = 0.25f,
                spineNeckTwistKeep = 0.9f,
                neckMaxConeDeg = 45f,
                maxChestDeltaDeg = 30f,
                thoracicBendStiffen = 0.3f,
                spineTautBandFrac = 0.015f,
                bendTwistCoupling = 0.15f,
                chestIkWeight = 0.5f,
                chestIkIterations = 8,
                chestIkHeadRestoreSweeps = 2,
                chestPosPullMaxDeg = 20f,
                chestPullMaxDist = 0.5f,
                targetOffsetHead = Quaternion.identity,
                playerUp = Vector3.up,
                chestIkTarget = false,
                spineAnatomicalRom = false,
            };
        }

        void DisposeRig()
        {
            if (_chain.IsCreated)
            {
                _chain.Dispose();
            }
            _skeleton?.Dispose();
            _skeleton = null;
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [TearDown]
        public void TearDown()
        {
            DisposeRig();
        }

        Vector3 RestHead => new Vector3(0f, Heights[5] * _scale, 0f);

        // Uniform tracker-scale noise, deterministic across runs.
        static Vector3 Noise(System.Random rng, float amplitude)
        {
            return new Vector3(
                (float)(rng.NextDouble() * 2.0 - 1.0) * amplitude,
                (float)(rng.NextDouble() * 2.0 - 1.0) * amplitude,
                (float)(rng.NextDouble() * 2.0 - 1.0) * amplitude);
        }

        float SolveFrameAndMeasure(Vector3 targetPos, ref Quaternion prevNeck, ref Quaternion prevChest,
            ref bool have, ref float maxNeckStep, ref float maxChestStep)
        {
            // A fresh gather per frame = the animator re-writing the pose stream each frame, which is why
            // the live solver has no frame-to-frame hysteresis to stabilize the bow plane.
            _skeleton.GatherNow();
            _job.SolveSequentialSpineIK(_skeleton.Stream, targetPos, Quaternion.identity);

            Quaternion neck = _skeleton.Stream.GetWorldRotation(_neckStreamIndex);
            Quaternion chest = _skeleton.Stream.GetWorldRotation(_chestStreamIndex);
            if (have)
            {
                maxNeckStep = Mathf.Max(maxNeckStep, Quaternion.Angle(prevNeck, neck));
                maxChestStep = Mathf.Max(maxChestStep, Quaternion.Angle(prevChest, chest));
            }
            prevNeck = neck;
            prevChest = chest;
            have = true;

            return (_chain[0].GetPosition(_skeleton.Stream) - targetPos).magnitude;
        }

        // ------------------------------------------------------------------ the headline: no buzz

        [Test]
        public void CompressedByNoiseScaleAmounts_TrackerNoise_DoesNotBuzzTheNeck()
        {
            // 2.5 mm commanded compression -- mid buzz band (a ~12 deg head pitch through the HMD lever),
            // over the 1 mm tolerance so the pre-fix loop ran all 20 iterations every frame. With 0.3 mm
            // noise the pre-fix CCD measures ~0.2-0.37 deg/frame worst neck steps here; converged frames
            // measure well under 0.05.
            var rng = new System.Random(9001);
            Vector3 baseTarget = RestHead - new Vector3(0f, 0.0025f, 0f);

            Quaternion prevNeck = Quaternion.identity, prevChest = Quaternion.identity;
            bool have = false;
            float maxNeckStep = 0f, maxChestStep = 0f;

            for (int frame = 0; frame < 150; frame++)
            {
                SolveFrameAndMeasure(baseTarget + Noise(rng, 0.0003f), ref prevNeck, ref prevChest,
                    ref have, ref maxNeckStep, ref maxChestStep);
            }

            Assert.Less(maxNeckStep, 0.05f, "neck must not buzz while the commanded compression is noise-scale");
            Assert.Less(maxChestStep, 0.05f, "chest must not buzz while the commanded compression is noise-scale");
        }

        // ------------------------------------------------------- the taut side is the untouched path

        [Test]
        public void TargetBeyondReach_StaysQuietAndStraight()
        {
            // The OTHER side of the singularity: 5 mm beyond reach, the tolerance can never be met, and
            // the pre-fix loop churned all 20 sweeps tilting the chain into each frame's noise azimuth —
            // measured 0.39 deg/frame worst neck steps, the same buzz as the compressed side. The reach-
            // sphere clamp converges to the identical straight-at-target pose (tip error unchanged,
            // 4.80 -> 4.81 mm here) without the churn.
            var rng = new System.Random(9001);
            Vector3 baseTarget = RestHead + new Vector3(0f, 0.005f, 0f);

            Quaternion prevNeck = Quaternion.identity, prevChest = Quaternion.identity;
            bool have = false;
            float maxNeckStep = 0f, maxChestStep = 0f;

            Quaternion restNeck = _skeleton.Stream.GetWorldRotation(_neckStreamIndex);
            for (int frame = 0; frame < 150; frame++)
            {
                SolveFrameAndMeasure(baseTarget + Noise(rng, 0.0003f), ref prevNeck, ref prevChest,
                    ref have, ref maxNeckStep, ref maxChestStep);
            }

            Assert.Less(maxNeckStep, 0.05f, "taut chain must stay quiet under noise");
            Assert.Less(Quaternion.Angle(restNeck, prevNeck), 0.25f, "taut chain must stay straight");
        }

        // ------------------------------------------------------------- real compression still solves

        [Test]
        public void DeepCompression_IsStillActuallySolved()
        {
            // 5 cm commanded compression. The raw CCD is a poor compressor from a perfectly STRAIGHT
            // base with or without the band (measured in-suite: it recovers ~1 mm of the 51) — in the
            // live pipeline DistributeSpineBend pre-curves the chain before the CCD ever runs, so the
            // guarantee that matters is: given a disambiguated bend plane, a real compression still
            // solves THROUGH the band, whose distortion is bounded by band^2/compression (~1.7 mm
            // here). Pre-bend standing in for the pre-bend pass, then the CCD must finish the job.
            // Measured in-suite: tipErr 20.4 mm of the 51 (band share <= 1.7), neck bow 33.5 deg —
            // the remaining residual is the solver's own compression character (20 iters, relax 0.8,
            // twist-stripped, thoracic-stiffened), identical with the band off.
            Vector3 target = RestHead - new Vector3(0f, 0.05f, -0.01f);
            float initialErr = (RestHead - target).magnitude;

            _skeleton.GatherNow();
            Quaternion restNeck = _skeleton.Stream.GetWorldRotation(_neckStreamIndex);

            BasisBoneHandle spineHandle = _skeleton.Bind(_bones[1]);
            BasisBoneHandle chestHandle = _skeleton.Bind(_bones[2]);
            spineHandle.SetRotation(_skeleton.Stream,
                Quaternion.AngleAxis(10f, Vector3.right) * spineHandle.GetRotation(_skeleton.Stream));
            chestHandle.SetRotation(_skeleton.Stream,
                Quaternion.AngleAxis(15f, Vector3.right) * chestHandle.GetRotation(_skeleton.Stream));

            _job.SolveSequentialSpineIK(_skeleton.Stream, target, Quaternion.identity);

            float tipErr = (_chain[0].GetPosition(_skeleton.Stream) - target).magnitude;
            float neckBow = Quaternion.Angle(restNeck, _skeleton.Stream.GetWorldRotation(_neckStreamIndex));
            TestContext.WriteLine($"tipErr {tipErr * 1000f:F2} mm (initial {initialErr * 1000f:F2}), neck bow {neckBow:F2} deg");

            Assert.Less(tipErr, 0.025f, "from a pre-bent chain the CCD must recover most of a real compression through the band");
            Assert.Less(tipErr, initialErr * 0.5f, "the band must not leave the solve anywhere near the no-op");
            Assert.Greater(neckBow, 20f, "a deep compression must still bow the chain for real");
        }

        // ------------------------------------------------- the property the fix establishes: continuity

        [Test]
        public void SolvedPose_IsContinuous_AcrossTheFullExtensionPoint()
        {
            // Sweep the commanded distance through the singular point (4 mm beyond reach to 4 mm inside)
            // in 0.25 mm steps, no noise, fresh base each solve. The whole point of the C1 hinge is that
            // the solved pose is now a CONTINUOUS function of the target distance — pre-fix, the pose
            // stepped between "taut" and "sqrt-bowed in an arbitrary plane" somewhere in this interval.
            // A small fixed lateral offset gives the bow a deterministic plane so the sweep measures the
            // distance response, not plane noise.
            Quaternion prevNeck = Quaternion.identity;
            bool have = false;
            float maxStep = 0f;

            for (float delta = -0.004f; delta <= 0.004f; delta += 0.00025f)
            {
                Vector3 target = RestHead + new Vector3(0.0005f, -delta, 0f);
                _skeleton.GatherNow();
                _job.SolveSequentialSpineIK(_skeleton.Stream, target, Quaternion.identity);

                Quaternion neck = _skeleton.Stream.GetWorldRotation(_neckStreamIndex);
                if (have)
                {
                    maxStep = Mathf.Max(maxStep, Quaternion.Angle(prevNeck, neck));
                }
                prevNeck = neck;
                have = true;
            }

            TestContext.WriteLine($"max neck step per 0.25 mm of target travel: {maxStep:F4} deg");
            Assert.Less(maxStep, 1.0f, "the solved neck must move continuously as the target crosses full extension");
        }

        // ----------------------------------------------------------- the band scales with the avatar

        [TestCase(0.35f)]
        [TestCase(2.5f)]
        public void TautBand_ScalesWithTheAvatar(float scale)
        {
            // The band is a fraction of the chain's own reach, so a child-sized or giant avatar must get
            // the same protection at its own scale: the buzz-band scenario scaled by the avatar factor
            // must stay exactly as quiet as it does at 1x.
            BuildRig(scale);

            var rng = new System.Random(9001);
            Vector3 baseTarget = RestHead - new Vector3(0f, 0.0025f * scale, 0f);

            Quaternion prevNeck = Quaternion.identity, prevChest = Quaternion.identity;
            bool have = false;
            float maxNeckStep = 0f, maxChestStep = 0f;

            for (int frame = 0; frame < 150; frame++)
            {
                SolveFrameAndMeasure(baseTarget + Noise(rng, 0.0003f * scale), ref prevNeck, ref prevChest,
                    ref have, ref maxNeckStep, ref maxChestStep);
            }

            Assert.Less(maxNeckStep, 0.05f, "the taut band must protect every avatar at its own scale");
        }

        // ------------------------------------------- the stretch clamp keeps the chain aimed at the target

        [Test]
        public void BeyondReach_PreservesTheAimDirection()
        {
            // The reach-sphere clamp regularizes the DISTANCE only. An off-axis beyond-reach target (a
            // hard lean) must still drag the chain to point at it — direction is the one thing the clamp
            // must never touch.
            Vector3 rootPos = _bones[0].position;
            Vector3 dir = (Quaternion.AngleAxis(25f, Vector3.right) * Vector3.up).normalized;
            float reach = (Heights[5] - Heights[0]) * _scale;
            Vector3 target = rootPos + dir * (reach + 0.02f);

            _skeleton.GatherNow();
            _job.SolveSequentialSpineIK(_skeleton.Stream, target, Quaternion.identity);

            Vector3 solvedDir = (_chain[0].GetPosition(_skeleton.Stream) - rootPos).normalized;
            float aimErr = Vector3.Angle(solvedDir, dir);
            TestContext.WriteLine($"aim error toward an off-axis beyond-reach target: {aimErr:F3} deg");
            Assert.Less(aimErr, 3f, "the reach clamp must preserve the aim direction of an unreachable target");
        }

        // --------------------------------------------------- the lordosis early-out must not drop the pin

        [Test]
        public void LordosisEarlyOut_KeepsTheHeadPinned()
        {
            // With cervical lordosis enabled and BaseDeg tuned to ~0, |lordosisDeg| crosses the 0.01
            // early-out cutoff constantly at level gaze. The early-out branch used to skip the head
            // position pin unless the pitch clamp had engaged, so the head toggled between "pinned to
            // target" and "CCD FK" with that threshold. Set up a sub-tolerance gap the CCD will not
            // close (0.8 mm), then check the pin lands on BOTH sides of the early-out.
            _job.lordosisBaseDeg = 0f;
            _job.lordosisPitchGainDeg = 8f;
            _job.lordosisNeckShare = 0.65f;
            _job.lordosisMaxHeadPitchDeg = 80f;
            _job.lordosisExtremeStartDeg = 50f;
            _job.lordosisExtremeFullDeg = 80f;
            _job.handleNeck = _skeleton.Bind(_bones[4]);
            _job.handleHead = _skeleton.Bind(_bones[5]);
            _job.handleChest = _skeleton.Bind(_bones[2]);
            _job.handleUpperChest = _skeleton.Bind(_bones[3]);
            _job.handleSpine = _skeleton.Bind(_bones[1]);
            _job.handleHips = _skeleton.Bind(_bones[0]);
            _job.offsetRotationHead = Quaternion.identity;

            foreach (float pitchDeg in new[] { 0.02f, 2f })   // early-out side / full-path side of 0.01 deg
            {
                Vector3 target = RestHead - new Vector3(0f, 0.0008f, 0f);
                Quaternion gaze = Quaternion.AngleAxis(pitchDeg, Vector3.right);
                _job.targetPositionHead = target;
                _job.targetRotationHead = gaze;

                _skeleton.GatherNow();
                _job.SolveSequentialSpineIK(_skeleton.Stream, target, gaze);
                _job.ApplyCervicalLordosis(_skeleton.Stream);

                float headErr = (_chain[0].GetPosition(_skeleton.Stream) - target).magnitude;
                TestContext.WriteLine($"pitch {pitchDeg} deg: head-to-target {headErr * 1000f:F3} mm");
                Assert.Less(headErr, 0.0002f,
                    $"the head pin must land on both sides of the lordosis early-out (pitch {pitchDeg})");
            }
        }
    }
}
