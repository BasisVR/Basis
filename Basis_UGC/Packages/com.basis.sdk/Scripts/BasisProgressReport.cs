using System;

public class BasisProgressReport
{
    // Delegate definitions for various stages of progress
    public delegate void ProgressReportState(string UniqueID, float progress, string eventDescription);
    public event ProgressReportState OnProgressReport;
    public const float MaxValue = 100f;
    public const float MinValue = 0f;

    /// <summary>
    /// Reports the current progress along with an event description.
    /// Ensures that progress is between 0 and 100.
    /// </summary>
    /// <param name="UniqueID">A Unique ID.</param>
    /// <param name="progress">A float value between 0 and 100 representing the progress.</param>
    /// <param name="eventDescription">A string describing the current event or stage.</param>
    public void ReportProgress(string UniqueID, float progress, string eventDescription)
    {
        progress = Math.Clamp(progress, MinValue, MaxValue); // Ensuring progress is within bounds

        // BasisDebug.LogError("Current Progress is " + progress);
        OnProgressReport?.Invoke(UniqueID, progress, eventDescription);
    }
}
