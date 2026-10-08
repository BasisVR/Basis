using Unity.Scripting.LifecycleManagement;
using System;
using System.Threading;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Bridges the URL security gate to the player. A Fake-IP proxy (Clash, sing-box in TUN mode)
    /// answers every domain with a synthetic address out of 198.18.0.0/15 and carries the
    /// connection itself, so every download is refused as non-routable and the world load simply
    /// stops — the failure looks like a broken server, not a policy the player can change.
    ///
    /// <para>Two jobs, and only two. It mirrors the compatibility setting into
    /// <see cref="BasisUrlSecurity.AllowBenchmarkRangeFromDns"/>, and when that exact refusal
    /// happens it offers the setting. The offer is the whole point: the gate is opened by the
    /// player answering a dialog, never by anything detected here.</para>
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisFakeIpCompatibilityPrompt
    {
        // Offer at most once per app run. A refused world load fires this for every file it was
        // going to fetch, and the answer is the same for all of them.
        private static int _asked;

        [RuntimeInitializeOnLoadMethod]
        private static void Init()
        {
            ApplySettingToGate();
            BasisSettingsDefaults.AllowProxyBenchmarkRange.OnChanged += _ => ApplySettingToGate();
            BasisUrlSecurity.OnBenchmarkRangeRefused += OnRefused;
        }

        private static void ApplySettingToGate()
        {
            BasisUrlSecurity.AllowBenchmarkRangeFromDns = BasisSettingsDefaults.AllowProxyBenchmarkRange.RawValue;
        }

        // Raised off the main thread from the download path.
        private static void OnRefused(string host)
        {
            if (BasisSettingsDefaults.AllowProxyBenchmarkRange.RawValue) return;
            if (Interlocked.Exchange(ref _asked, 1) == 1) return;
            _ = ConfirmAndOfferAsync(host);
        }

        private static async System.Threading.Tasks.Task ConfirmAndOfferAsync(string host)
        {
            bool fakeIp;
            try
            {
                fakeIp = await BasisFakeIpDetection.IsResolverActiveAsync();
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[FakeIP] Detection failed: {ex.Message}", BasisDebug.LogTag.Networking);
                Volatile.Write(ref _asked, 0);
                return;
            }

            if (!fakeIp)
            {
                // Something else put a non-routable address in front of us. Say nothing, and stay
                // armed — a proxy started later in the session still deserves the offer.
                Volatile.Write(ref _asked, 0);
                return;
            }

            BasisDebug.Log($"[FakeIP] '{host}' resolved into 198.18/15 and a Fake-IP resolver is active; offering the compatibility setting.", BasisDebug.LogTag.Networking);
            BasisDeviceManagement.EnqueueOnMainThread(() => ShowPrompt());
        }

        private static void ShowPrompt(bool forceShow = false)
        {
            string title = BasisLocalization.Get("settings.network.fakeIp.prompt.title");
            string body = BasisLocalization.Get("settings.network.fakeIp.prompt.body");

            // Do-not-disturb / VR → park it in the notification list rather than throwing a dialog
            // over whatever they are doing. forceShow bypasses that when reopened from the bell.
            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(title, body, AddressableAssets.Sprites.Network,
                    reopen: () => ShowPrompt(true),
                    onDismiss: () => { },
                    category: BasisNotificationCategory.Network);
                return;
            }

            BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                BasisDebug.LogWarning("[FakeIP] Main menu unavailable — compatibility offer skipped.", BasisDebug.LogTag.Networking);
                return;
            }

            if (BasisMainMenu.Instance.Dialogue != null)
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();

            BasisMainMenu.Instance.OpenDialogue(
                title,
                body,
                BasisLocalization.Get("ui.yes"),
                BasisLocalization.Get("ui.no"),
                accepted =>
                {
                    if (!accepted) return;
                    BasisSettingsDefaults.AllowProxyBenchmarkRange.SetValue(true);
                }, category: BasisNotificationCategory.Network);
        }
    }
}
