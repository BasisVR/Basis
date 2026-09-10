using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // that exist) with default configs, writing each CSV to persistentDataPath, then evaluates the
    // quality gates in BasisIKTestGates -- a one-click regression test over all the IK math. PASS/FAIL
    // reflects the gate thresholds, not just whether the sweep ran. Each run is isolated.
    public class BasisIKSweepRunnerPage : BasisIKSweepPage
    {
        public override string Group => "Run All";
        public override string Title => "Run All Sweeps";
        public override int Order => 10;
        public override string Description =>
            "Runs every IK sweep (grid) and the available trajectory scans with default configs, " +
            "writing each CSV to persistentDataPath, then checks the BasisIKTestGates quality gates. " +
            "PASS/FAIL = the IK math passed its thresholds (not just 'it ran'). Tune thresholds in BasisIKTestGates.";

        struct Row { public string Name; public bool Ok; public string Detail; public string Path; }

        readonly List<Row> _rows = new List<Row>();
        bool _hasRun;
        bool _includeTraj = true;
        float _trajNoise = 0.003f;
        [System.NonSerialized] int _armGridSteps = 75;   // per-axis reach-target density for the arm grid sweep. NonSerialized so this code default wins on every recompile (Unity otherwise persists the open window's slider value).
        [System.NonSerialized] float _density = 1f;      // global multiplier on every sweep's grid/case resolution (1 = each sweep's own default). NonSerialized so the code default wins on recompile.
        Vector2 _scroll;

        // Scale a sweep's resolution by the global density multiplier. Per-axis (1D step counts, Cases),
        // so a 3D grid grows by ~density^3 and a 2D grid by ~density^2 -- the per-sweep readouts show it.
        static int Sc(int n, float m) => Mathf.Max(1, Mathf.RoundToInt(n * m));
        static Vector3Int Sc(Vector3Int s, float m) => new Vector3Int(Sc(s.x, m), Sc(s.y, m), Sc(s.z, m));

        public override void Draw()
        {
            _includeTraj = EditorGUILayout.Toggle("Include Trajectory Scans", _includeTraj);
            using (new EditorGUI.DisabledScope(!_includeTraj))
            {
                _trajNoise = EditorGUILayout.Slider("Trajectory Noise (m)", _trajNoise, 0f, 0.01f);
            }

            _density = EditorGUILayout.Slider("Validation Density ×", _density, 1f, 4f);
            BasisEditorUI.Note($"    scales EVERY sweep's grid/case resolution ×{_density:0.00} (per dimension: ~×{_density * _density:0.0} on 2D grids, ~×{_density * _density * _density:0.0} on the 3D arm/leg/elbow grids). Higher = more thorough, slower.");

            _armGridSteps = EditorGUILayout.IntSlider("Arm Grid Steps (per axis, base)", _armGridSteps, 9, 99);
            int armEff = Sc(_armGridSteps, _density);
            long armPts = (long)armEff * armEff * armEff;
            EditorGUILayout.LabelField($"    arm grid = {armPts:n0} targets ({armEff}^3 = {_armGridSteps}×{_density:0.0}); the densest sweep, sets the runtime", EditorStyles.miniLabel);

            if (BasisEditorUI.PrimaryButton("Run All IK Tests", 32f))
            {
                RunAll();
            }

            EditorGUILayout.HelpBox(
                "Live jitter capture: ENTER PLAY, click below, then move/hold the arm for ~10s. Writes the " +
                "solved shoulder/elbow/hand + IK inputs each frame to persistentDataPath/ArmIKRuntime so we " +
                "can see which one jitters (shoulder vs filtered target/hint vs the elbow itself).",
                MessageType.None);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Capture Live Arm Jitter (10s)"))
                {
                    Basis.Scripts.Drivers.BasisArmIKRuntimeRecorder.RequestCapture(900);
                }
            }

            if (_hasRun)
            {
                EditorGUILayout.Space();
                int ok = 0;
                for (int i = 0; i < _rows.Count; i++) if (_rows[i].Ok) ok++;
                BasisEditorUI.SectionTitle($"Results: {ok}/{_rows.Count} passed");

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var r in _rows)
                {
                    EditorGUILayout.BeginHorizontal();
                    var prev = GUI.color;
                    GUI.color = r.Ok ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
                    EditorGUILayout.LabelField(r.Ok ? "PASS" : "FAIL", GUILayout.Width(42));
                    GUI.color = prev;
                    EditorGUILayout.LabelField(r.Name, GUILayout.Width(150));
                    EditorGUILayout.LabelField(r.Detail);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(r.Path)))
                    {
                        if (GUILayout.Button("Reveal", GUILayout.Width(60)) && !string.IsNullOrEmpty(r.Path))
                        {
                            EditorUtility.RevealInFinder(r.Path);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();

                if (BasisEditorUI.SecondaryButton("Reveal Output Folder"))
                {
                    EditorUtility.RevealInFinder(Application.persistentDataPath);
                }
            }
        }

        void RunAll()
        {
            _rows.Clear();
            _hasRun = true;

            // Capture everything that needs the main thread up front: Application.persistentDataPath (the
            // paths) and the window fields. The sweeps themselves are pure math + per-file CSV IO and share
            // no mutable state, so each runs on a worker thread and they all go in parallel. We block until
            // they finish (the editor is busy during the run, same as before -- just shorter), then log in
            // submission order so the console/list read the same as the serial version.
            int armSteps = _armGridSteps;
            float density = _density;
            float trajNoise = _trajNoise;
            bool includeTraj = _includeTraj;

            string legPath = BasisLegIKSweep.DefaultPath();
            string legStancePath = TrajPath("BasisLegStraightStance.csv");
            string legInvPath = BasisLegInversionSweep.DefaultPath();
            string trackerPlacementPath = BasisTrackerPlacementSweep.DefaultPath();
            string multiTrackerRotPath = BasisMultiTrackerRotationSweep.DefaultPath();
            string multiTrackerTemporalPath = BasisMultiTrackerRotationSweep.DefaultTemporalPath();
            string calibMathPath = BasisCalibrationMathSweep.DefaultPath();
            string calibLockInPath = BasisCalibrationLockInSweep.DefaultPath();
            string legTwistPath = BasisLegTwistSweep.DefaultPath();
            string bendNormalPath = BasisBendNormalSweep.DefaultPath();
            string twistPath = BasisTwistSweep.DefaultPath();
            string spinePath = BasisSpineSweep.DefaultPath();
            string remoteBonePath = BasisRemoteBoneSweep.DefaultPath();
            string spineClampPath = BasisSpineClampSweep.DefaultPath();
            string hipHingePath = BasisHipHingeSweep.DefaultPath();
            string chestSpringPath = BasisChestSpringSweep.DefaultPath();
            string crouchPath = BasisCrouchOffsetSweep.DefaultPath();
            string spineCompPath = BasisSpineCompressionSweep.DefaultPath();
            string spineBendPath = BasisSpineBendSweep.DefaultPath();
            string spineTwistPath = BasisSpineTwistSweep.DefaultPath();
            string swivelFilterPath = BasisSwivelFilterSweep.DefaultPath();
            string oneEuroPath = BasisOneEuroSweep.DefaultPath();
            string eyePath = BasisEyeSweep.DefaultPath();
            string blinkPath = BasisBlinkTimingSweep.DefaultPath();
            string locomotionPath = BasisLocomotionSweep.DefaultPath();
            string boneSimPath = BasisBoneSimStabilitySweep.DefaultPath();
            string spineTemporalPath = BasisVirtualSpineTemporalSweep.DefaultPath();
            string footPath = BasisFootIKSweep.DefaultPath();
            string headPath = BasisHeadSweep.DefaultPath();
            string legTrajPath = TrajPath("BasisLegIKTrajectory.csv");
            string legTempPath = TrajPath("BasisLegIKTemporal.csv");
            string legRoundTripPath = TrajPath("BasisLegIKPoleRoundTrip.csv");
            string legStanceFlickerPath = TrajPath("BasisLegStraightStanceTemporal.csv");
            string legInvTempPath = TrajPath("BasisLegInversionTemporal.csv");
            string legCrouchLPath = TrajPath("BasisLegCrouch_L.csv");
            string legCrouchRPath = TrajPath("BasisLegCrouch_R.csv");
            string headTrajPath = TrajPath("BasisHeadTrajectory.csv");

            var jobs = new System.Collections.Generic.List<System.Func<Row[]>>();

            // Side-specific grid sweeps run for BOTH sides (RIGHT then LEFT, mirrored) so a left/right
            // asymmetry trips a gate. Each side writes its OWN csv (_L/_R) -- concurrent writes to one
            // file would corrupt it. Head has no side. The jobs all fan out in parallel below.
            foreach (bool isLeft in new[] { false, true })
            {
                bool L = isLeft;
                string side = L ? "L" : "R";
                string lp = SidePath(legPath, L);
                string lss = SidePath(legStancePath, L);

                jobs.Add(() => Job($"Leg IK ({side})", lp, () => { var cfg = BasisLegIKSweepConfig.Default(); cfg.IsLeft = L; cfg.Steps = Sc(cfg.Steps, density); var s = BasisLegIKSweep.Run(cfg, lp); return BasisIKTestGates.GateLeg(s); }));
                jobs.Add(() => Job($"Leg IK · straight stance ({side})", lss, () => { var cfg = BasisLegIKSweepConfig.Default(); cfg.IsLeft = L; var s = BasisLegIKSweep.RunStraightStance(cfg, lss); return BasisIKTestGates.GateLegStraightStance(s); }));
            }
            // Head and Leg Inversion have no per-side config (symmetric) -- run once.
            jobs.Add(() => Job("Head", headPath, () => { var c = BasisHeadSweepConfig.Default(); c.PitchSteps = Sc(c.PitchSteps, density); var s = BasisHeadSweep.Run(c, headPath); return BasisIKTestGates.GateHead(s); }));
            jobs.Add(() => Job("Leg Inversion", legInvPath, () => { var cfg = BasisLegInversionConfig.Default(); cfg.SafeConeDeg = BasisIKTestGates.LegInvertHintSafeConeDeg; cfg.HintAzSteps = Sc(cfg.HintAzSteps, density); cfg.HintElSteps = Sc(cfg.HintElSteps, density); var s = BasisLegInversionSweep.Run(cfg, legInvPath); return BasisIKTestGates.GateLegInversion(s); }));
            jobs.Add(() => Job("Tracker Placement", trackerPlacementPath, () => { var s = BasisTrackerPlacementSweep.Run(BasisTrackerPlacementSweepConfig.Default(), trackerPlacementPath); return BasisIKTestGates.GateTrackerPlacement(s); }));
            jobs.Add(() => Job("Multi-Tracker Rotation", multiTrackerRotPath, () => { var c = BasisMultiTrackerRotationConfig.Default(); c.YawSteps = Sc(c.YawSteps, density); c.PitchSteps = Sc(c.PitchSteps, density); c.RollSteps = Sc(c.RollSteps, density); var s = BasisMultiTrackerRotationSweep.Run(c, multiTrackerRotPath); return BasisIKTestGates.GateMultiTrackerRotation(s); }));
            jobs.Add(() => Job("Multi-Tracker Rotation · temporal", multiTrackerTemporalPath, () => { var s = BasisMultiTrackerRotationSweep.RunTemporal(BasisMultiTrackerRotationConfig.Default(), Basis.Scripts.Device_Management.Devices.Pairing.BasisMidpointFusionTunables.Default(), 1f / 90f, 0f, multiTrackerTemporalPath); return BasisIKTestGates.GateMultiTrackerRotationTemporal(s); }));
            jobs.Add(() => Job("Calibration Math", calibMathPath, () => { var c = BasisCalibrationMathSweepConfig.Default(); c.CasesPerSection = Sc(c.CasesPerSection, density); var s = BasisCalibrationMathSweep.Run(c, calibMathPath); return BasisIKTestGates.GateCalibrationMath(s); }));
            jobs.Add(() => Job("Twist", twistPath, () => { var c = BasisTwistSweepConfig.Default(); c.Cases = Sc(c.Cases, density); var s = BasisTwistSweep.Run(c, twistPath); return BasisIKTestGates.GateTwist(s); }));
            jobs.Add(() => Job("Spine", spinePath, () => { var c = BasisSpineSweepConfig.Default(); c.Cases = Sc(c.Cases, density); var s = BasisSpineSweep.Run(c, spinePath); return BasisIKTestGates.GateSpine(s); }));
            jobs.Add(() => Job("Remote Bone", remoteBonePath, () => { var c = BasisRemoteBoneSweepConfig.Default(); c.Cases = Sc(c.Cases, density); var s = BasisRemoteBoneSweep.Run(c, remoteBonePath); return BasisIKTestGates.GateRemoteBone(s); }));
            jobs.Add(() => Job("Spine Clamp", spineClampPath, () => { var c = BasisSpineClampSweepConfig.Default(); c.VerticalSteps = Sc(c.VerticalSteps, density); c.LateralSteps = Sc(c.LateralSteps, density); var s = BasisSpineClampSweep.Run(c, spineClampPath); return BasisIKTestGates.GateSpineClamp(s); }));
            jobs.Add(() => Job("Hip Hinge", hipHingePath, () => { var c = BasisHipHingeSweepConfig.Default(); c.LeanSteps = Sc(c.LeanSteps, density); c.AzimuthSteps = Sc(c.AzimuthSteps, density); var s = BasisHipHingeSweep.Run(c, hipHingePath); return BasisIKTestGates.GateHipHinge(s); }));
            jobs.Add(() => Job("Chest Spring", chestSpringPath, () => { var s = BasisChestSpringSweep.Run(BasisChestSpringSweepConfig.Default(), chestSpringPath); return BasisIKTestGates.GateChestSpring(s); }));
            jobs.Add(() => Job("Crouch Offset", crouchPath, () => { var c = BasisCrouchOffsetSweepConfig.Default(); c.DepthSteps = Sc(c.DepthSteps, density); c.YawSteps = Sc(c.YawSteps, density); var s = BasisCrouchOffsetSweep.Run(c, crouchPath); return BasisIKTestGates.GateCrouchOffset(s); }));
            jobs.Add(() => Job("Spine Compression", spineCompPath, () => { var c = BasisSpineCompressionSweepConfig.Default(); c.HeadDropSteps = Sc(c.HeadDropSteps, density); var s = BasisSpineCompressionSweep.Run(c, spineCompPath); return BasisIKTestGates.GateSpineCompression(s); }));
            jobs.Add(() => Job("Spine Bend", spineBendPath, () => { var c = BasisSpineBendSweepConfig.Default(); c.HeadGridSteps = Sc(c.HeadGridSteps, density); c.TwistYawSteps = Sc(c.TwistYawSteps, density); var s = BasisSpineBendSweep.Run(c, spineBendPath); return BasisIKTestGates.GateSpineBend(s); }));
            jobs.Add(() => Job("Spine Twist", spineTwistPath, () => { var c = BasisSpineTwistSweepConfig.Default(); c.InvariantCases = Sc(c.InvariantCases, density); c.LeanSteps = Sc(c.LeanSteps, density); var s = BasisSpineTwistSweep.Run(c, spineTwistPath); return BasisIKTestGates.GateSpineTwist(s); }));
            jobs.Add(() => Job("Swivel Filter", swivelFilterPath, () => { var s = BasisSwivelFilterSweep.Run(BasisSwivelFilterSweepConfig.Default(), swivelFilterPath); return BasisIKTestGates.GateSwivelFilter(s); }));
            jobs.Add(() => Job("One-Euro Filter", oneEuroPath, () => { var s = BasisOneEuroSweep.Run(BasisOneEuroSweepConfig.Default(), oneEuroPath); return BasisIKTestGates.GateOneEuro(s); }));
            jobs.Add(() => Job("Eye Gaze", eyePath, () => { var s = BasisEyeSweep.Run(BasisEyeSweepConfig.Default(), eyePath); return BasisIKTestGates.GateEye(s); }));
            jobs.Add(() => Job("Blink Timing", blinkPath, () => { var s = BasisBlinkTimingSweep.Run(BasisBlinkTimingSweepConfig.Default(), blinkPath); return BasisIKTestGates.GateBlinkTiming(s); }));
            jobs.Add(() => Job("Locomotion", locomotionPath, () => { var s = BasisLocomotionSweep.Run(BasisLocomotionSweepConfig.Default(), locomotionPath); return BasisIKTestGates.GateLocomotion(s); }));
            jobs.Add(() => Job("Leg Twist (standing)", legTwistPath, () => { var s = BasisLegTwistSweep.Run(BasisLegTwistSweepConfig.Default(), legTwistPath); return BasisIKTestGates.GateLegTwist(s); }));
            jobs.Add(() => Job("Tracker Bend Normal", bendNormalPath, () => { var s = BasisBendNormalSweep.Run(BasisBendNormalSweepConfig.Default(), bendNormalPath); return BasisIKTestGates.GateBendNormal(s); }));
            jobs.Add(() => Job("Calibration Lock-In", calibLockInPath, () => { var s = BasisCalibrationLockInSweep.Run(BasisCalibrationLockInSweepConfig.Default(), calibLockInPath); bool ok = BasisCalibrationLockInSweep.Gate(s, out string reason); return (ok, reason); }));

            if (includeTraj)
            {
                foreach (bool isLeft in new[] { false, true })
                {
                    bool L = isLeft;
                    string side = L ? "L" : "R";
                    string ltp = SidePath(legTrajPath, L), ltmp = SidePath(legTempPath, L), lrtp = SidePath(legRoundTripPath, L), lsf = SidePath(legStanceFlickerPath, L);

                    jobs.Add(() => Job($"Leg IK · traj ({side})", ltp, () => { var c = BasisLegIKSweepConfig.Default(); c.IsLeft = L; var s = BasisLegIKSweep.RunTrajectories(c, trajNoise, 1f / 90f, false, ltp); return BasisIKTestGates.GateTrajectory(s.Ok, s.Error, s.WorstPopDeg, s.WorstRoughDeg); }));
                    jobs.Add(() => Job($"Leg IK · temporal+footnoise ({side})", ltmp, () => { var c = BasisLegIKSweepConfig.Default(); c.IsLeft = L; var s = BasisLegIKSweep.RunTrajectories(c, trajNoise, 1f / 90f, true, ltmp); return BasisIKTestGates.GateLegKneeJitter(s.Ok, s.Error, s.WorstKneeJitterWellCondM, s.WorstKneeJitterM, trajNoise); }));
                    // Pole round-trip: hint + foot both present, smooth foot motion (noise-free, stateful feed) -- catches the
                    // knee pole swinging out and rotating back to where it was, which the per-frame pop gate above misses.
                    jobs.Add(() => Job($"Leg IK · pole round-trip ({side})", lrtp, () => { var c = BasisLegIKSweepConfig.Default(); c.IsLeft = L; var s = BasisLegIKSweep.RunTrajectories(c, 0f, 1f / 90f, true, lrtp); return BasisIKTestGates.GateLegSwivelRoundTrip(s.Ok, s.Error, s.WorstSwivelRoundTripDeg, s.WorstRoundTripPath); }));
                    // Stateful flicker: hold near-straight + 3mm foot noise, feed the previous knee -- the live model of the standing outward/inward flip (the stateless straight-stance check can't see it).
                    jobs.Add(() => Job($"Leg IK · stance flicker ({side})", lsf, () => { var c = BasisLegIKSweepConfig.Default(); c.IsLeft = L; var s = BasisLegIKSweep.RunStraightStanceTemporal(c, trajNoise, lsf); return BasisIKTestGates.GateLegStraightStanceFlicker(s); }));
                }
                jobs.Add(() => Job("Head · traj", headTrajPath, () => { var s = BasisHeadSweep.RunTrajectories(BasisHeadSweepConfig.Default(), 0.3f, headTrajPath); return BasisIKTestGates.GateTrajectory(s.Ok, s.Error, s.WorstPopDeg, s.WorstRoughDeg); }));
                jobs.Add(() => Job("Leg Inversion · temporal", legInvTempPath, () => { var c = BasisLegInversionConfig.Default(); c.SafeConeDeg = BasisIKTestGates.LegInvertHintSafeConeDeg; var s = BasisLegInversionSweep.RunTemporal(c, trajNoise, legInvTempPath); return BasisIKTestGates.GateLegInversionTemporal(s); }));
                jobs.Add(() => Job("Leg Inversion · crouch (L)", legCrouchLPath, () => { var c = BasisLegInversionConfig.Default(); c.Base.IsLeft = true; var s = BasisLegInversionSweep.RunCrouch(c, legCrouchLPath); return BasisIKTestGates.GateLegCrouch(s); }));
                jobs.Add(() => Job("Leg Inversion · crouch (R)", legCrouchRPath, () => { var c = BasisLegInversionConfig.Default(); c.Base.IsLeft = false; var s = BasisLegInversionSweep.RunCrouch(c, legCrouchRPath); return BasisIKTestGates.GateLegCrouch(s); }));
            }

            // Fan out: one worker thread per sweep (the .NET thread pool caps concurrency to the core count).
            var running = new System.Threading.Tasks.Task<Row[]>[jobs.Count];
            for (int i = 0; i < jobs.Count; i++) { var job = jobs[i]; running[i] = System.Threading.Tasks.Task.Run(job); }
            try { System.Threading.Tasks.Task.WaitAll(running); }
            catch (System.AggregateException) { } // every job already captures its own exception into a FAIL row

            foreach (var t in running)
                foreach (var row in t.Result)
                    Record(row.Name, row.Ok, row.Detail, row.Path);

            // Foot placement runs on the MAIN THREAD (not in the parallel fan-out above): it ticks the real
            // Burst foot job over NativeArrays, which the editor's job-safety system won't let us allocate on
            // a worker thread. It is a short temporal sim, so running it serially here costs almost nothing.
            try
            {
                var fs = BasisFootIKSweep.Run(BasisFootIKSweepConfig.Default(), footPath);
                var fg = BasisIKTestGates.GateFoot(fs);
                Record("Foot Placement", fg.pass, fg.reason, footPath);
            }
            catch (System.Exception e) { Record("Foot Placement", false, e.Message, null); }

            // Bone-sim stability and the virtual-spine temporal solve also run on the MAIN THREAD: both tick a
            // real Burst job over NativeArrays (allocated here), same as Foot Placement above. Short temporal sims.
            try
            {
                var bs = BasisBoneSimStabilitySweep.Run(BasisBoneSimStabilitySweepConfig.Default(), boneSimPath);
                var bg = BasisIKTestGates.GateBoneSim(bs);
                Record("Bone Sim Stability", bg.pass, bg.reason, boneSimPath);
            }
            catch (System.Exception e) { Record("Bone Sim Stability", false, e.Message, null); }

            try
            {
                var ss = BasisVirtualSpineTemporalSweep.Run(BasisVirtualSpineTemporalSweepConfig.Default(), spineTemporalPath);
                var sg = BasisIKTestGates.GateVirtualSpineTemporal(ss);
                Record("Virtual Spine Temporal", sg.pass, sg.reason, spineTemporalPath);
            }
            catch (System.Exception e) { Record("Virtual Spine Temporal", false, e.Message, null); }
        }

        // One sweep job for the parallel Run All: runs body on a worker thread, turns its (pass, reason) into
        // a Row, and captures any exception (incl. a sweep that touches a main-thread-only API) into a FAIL
        // row so one bad sweep can't sink the rest.
        static Row[] Job(string name, string path, System.Func<(bool pass, string reason)> body)
        {
            try { var (pass, reason) = body(); return new[] { new Row { Name = name, Ok = pass, Detail = reason, Path = path } }; }
            catch (System.Exception e) { return new[] { new Row { Name = name, Ok = false, Detail = e.Message, Path = null } }; }
        }

        // Inserts _L / _R before the extension so the left and right sweeps write distinct CSVs (two
        // sides writing the same file in parallel would corrupt it). Pure string op -- safe off-thread.
        static string SidePath(string basePath, bool isLeft)
        {
            string dir = System.IO.Path.GetDirectoryName(basePath);
            string name = System.IO.Path.GetFileNameWithoutExtension(basePath);
            string ext = System.IO.Path.GetExtension(basePath);
            return System.IO.Path.Combine(dir, name + (isLeft ? "_L" : "_R") + ext);
        }

        static string TrajPath(string fname)
        {
            return System.IO.Path.Combine(Application.persistentDataPath, fname);
        }

        void Record(string name, bool ok, string detail, string path)
        {
            _rows.Add(new Row { Name = name, Ok = ok, Detail = detail, Path = path });
            if (ok) Debug.Log($"[IKTests] {name} PASS: {detail} -> {path}");
            else Debug.LogError($"[IKTests] {name} FAIL: {detail}");
        }
    }
}
