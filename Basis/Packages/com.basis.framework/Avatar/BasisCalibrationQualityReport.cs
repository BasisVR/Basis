using Unity.Scripting.LifecycleManagement;
using System;
using System.Text;
using Basis.BasisUI;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Read-only "how good was this calibration" summary. Tracker coverage / fit / staleness come from the
    /// reliable constellation snapshot the role classifier already fills (BasisAvatarIKStageCalibration.
    /// ConstellationDebug). It never changes avatar behaviour, and the whole capture is wrapped so a report
    /// failure can never break calibration.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisCalibrationQualityReport
    {
        public static bool HasReport;
        public static string Grade = "-";
        public static int TrackersAssigned;
        public static int TrackersExpected;
        public static int TrackersStale;
        public static int TrackersMarginal;
        public static float EyeHeight;
        public static string Summary = "No calibration captured yet.";

        // Fit margin (assigned score above the accept threshold) below which a tracker counts as marginal.
        const float k_MarginalBand = 3f;

        public static void Capture()
        {
            HasReport = false;
            try
            {
                if (!BasisAvatarIKStageCalibration.ConstellationDebug.HasSnapshot)
                {
                    Summary = "No calibration captured yet.";
                    return;
                }

                var samples = BasisAvatarIKStageCalibration.ConstellationDebug.Samples;
                var priors = BasisAvatarIKStageCalibration.ConstellationDebug.Priors;
                float threshold = BasisAvatarIKStageCalibration.ConstellationDebug.AcceptThreshold;
                EyeHeight = BasisAvatarIKStageCalibration.ConstellationDebug.EyeHeight;

                int assigned = 0, stale = 0, marginal = 0;
                if (samples != null)
                {
                    foreach (var s in samples)
                    {
                        if (s == null) continue;
                        if (s.NearOrigin) stale++;
                        if (s.Assigned)
                        {
                            assigned++;
                            if (!s.NearOrigin && (s.AssignedScore - threshold) < k_MarginalBand) marginal++;
                        }
                    }
                }

                int expected = 0;
                if (priors != null)
                {
                    foreach (var p in priors)
                    {
                        if (p != null && p.Enabled) expected++;
                    }
                }

                TrackersAssigned = assigned;
                TrackersExpected = Math.Max(expected, assigned);
                TrackersStale = stale;
                TrackersMarginal = marginal;
                Grade = ComputeGrade(assigned, TrackersExpected, stale, marginal);
                Summary = BuildSummary();
                HasReport = true;
            }
            catch (Exception e)
            {
                HasReport = false;
                Summary = "Calibration report unavailable: " + e.Message;
            }
        }

        // Staleness is an automatic fail (a dead tracker bound to a role); otherwise grade on coverage
        // and how many trackers only just cleared the accept threshold.
        static string ComputeGrade(int assigned, int expected, int stale, int marginal)
        {
            if (assigned <= 0 || stale > 0) return "F";
            float coverage = expected > 0 ? assigned / (float)expected : 1f;
            if (coverage >= 0.999f && marginal == 0) return "A";
            if (coverage >= 0.999f && marginal <= 1) return "B";
            if (coverage >= 0.85f && marginal <= 2) return "C";
            if (coverage >= 0.7f) return "D";
            return "F";
        }

        public static string BuildSummary()
        {
            var sb = new StringBuilder(256);
            sb.Append(BasisLocalization.Get("calibration.report.trackers", TrackersAssigned, TrackersExpected));
            if (TrackersStale > 0) sb.Append("   ").Append(BasisLocalization.Get("calibration.report.stale", TrackersStale));
            if (TrackersMarginal > 0) sb.Append("   ").Append(BasisLocalization.Get("calibration.report.marginal", TrackersMarginal));
            if (EyeHeight > 0f) sb.Append("   ").Append(BasisLocalization.Get("calibration.report.eye", EyeHeight.ToString("0.00")));
            sb.Append('\n');

            if (TrackersStale > 0)
            {
                sb.Append(BasisLocalization.Get("calibration.report.staleWarning"));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
