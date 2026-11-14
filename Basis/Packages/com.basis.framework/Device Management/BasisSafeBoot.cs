using System;
using System.IO;
using System.Threading.Tasks;
using Basis;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using Basis.Scripts.Rendering;
using UnityEngine;
using Unity.Scripting.LifecycleManagement;
namespace Basis.Scripts.Device_Management
{
    [AutoStaticsCleanup]
    public static partial class BasisSafeBoot
    {
        public const float FinishAfterSeconds = 30f;
        private static string path;
        private static bool started, finished;
        public static bool SafeMode { get; private set; }
        public static string PreviousLastStep { get; private set; }
        public static string PreviousSteps { get; private set; }
        public static void Begin()
        {
            if (started || Application.isEditor) { return; }
            started = true;
            try
            {
                path = Path.Combine(Application.persistentDataPath, "boot.trace");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    PreviousLastStep = lines.Length > 0 ? lines[lines.Length - 1] : "(nothing written)";
                    PreviousSteps = lines.Length > 1 ? string.Join("\n", lines, Math.Max(0, lines.Length - 6), Math.Min(5, lines.Length - 1)) : "(none)";
                    SafeMode = true;
                    File.Copy(path, path + ".prev", true);
                }
                File.WriteAllText(path, $"boot {DateTime.Now:O} wine={BasisProtonDetection.WineVersion ?? "no"} proton={BasisProtonDetection.IsProton}\n");
            }
            catch (Exception e) { path = null; BasisDebug.LogError($"Safe boot trace unavailable: {e.Message}", BasisDebug.LogTag.Device); return; }
            Application.quitting -= Finish;
            Application.quitting += Finish;
            if (SafeMode) { BasisDebug.LogError($"Previous launch did not finish starting (last step: {PreviousLastStep}). Starting in safe mode: GI, ray traced AO, path tracer and D3D11 VRS are off for this launch only. Trace kept at {path}.prev", BasisDebug.LogTag.Device); }
        }
        public const float PopupDelaySeconds = 3f;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterPrompt() => BasisStartupPrompts.Register(ShowPrompt);
        private static bool ShowPrompt(Action onClosed)
        {
            if (!SafeMode) { return false; }
            ShowPromptLater(onClosed);
            return true;
        }
        private static async void ShowPromptLater(Action onClosed)
        {
            await Task.Delay(TimeSpan.FromSeconds(PopupDelaySeconds));
            string body = $"<b>The last launch died after this step: {Describe(PreviousLastStep)}</b>\n\n" +
                $"Steps before it:\n{PreviousSteps}\n\n" +
                $"Host: {(BasisProtonDetection.IsProton ? "Proton" : BasisProtonDetection.IsWine ? "Wine" : "Windows")} {BasisProtonDetection.WineVersion}\n" +
                "Off for this launch only, settings unchanged: global illumination, ray traced AO, path tracer, D3D11 VRS, graphics API relaunch, webcam prompt.\n" +
                $"Trace: {path}.prev";
            try { BasisFeatureDetectedPrompt.ShowNotice("Safe Mode", body, "BasisSafeBootPrompt", "OK", onClosed); }
            catch (Exception e) { BasisDebug.LogError($"Safe mode popup failed: {e}", BasisDebug.LogTag.Device); onClosed?.Invoke(); }
        }
        private static string Describe(string line)
        {
            int space = line?.IndexOf(' ') ?? -1;
            string step = space > 0 && float.TryParse(line.Substring(0, space), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) ? $"{line.Substring(space + 1)} ({seconds:F1} s in)" : line;
            return step;
        }
        public static void Step(string name)
        {
            if (path == null || finished) { return; }
            try { File.AppendAllText(path, $"{Time.realtimeSinceStartup:F2} {name}\n"); } catch (Exception) { }
        }
        public static void ApplySettings()
        {
            if (!SafeMode) { return; }
            BasisExternalRayTracer.Blocked = true;
            BasisVrsD3D11.Blocked = true;
            Preview(BasisSettingsDefaults.UseGlobalIllumination);
            Preview(BasisSettingsDefaults.UseRayTracedAmbientOcclusion);
            Preview(BasisSettingsDefaults.DevVariableRateShading);
            Preview(BasisSettingsDefaults.DevVariableRateShadingDesktop);
        }
        private static void Preview(BasisSettingsBinding<bool> binding)
        {
            binding.SetValueWithoutNotify(false);
            binding.OnChanged?.Invoke(false);
            BasisSettingsSystem.PreviewString(binding.BindingKey, "false");
        }
        public static async void FinishLater()
        {
            if (path == null) { return; }
            Step("initialized");
            await Task.Delay(TimeSpan.FromSeconds(FinishAfterSeconds));
            Finish();
        }
        public static void Finish()
        {
            if (path == null || finished) { return; }
            finished = true;
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
