namespace Basis.BasisUI
{
    /// <summary>
    /// Bridge between the calibration panel and the calibration cutout mirror. The mirror
    /// implementation lives in the BasisExamples assembly (it configures BasisSDKMirror
    /// directly), which this assembly cannot reference; it registers itself here on boot.
    /// </summary>
    public static class BasisCalibrationMirrorService
    {
        public static IBasisCalibrationMirror Provider;
        public static bool Available => Provider != null;
    }

    public interface IBasisCalibrationMirror
    {
        /// <summary>True while the calibration mirror is spawned.</summary>
        bool IsUp { get; }
        /// <summary>Toggle the mirror; returns true if it is now up.</summary>
        bool Toggle();
        /// <summary>Bring the mirror up if it isn't already (idempotent); returns whether it's up.</summary>
        bool Summon();
        /// <summary>Take the mirror down (if up).</summary>
        void Hide();
    }
}
