using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using static SerializableBasis;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisExternalLinks
    {
        public enum Result
        {
            Opened = 0,
            Declined = 1,
            InvalidUrl = 2,
            Blocked = 3,
            NoInteraction = 4,
            Busy = 5,
            Unsupported = 6,
        }

        public const float InteractionSeconds = 5f;
        public const float ArmSeconds = 1f;
        public const float DeclineCooldownSeconds = 30f;
        public const int DeclinesBeforeMute = 3;
        public const BasisLinkSafety.Warning Refused = BasisLinkSafety.Warning.LocalNetwork | BasisLinkSafety.Warning.Program;
        private const int MaxNameLength = 48;
        private const string SevereHex = "#FF5A4A";

        [NoAutoStaticsCleanup] private static readonly BasisLinkSafety.Warning[] ShownWarnings =
        {
            BasisLinkSafety.Warning.LookAlikeName,
            BasisLinkSafety.Warning.IpAddress,
            BasisLinkSafety.Warning.Shortened,
            BasisLinkSafety.Warning.Unencrypted,
        };

        private sealed class Strike
        {
            public int Count;
            public float Until;
        }

        private sealed class Ask
        {
            public Uri Link;
            public string Address;
            public BasisLinkSafety.Warning Warnings;
            public BasisContentBase Content;
            public string Name;
            public string Kind;
            public Action<Result> OnResult;
            public bool Done;
        }

        private static readonly Dictionary<BasisContentBase, Strike> strikes = new Dictionary<BasisContentBase, Strike>();
        private static readonly List<BasisContentBase> forgotten = new List<BasisContentBase>();
        private static bool asking;

        public static bool IsAsking => asking;

        public static bool TryParse(string url, out Uri link, out string address)
        {
            link = null;
            address = null;
            if (!ContentSharePayload.TryParseLink(url, out Uri parsed)) return false;
            if ((BasisLinkSafety.Inspect(parsed) & Refused) != 0) return false;
            if (!BasisLinkSafety.TryGetOpenAddress(parsed, out string open)) return false;
            link = parsed;
            address = open;
            return true;
        }

        public static void Request(string url, GameObject source, Action<Result> onResult)
        {
            if (!Admit(url, source, out Uri link, out string address, out BasisContentBase content, out Result refusal))
            {
                Deliver(onResult, refusal);
                return;
            }
            Ask ask = new Ask
            {
                Link = link,
                Address = address,
                Warnings = BasisLinkSafety.Inspect(link),
                Content = content,
                Name = NameOf(content),
                Kind = KindOf(content),
                OnResult = onResult,
            };
            asking = true;
            try
            {
                Show(ask);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[ExternalLinks] Could not ask about a link: {e}", BasisDebug.LogTag.System);
                Finish(ask, Result.Unsupported);
            }
        }

        public static bool Admit(string url, GameObject source, out Uri link, out string address, out BasisContentBase content, out Result refusal)
        {
            link = null;
            address = null;
            content = null;
            refusal = Result.Blocked;
            if (Headless)
            {
                refusal = Result.Unsupported;
                return false;
            }
            if (!TryParse(url, out link, out address))
            {
                refusal = Result.InvalidUrl;
                return false;
            }
            if (source == null || !BasisContent.TryResolve(source, out content) || content is BasisAvatar)
            {
                content = null;
                return false;
            }
            if (!BasisUserActivation.Recent(content, InteractionSeconds))
            {
                refusal = Result.NoInteraction;
                return false;
            }
            if (!Allowed() || IsMuted(content)) return false;
            if (asking)
            {
                refusal = Result.Busy;
                return false;
            }
            return true;
        }

        public static bool Allowed() => BasisSettingsDefaults.ExternalLinkRequests.RawValue && !BasisNetworkModeration.ExternalLinksBlockedLocally;

        private static bool Headless
        {
            get
            {
#if UNITY_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsMuted(BasisContentBase content)
        {
            return content != null && strikes.TryGetValue(content, out Strike strike) && (strike.Count >= DeclinesBeforeMute || Time.realtimeSinceStartup < strike.Until);
        }

        public static void Forgive(BasisContentBase content)
        {
            if (content != null) strikes.Remove(content);
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            StringBuilder builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
            bool space = false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    space = builder.Length > 0;
                    continue;
                }
                int length = char.IsSurrogatePair(name, i) ? 2 : 1;
                if (length == 1 && char.IsSurrogate(c)) continue;
                if (c == '<' || c == '>' || c == '\\' || CharUnicodeInfo.GetUnicodeCategory(name, i) == UnicodeCategory.Format)
                {
                    i += length - 1;
                    continue;
                }
                if (builder.Length + length + (space ? 1 : 0) > MaxNameLength) break;
                if (space) builder.Append(' ');
                space = false;
                builder.Append(name, i, length);
                i += length - 1;
            }
            return builder.ToString();
        }

        private static void Show(Ask ask)
        {
            if (ask.Done) return;
            string title = BasisLocalization.Get("externalLinks.prompt.title");
            string body = BasisLocalization.Get("externalLinks.prompt.body", ask.Name, ask.Kind);
            if (BasisNotificationCenter.RouteToNotifications)
            {
                Defer(ask, title, body);
                return;
            }
            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                Defer(ask, title, body);
                return;
            }
            if (BasisMainMenu.Instance.Dialogue && !BasisMainMenu.Instance.Dialogue.IsReleased)
            {
                BasisMainMenu.Instance.Dialogue.OnInstanceReleased += () => BasisDeviceManagement.EnqueueOnMainThread(() => Show(ask));
                return;
            }

            bool answered = false;
            bool severe = (ask.Warnings & BasisLinkSafety.Severe) != 0;
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(
                title,
                body,
                BasisLocalization.Get("externalLinks.prompt.open"),
                BasisLocalization.Get("ui.cancel"),
                accepted =>
                {
                    answered = true;
                    Finish(ask, accepted ? Launch(ask) : Result.Declined);
                },
                false,
                severe ? BasisPanelSeverity.Hot : BasisPanelSeverity.Caution,
                BasisNotificationCategory.Content);
            if (panel == null)
            {
                Defer(ask, title, body);
                return;
            }

            BasisMainMenu.Instance.Dialogue = panel;
            panel.CaptureOnClose = false;
            panel.ShowDetails(Details(ask));
            PanelButton copy = null;
            copy = panel.AddOption(BasisLocalization.Get("keyboard.key.copy"), () => BasisClipboard.Copy(ask.Address, copy), false);
            Arm(panel.AcceptButton);
            panel.OnInstanceReleased += () =>
            {
                if (!answered) Defer(ask, title, body);
            };
        }

        private static void Defer(Ask ask, string title, string body)
        {
            if (ask.Done) return;
            BasisNotificationCenter.AddPending(
                title,
                body + "\n" + BasisLinkSafety.SiteLabel(ask.Link),
                AddressableAssets.Sprites.Link,
                reopen: () => Show(ask),
                onDismiss: () => Finish(ask, Result.Declined),
                category: BasisNotificationCategory.Content);
        }

        private static Result Launch(Ask ask)
        {
            if (!Allowed()) return Result.Blocked;
            try
            {
                return BasisLinkSafety.Open(ask.Address) ? Result.Opened : Result.InvalidUrl;
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[ExternalLinks] The browser could not be opened: {e.Message}", BasisDebug.LogTag.System);
                return Result.Unsupported;
            }
        }

        private static void Finish(Ask ask, Result result)
        {
            if (ask.Done) return;
            ask.Done = true;
            asking = false;
            if (result == Result.Declined) StrikeOut(ask.Content);
            else if (result == Result.Opened) Forgive(ask.Content);
            BasisDebug.Log($"[ExternalLinks] The {ask.Kind} \"{ask.Name}\" asked to open a page on {ask.Link.IdnHost}: {result}", BasisDebug.LogTag.System);
            Deliver(ask.OnResult, result);
        }

        internal static void StrikeOut(BasisContentBase content)
        {
            if (content == null) return;
            if (!strikes.TryGetValue(content, out Strike strike))
            {
                PruneDestroyed();
                strike = new Strike();
                strikes[content] = strike;
            }
            strike.Count++;
            strike.Until = Time.realtimeSinceStartup + DeclineCooldownSeconds;
        }

        private static void PruneDestroyed()
        {
            forgotten.Clear();
            foreach (BasisContentBase key in strikes.Keys)
            {
                if (key == null) forgotten.Add(key);
            }
            for (int i = 0; i < forgotten.Count; i++) strikes.Remove(forgotten[i]);
            forgotten.Clear();
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
                BasisDebug.LogErrorUnreported($"[ExternalLinks] A link result callback threw: {e}", BasisDebug.LogTag.System);
            }
        }

        private static List<BasisMenuDialoguePanel.DetailRow> Details(Ask ask)
        {
            List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>
            {
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("externalLinks.prompt.site"), BasisLinkSafety.SiteLabel(ask.Link), true),
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("externalLinks.prompt.link"), ask.Address, true),
            };
            for (int i = 0; i < ShownWarnings.Length; i++)
            {
                BasisLinkSafety.Warning warning = ShownWarnings[i];
                if ((ask.Warnings & warning) == 0) continue;
                string key = "content.share.link.warning." + WarningKey(warning);
                string heading = BasisLocalization.Get(key + ".title");
                if ((warning & BasisLinkSafety.Severe) != 0) heading = $"<color={SevereHex}>{heading}</color>";
                rows.Add(new BasisMenuDialoguePanel.DetailRow(heading, BasisLocalization.Get(key), true));
            }
            return rows;
        }

        private static string WarningKey(BasisLinkSafety.Warning warning)
        {
            switch (warning)
            {
                case BasisLinkSafety.Warning.LocalNetwork: return "local";
                case BasisLinkSafety.Warning.IpAddress: return "ip";
                case BasisLinkSafety.Warning.LookAlikeName: return "lookalike";
                case BasisLinkSafety.Warning.Shortened: return "shortened";
                case BasisLinkSafety.Warning.Program: return "program";
                default: return "unencrypted";
            }
        }

        private static string NameOf(BasisContentBase content)
        {
            string name = content.BasisBundleDescription != null ? content.BasisBundleDescription.AssetBundleName : null;
            if (string.IsNullOrWhiteSpace(name) && content is BasisScene) name = content.gameObject.scene.name;
            string clean = CleanName(name);
            return clean.Length > 0 ? clean : BasisLocalization.Get("externalLinks.unnamed");
        }

        private static string KindOf(BasisContentBase content)
        {
            return BasisLocalization.Get(content is BasisScene ? "externalLinks.kind.world" : "externalLinks.kind.prop");
        }

        private static void Arm(PanelButton accept)
        {
            if (accept == null) return;
            accept.SetInteractable(false, BasisLocalization.Get("externalLinks.prompt.wait"));
            accept.gameObject.AddComponent<Arming>().Begin(accept, ArmSeconds);
        }

        private sealed class Arming : MonoBehaviour
        {
            private PanelButton button;
            private float readyAt;

            public void Begin(PanelButton target, float seconds)
            {
                button = target;
                readyAt = Time.unscaledTime + seconds;
            }

            private void Update()
            {
                if (Time.unscaledTime < readyAt) return;
                if (button != null) button.SetInteractable(true);
                Destroy(this);
            }
        }
    }
}
