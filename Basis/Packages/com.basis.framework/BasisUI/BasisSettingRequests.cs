using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisSettingRequests
    {
        public enum Result
        {
            Shown = 0,
            Declined = 1,
            Blocked = 2,
            Busy = 3,
            Unsupported = 4,
            NotFound = 5,
        }
        public const float DeclineCooldownSeconds = 30f;
        public const int DeclinesBeforeMute = 3, MaxKeyLength = 128, MaxMessageLength = 140;
        private sealed class Strike
        {
            public int Count;
            public float Until;
        }
        private sealed class Ask
        {
            public string AskerId, BindingKey, Title, Body;
            public Action<Result> OnResult;
            public bool Done;
        }
        private static readonly Dictionary<string, Strike> strikes = new Dictionary<string, Strike>();
        private static Ask current;
        public static bool IsAsking => current != null;
        public static bool IsMuted(string askerId) => !string.IsNullOrEmpty(askerId) && strikes.TryGetValue(askerId, out Strike strike) && (strike.Count >= DeclinesBeforeMute || Time.realtimeSinceStartup < strike.Until);
        public static bool ValidKey(string bindingKey)
        {
            if (string.IsNullOrEmpty(bindingKey) || bindingKey.Length > MaxKeyLength) return false;
            for (int i = 0; i < bindingKey.Length; i++)
            {
                char c = bindingKey[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')) return false;
            }
            return true;
        }
        public static string CleanMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            StringBuilder builder = new StringBuilder(Math.Min(message.Length, MaxMessageLength));
            bool space = false;
            for (int i = 0; i < message.Length && builder.Length < MaxMessageLength; i++)
            {
                char c = message[i];
                if (c == '<' || c == '>' || char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    space = builder.Length > 0;
                    continue;
                }
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
                if (space) builder.Append(' ');
                space = false;
                builder.Append(c);
            }
            return builder.ToString();
        }
        public static void Request(string askerId, string contentName, string contentKind, string ownerName, string bindingKey, string message, Action<Result> onResult)
        {
#if UNITY_SERVER
            Deliver(onResult, Result.Unsupported);
#else
            if (string.IsNullOrEmpty(askerId) || !ValidKey(bindingKey) || IsMuted(askerId))
            {
                Deliver(onResult, Result.Blocked);
                return;
            }
            if (current != null)
            {
                Deliver(onResult, Result.Busy);
                return;
            }
            string what = BasisExternalLinks.CleanName(contentName);
            if (what.Length == 0) what = BasisLocalization.Get("externalLinks.unnamed");
            string kind = contentKind ?? string.Empty;
            string who = BasisExternalLinks.CleanName(ownerName);
            if (who.Length > 0) kind = BasisLocalization.Get("settingRequests.from", kind, who);
            string body = BasisLocalization.Get("settingRequests.body", what, kind);
            string said = CleanMessage(message);
            if (said.Length > 0) body += "\n\n" + BasisLocalization.Get("settingRequests.says", said);
            Ask ask = new Ask { AskerId = askerId, BindingKey = bindingKey, Title = BasisLocalization.Get("settingRequests.title"), Body = body, OnResult = onResult };
            current = ask;
            try
            {
                Show(ask);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[SettingRequests] Could not ask to show a setting: {e}");
                Finish(ask, Result.Unsupported);
            }
#endif
        }
        private static void Show(Ask ask)
        {
            if (ask.Done) return;
            if (BasisNotificationCenter.RouteToNotifications)
            {
                Defer(ask);
                return;
            }
            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                Defer(ask);
                return;
            }
            if (BasisMainMenu.Instance.Dialogue && !BasisMainMenu.Instance.Dialogue.IsReleased)
            {
                BasisMainMenu.Instance.Dialogue.OnInstanceReleased += () => BasisDeviceManagement.EnqueueOnMainThread(() => Show(ask));
                return;
            }
            bool answered = false;
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(ask.Title, ask.Body, BasisLocalization.Get("content.share.setting.show"), BasisLocalization.Get("ui.cancel"), accepted =>
            {
                answered = true;
                if (accepted) Open(ask);
                else Finish(ask, Result.Declined);
            }, false, BasisPanelSeverity.Calm, BasisNotificationCategory.Content);
            if (panel == null)
            {
                Defer(ask);
                return;
            }
            BasisMainMenu.Instance.Dialogue = panel;
            panel.CaptureOnClose = false;
            BasisPromptArming.Arm(panel.AcceptButton);
            panel.OnInstanceReleased += () =>
            {
                if (!answered) Defer(ask);
            };
        }
        private static void Open(Ask ask)
        {
            if (ask.Done) return;
            ask.Done = true;
            if (ReferenceEquals(current, ask)) current = null;
            strikes.Remove(ask.AskerId);
            _ = ShowSetting(ask);
        }
        private static async Task ShowSetting(Ask ask)
        {
            PanelComponent shown = null;
            try
            {
                await Awaitable.NextFrameAsync();
                shown = await SettingsProvider.RevealSetting(ask.BindingKey);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[SettingRequests] Could not show a setting: {e}");
            }
            if (shown != null)
            {
                Deliver(ask.OnResult, Result.Shown);
                return;
            }
            BasisMenuDialoguePanel.CreateNew(BasisLocalization.Get("content.share.setting.missing.title"), BasisLocalization.Get("settingRequests.missing"), BasisLocalization.Get("ui.ok"), _ => { }, false, BasisPanelSeverity.Calm, BasisNotificationCategory.Content);
            Deliver(ask.OnResult, Result.NotFound);
        }
        private static void Defer(Ask ask)
        {
            if (ask.Done) return;
            BasisNotificationCenter.AddPending(ask.Title, ask.Body, AddressableAssets.Sprites.Settings, reopen: () => Show(ask), onDismiss: () => Finish(ask, Result.Declined), category: BasisNotificationCategory.Content);
        }
        private static void Finish(Ask ask, Result result)
        {
            if (ask.Done) return;
            ask.Done = true;
            if (ReferenceEquals(current, ask)) current = null;
            if (result == Result.Declined) StrikeOut(ask.AskerId);
            Deliver(ask.OnResult, result);
        }
        private static void StrikeOut(string askerId)
        {
            if (!strikes.TryGetValue(askerId, out Strike strike))
            {
                strike = new Strike();
                strikes[askerId] = strike;
            }
            strike.Count++;
            strike.Until = Time.realtimeSinceStartup + DeclineCooldownSeconds;
        }
        private static void Deliver(Action<Result> onResult, Result result)
        {
            if (onResult == null) return;
            try
            {
                onResult(result);
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[SettingRequests] A setting request callback threw: {e}");
            }
        }
    }
}
