using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Thresholds a run is graded against. Defaults are the bands written up
/// in the engine's TESTING.md; they are parameters so a lane with known
/// different characteristics can state its own rather than lower the bar
/// for everything.
/// </summary>
public sealed class BasisMediaSmokeBands
{
    /// <summary>Seconds of the start to ignore. A join legitimately holds
    /// frames and moves the clock about while it settles, and including
    /// that hides real regressions behind a known one.</summary>
    public double JoinSkipSeconds = 2.0;

    /// <summary>How far the clock may drift from 1 s per wall second. The
    /// sync ladder's slew cap is 2%.</summary>
    public double PositionRateTolerance = 0.02;

    /// <summary>Frames presented may trail frames decoded by this many
    /// before it counts as dropping.</summary>
    public long MaxDecodedMinusPresented = 8;

    /// <summary>Share of presented frames that must be held for the modal
    /// number of Unity frames.</summary>
    public double MinIdealHoldShare = 0.99;

    /// <summary>How far the audio pull may sit from the stream rate.</summary>
    public double PullRateTolerance = 0.01;

    /// <summary>How far a per-speaker output may sit from the device rate.
    /// The taps render straight into DSP blocks, so anything but the device
    /// rate means blocks were missed and the output broke up.</summary>
    public double OutputRateTolerance = 0.02;

    /// <summary>Whether the run is expected to have audio outputs wired.
    /// A player on its own is silent by design, so a video-only rig states
    /// this rather than failing.</summary>
    public bool ExpectAudioOutput = true;

    /// <summary>Whether the run is expected to reach Ended.</summary>
    public bool ExpectEnded = true;

    /// <summary>Whether a failing hold share fails the run. Off by
    /// default because an editor play session does not present on a
    /// stable display cadence. The number is worth reporting there, but
    /// only a build or a device can be held to it.</summary>
    public bool EnforceHoldShare;
}

/// <summary>What a graded run measured, and what it made of it.</summary>
public sealed class BasisMediaSmokeReport
{
    public bool Passed => Failures.Count == 0;
    public readonly List<string> Failures = new List<string>();
    public readonly List<string> Notes = new List<string>();

    public int Rows;
    public bool ReachedPlaying;
    public bool ReachedEnded;
    public bool SawError;
    public double PositionRate;
    public int BackwardsSteps;
    public long Decoded;
    public long Presented;
    public int IdealHold;
    public double IdealHoldShare;
    public double PullRate;
    public int StreamRate;
    public long MinBankedWhilePlaying;
    public bool HasOutputColumns;
    public int BoundOutputs;
    public double OutputRate;
    public int DspRate;
    public float PeakOutputLevel;

    public string Summarise()
    {
        var text = new StringBuilder();
        text.AppendLine(Passed ? "SMOKE PASS" : "SMOKE FAIL");
        text.AppendLine($"  rows           {Rows}");
        text.AppendLine($"  reached        playing={ReachedPlaying} ended={ReachedEnded} error={SawError}");
        text.AppendLine($"  position rate  {PositionRate:F4} s/s, {BackwardsSteps} backwards step(s)");
        text.AppendLine($"  frames         decoded {Decoded}, presented {Presented}");
        text.AppendLine($"  frame holds    {IdealHoldShare * 100:F1}% at {IdealHold} unity frame(s)");
        text.AppendLine($"  audio pull     {PullRate:F0} Hz against a {StreamRate} Hz stream");
        text.AppendLine(HasOutputColumns
            ? $"  audio out      {BoundOutputs} output(s), {OutputRate:F0} Hz against a {DspRate} Hz device, peak {PeakOutputLevel:F3}"
            : "  audio out      not captured");
        text.AppendLine($"  banked min     {MinBankedWhilePlaying} ms while playing");
        foreach (string failure in Failures) text.AppendLine($"  FAIL  {failure}");
        foreach (string note in Notes) text.AppendLine($"  note  {note}");
        return text.ToString();
    }
}

/// <summary>
/// Grades a frame-capture CSV against the documented bands, so a run is
/// judged by its numbers rather than by watching it. Deliberately free of
/// Unity types: the judgement is the part with logic in it, and it can be
/// exercised on its own.
/// </summary>
public static class BasisMediaSmokeGrader
{
    /// <param name="lines">The capture, header row first.</param>
    public static BasisMediaSmokeReport Grade(IReadOnlyList<string> lines, BasisMediaSmokeBands bands)
    {
        bands ??= new BasisMediaSmokeBands();
        var report = new BasisMediaSmokeReport();
        if (lines == null || lines.Count < 2)
        {
            report.Failures.Add("the capture is empty: the logger never wrote a row");
            return report;
        }

        string[] header = Split(lines[0]);
        var column = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length; i++) column[header[i].Trim()] = i;

        string[] required =
        {
            "unity_time", "state", "position_us", "banked_ms", "decoded", "presented",
            "presented_delta", "frames_held", "audio_pulled", "stream_rate",
        };
        foreach (string name in required)
        {
            if (!column.ContainsKey(name))
            {
                report.Failures.Add($"the capture has no '{name}' column; it comes from a different build");
                return report;
            }
        }

        // A capture without the per-speaker columns still grades on
        // everything else.
        report.HasOutputColumns = column.ContainsKey("out_consumed") && column.ContainsKey("out_bound");

        var holds = new Dictionary<int, int>();
        double firstTime = double.NaN, lastTime = 0;
        double windowStartTime = double.NaN, windowStartPosition = 0;
        double lastPosition = double.NaN;
        long windowStartPulled = -1, lastPulled = 0;
        long windowStartConsumed = -1, lastConsumed = 0;
        report.MinBankedWhilePlaying = long.MaxValue;
        report.Rows = lines.Count - 1;

        for (int row = 1; row < lines.Count; row++)
        {
            string[] cells = Split(lines[row]);
            if (cells.Length < header.Length) continue; // a torn final row on a hard stop

            double time = Number(cells, column, "unity_time");
            int state = (int)Number(cells, column, "state");
            double position = Number(cells, column, "position_us") / 1_000_000.0;
            long banked = (long)Number(cells, column, "banked_ms");
            long pulled = (long)Number(cells, column, "audio_pulled");

            if (double.IsNaN(firstTime)) firstTime = time;
            lastTime = time;

            if (state == 6) report.SawError = true;
            if (state == 3) report.ReachedPlaying = true;
            if (state == 5) report.ReachedEnded = true;

            report.Decoded = (long)Number(cells, column, "decoded");
            report.Presented = (long)Number(cells, column, "presented");
            report.StreamRate = (int)Number(cells, column, "stream_rate");
            lastPulled = pulled;
            if (report.HasOutputColumns)
            {
                lastConsumed = (long)Number(cells, column, "out_consumed");
                report.BoundOutputs = (int)Number(cells, column, "out_bound");
                report.DspRate = (int)Number(cells, column, "dsp_rate");
            }

            if (state == 3 && banked < report.MinBankedWhilePlaying) report.MinBankedWhilePlaying = banked;

            // The clock is only meaningful while playing, and only past the
            // join.
            bool inWindow = state == 3 && time - firstTime >= bands.JoinSkipSeconds;
            if (inWindow)
            {
                if (double.IsNaN(windowStartTime))
                {
                    windowStartTime = time;
                    windowStartPosition = position;
                    windowStartPulled = pulled;
                    windowStartConsumed = lastConsumed;
                }
                if (!double.IsNaN(lastPosition) && position < lastPosition - 0.001)
                    report.BackwardsSteps++;
                if (report.HasOutputColumns)
                {
                    float peak = (float)Number(cells, column, "out_peak");
                    if (peak > report.PeakOutputLevel) report.PeakOutputLevel = peak;
                }

                int held = (int)Number(cells, column, "frames_held");
                if (held > 0)
                    holds[held] = holds.TryGetValue(held, out int count) ? count + 1 : 1;
            }
            if (state == 3) lastPosition = position;
        }

        if (report.MinBankedWhilePlaying == long.MaxValue) report.MinBankedWhilePlaying = 0;

        double windowSeconds = double.IsNaN(windowStartTime) ? 0 : lastTime - windowStartTime;
        if (windowSeconds > 0.5)
        {
            report.PositionRate = (lastPosition - windowStartPosition) / windowSeconds;
            if (windowStartPulled >= 0)
                report.PullRate = (lastPulled - windowStartPulled) / windowSeconds;
            if (windowStartConsumed >= 0)
                report.OutputRate = (lastConsumed - windowStartConsumed) / windowSeconds;
        }

        // The modal hold is the cadence the run actually settled on; the
        // share at it is how steady that cadence was.
        int best = 0, bestCount = 0, totalHolds = 0;
        foreach (KeyValuePair<int, int> entry in holds)
        {
            totalHolds += entry.Value;
            if (entry.Value > bestCount) { bestCount = entry.Value; best = entry.Key; }
        }
        report.IdealHold = best;
        report.IdealHoldShare = totalHolds > 0 ? bestCount / (double)totalHolds : 0;

        Judge(report, bands, windowSeconds);
        return report;
    }

    static void Judge(BasisMediaSmokeReport report, BasisMediaSmokeBands bands, double windowSeconds)
    {
        if (report.SawError)
            report.Failures.Add("the session reported an error");
        if (!report.ReachedPlaying)
            report.Failures.Add("the session never reached Playing");
        if (bands.ExpectEnded && !report.ReachedEnded)
            report.Failures.Add("the session never reached Ended");

        if (windowSeconds <= 0.5)
        {
            report.Failures.Add(
                $"only {windowSeconds:F2} s of playing time past the join, too short to grade");
            return;
        }

        if (report.BackwardsSteps > 0)
            report.Failures.Add(
                $"the clock snapped backwards {report.BackwardsSteps} time(s)");

        double drift = Math.Abs(report.PositionRate - 1.0);
        if (drift > bands.PositionRateTolerance)
            report.Failures.Add(
                $"the clock ran at {report.PositionRate:F4} s/s, outside ±{bands.PositionRateTolerance:P0}");

        long behind = report.Decoded - report.Presented;
        if (behind > bands.MaxDecodedMinusPresented)
            report.Failures.Add(
                $"{behind} decoded frames were dropped before being presented");

        if (report.StreamRate > 0)
        {
            double ratio = report.PullRate / report.StreamRate;
            if (Math.Abs(ratio - 1.0) > bands.PullRateTolerance)
            {
                string message =
                    $"audio pulled at {report.PullRate:F0} Hz against a {report.StreamRate} Hz stream " +
                    $"({ratio:F3}×), and the clock and picture follow the pull";
                report.Failures.Add(message);
            }
        }
        else
        {
            report.Notes.Add("no audio was announced, so the pull rate was not graded");
        }

        JudgeOutputs(report, bands);

        if (report.IdealHoldShare < bands.MinIdealHoldShare)
        {
            string message =
                $"only {report.IdealHoldShare:P1} of presented frames held for {report.IdealHold} " +
                $"unity frame(s), against a {bands.MinIdealHoldShare:P0} bar";
            if (bands.EnforceHoldShare) report.Failures.Add(message);
            else report.Notes.Add(message + " (not enforced off a stable display cadence)");
        }

        if (report.MinBankedWhilePlaying <= 0)
            report.Notes.Add("the bank reached 0 ms while playing: the source was not keeping ahead");
    }

    /// <summary>
    /// The far side of the ring. The pull band above says the engine was
    /// drained at the stream rate; these say a speaker was actually fed at
    /// the device rate, and that what reached it was not silence. A run can
    /// pass every other band with no sound at all.
    /// </summary>
    static void JudgeOutputs(BasisMediaSmokeReport report, BasisMediaSmokeBands bands)
    {
        if (!report.HasOutputColumns)
        {
            report.Notes.Add("the capture carries no per-output columns, so nothing was graded past the ring");
            return;
        }
        if (!bands.ExpectAudioOutput) return;
        if (report.StreamRate <= 0) return; // no audio in the source to play

        if (report.BoundOutputs <= 0)
        {
            report.Failures.Add(
                "no audio outputs were bound; nothing heard the decoded audio");
            return;
        }

        if (report.DspRate > 0)
        {
            double ratio = report.OutputRate / report.DspRate;
            if (Math.Abs(ratio - 1.0) > bands.OutputRateTolerance)
            {
                report.Failures.Add(
                    $"the primary output ran at {report.OutputRate:F0} Hz against a {report.DspRate} Hz device " +
                    $"({ratio:F3}×): missed DSP blocks, heard as break-up");
            }
        }

        if (report.PeakOutputLevel <= 0f)
        {
            report.Failures.Add(
                "every block the primary output mixed was silent: the outputs ran but carried nothing");
        }
    }

    static string[] Split(string line) => line.Split(',');

    static double Number(string[] cells, Dictionary<string, int> column, string name)
    {
        int index = column[name];
        if (index >= cells.Length) return 0;
        return double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }
}
