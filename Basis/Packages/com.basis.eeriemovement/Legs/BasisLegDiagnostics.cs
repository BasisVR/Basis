namespace Basis.IK
{
    public struct BasisLegDiagnostics
    {
        public float ReachRatio;
        public float KneeAngleDeg;
        public float AxisSource;
        public float HintApplied;
        public float ModelHintUsed;
        public float ModelConfidence;
        public float HintDistrust;
        public float RawSwivelDeg;
        public float SmoothSwivelDeg;
        public float Conditioning;
        public float HoldGate;
        public float AnteriorGuardApplied;
        public float Seeded;
        public float ShinRollDeg;

        // Hip pose in the PELVIS frame, for diagnosing hip-to-upper-leg complaints. Nothing constrains the
        // femur against the pelvis anywhere in the solve, so these are logged rather than guarded: they say
        // whether a reported hip artifact is actually out of anatomical range (roughly 120 flex / 30 ext /
        // 45 abd / 30 add) or something else. Flex/abduction come from the femur DIRECTION, so they carry no
        // bind-convention dependency; the twist does and is only meaningful as a relative signal.
        public float HipFlexionDeg;
        public float HipAbductionDeg;
        public float FemurTwistDeg;

        public static string Header =>
            "leg,reach,kneeDeg,axisSrc,hintApplied,modelUsed,modelConf,distrust,rawSwivel,smoothSwivel,cond,holdGate,antGuard,seeded,shinRoll,hipFlex,hipAbd,femurTwist";

        public string ToRow(string leg) =>
            $"{leg},{ReachRatio:F4},{KneeAngleDeg:F2},{AxisSource:F0},{HintApplied:F0},{ModelHintUsed:F0}," +
            $"{ModelConfidence:F3},{HintDistrust:F3},{RawSwivelDeg:F2},{SmoothSwivelDeg:F2}," +
            $"{Conditioning:F4},{HoldGate:F3},{AnteriorGuardApplied:F0},{Seeded:F0},{ShinRollDeg:F2}," +
            $"{HipFlexionDeg:F2},{HipAbductionDeg:F2},{FemurTwistDeg:F2}";
    }
}
