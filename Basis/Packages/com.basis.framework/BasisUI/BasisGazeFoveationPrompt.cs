using System;

namespace Basis.BasisUI
{
    /// <summary>
    /// Per-headset prompt offering to enable gaze-foveated Variable Rate Shading when an
    /// eye-tracking headset is detected. Enable and DontRemind are remembered by the caller;
    /// No and closing without answering leave it undecided, so it is asked again next time.
    /// </summary>
    public static class BasisGazeFoveationPrompt
    {
        public static void Show(string deviceName, Action<BasisFeatureDetectedPrompt.Choice> onDecided, Action onDismissed)
        {
            string body = BasisLocalization.Get("settings.eyeFoveation.prompt.body");
            if (!string.IsNullOrEmpty(deviceName))
            {
                body = BasisLocalization.Get("settings.eyeFoveation.prompt.detected", deviceName) + "\n\n" + body;
            }
            BasisFeatureDetectedPrompt.Show(BasisLocalization.Get("settings.eyeFoveation.prompt.title"), body,
                nameof(BasisGazeFoveationPrompt), onDecided, onDismissed);
        }
    }
}
