namespace Basis.IK.Mocap
{
    public enum BasisMocapHintSource
    {
        None,          // no hint at all -- the two-bone core's internal fallback
        Lookup,        // WHAT SHIPS for an untracked arm: ArmBendFrame -> BasisArmBendLookup -> chicken-wing flare
        LookupNoFlare, // the same lookup, with the chicken-wing flare switched off. Isolates what the flare COSTS.
        SwivelModel,   // THE CANDIDATE: BasisArmSwivelModel -- a polynomial fitted to this very corpus.
        // The same model with the elbow's One-Euro output filter (SmoothElbowSwivel) left ON.
        //
        // This row exists to ANSWER A SHIPPING QUESTION rather than guess at it. The filter was added to fight
        // the LOOKUP's jitter; the model is a polynomial and is already smoother than a real tracker, so the
        // filter may now be pure lag. Ship whichever of SwivelModel / SwivelModelSmoothed measures better --
        // and do not reason about it, because a One-Euro is speed-adaptive and its behaviour on a signal it was
        // not tuned for is not something anyone can predict from the armchair.
        SwivelModelSmoothed,
        // WHAT SHIPS NOW: BasisElbowFieldModel. Predicts the elbow's POSITION and projects it onto the
        // reachable circle -- no reference direction, no atan2, no confidence gate.
        //
        // SwivelModel above predicted the swivel ANGLE, which has to be measured FROM something, and every
        // choice of reference vanishes somewhere on the sphere of hand directions (hairy ball). Its choice --
        // body-DOWN -- vanished when the arm hangs down, which is 29.7% of real human frames and the commonest
        // pose in VR. Keep the row: it is the A/B that proves the difference is the frame and not the fit.
        ElbowField,
        // THE NEURAL CANDIDATE: small MLPs (3->24->16->2) fitted to the SAME dumped features as SwivelModel and
        // predicting the SAME (sin,cos) swivel, so they share SwivelModel's exact wiring and BendDirection --
        // this row isolates "MLP vs polynomial" with everything else held fixed. The ELBOW uses
        // BasisArmSwivelNeuralModel (A/B vs BasisElbowFieldModel, which ships); the KNEE uses
        // BasisLegSwivelNeuralModel (A/B vs BasisLegSwivelModel). See SolveArm / SolveLeg.
        NeuralSwivel,
        // The LIVE-RIG elbow path scored offline: BasisSwivelHintCore.ArmHint with useNeural=true (the neural
        // POSITION model, BasisArmElbowNeuralFieldModel). ElbowField is the SAME entry point with useNeural=false,
        // so NeuralField vs ElbowField is EXACTLY what UseNeuralPole flips on a real avatar's elbow. (The knee's
        // live path is the neural angle model, already the NeuralSwivel row's knee column.)
        NeuralField,
        TruthJoint,    // the elbow/knee tracker case: hand the solver the real joint. The accuracy CEILING.
    }
}
