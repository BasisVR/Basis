using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public static partial class BasisServerRequests
    {
        public enum Result
        {
            Accepted = 0,
            Declined = 1,
            InvalidAddress = 2,
            Blocked = 3,
            NoInteraction = 4,
            Busy = 5,
            Unsupported = 6,
            NotConnected = 7,
        }

        public const float InteractionSeconds = 5f;
        public const float ArmSeconds = 1f;
        public const float DeclineCooldownSeconds = 30f;
        public const int DeclinesBeforeMute = 3;
        public const int MaxAddressLength = 253;
        public const string EntryId = "__content__";

        private sealed class Strike
        {
            public int Count;
            public float Until;
        }

        private sealed class Ask
        {
            public ServerDirectoryEntry Entry;
            public string Server;
            public string UserName;
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

        public static void RequestConnect(string address, ushort port, string password, GameObject source, Action<Result> onResult)
        {
            if (Headless)
            {
                Deliver(onResult, Result.Unsupported);
                return;
            }
            if (!TryBuildEntry(address, port, password, out ServerDirectoryEntry entry, out string server))
            {
                Deliver(onResult, Result.InvalidAddress);
                return;
            }
            Request(entry, server, source, onResult);
        }

        public static void RequestDisconnect(GameObject source, Action<Result> onResult) => Request(null, null, source, onResult);

        public static bool TryBuildEntry(string address, ushort port, string password, out ServerDirectoryEntry entry, out string server)
        {
            entry = null;
            server = null;
            if (port == 0 || !TryCleanAddress(address, out string host, out bool bracketed)) return false;
            string portText = port.ToString(CultureInfo.InvariantCulture);
            server = (bracketed ? "[" + host + "]" : host) + ":" + portText;
            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, server);
            target.Set(ConnectionTarget.Keys.Address, host);
            target.Set(ConnectionTarget.Keys.Port, portText);
            entry = new ServerDirectoryEntry
            {
                Id = EntryId,
                SourceId = SavedServersDirectorySource.Id,
                DisplayName = string.Empty,
                Target = target,
                HasPassword = !string.IsNullOrEmpty(password),
                Password = password ?? string.Empty,
                CanEdit = false,
                CanRemove = false,
                CanAutoConnect = false,
            };
            return true;
        }

        public static bool TryCleanAddress(string address, out string host, out bool bracketed)
        {
            host = null;
            bracketed = false;
            if (string.IsNullOrEmpty(address) || address.Length > MaxAddressLength) return false;
            if (IPAddress.TryParse(address, out IPAddress ip))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork && ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
                host = ip.ToString();
                bracketed = ip.AddressFamily == AddressFamily.InterNetworkV6;
                return true;
            }
            if (Uri.CheckHostName(address) != UriHostNameType.Dns) return false;
            try
            {
                host = new IdnMapping().GetAscii(address).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
            return host.Length > 0 && host.Length <= MaxAddressLength;
        }

        public static bool Admit(GameObject source, out BasisContentBase content, out Result refusal)
        {
            content = null;
            refusal = Result.Blocked;
            if (Headless)
            {
                refusal = Result.Unsupported;
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
            if (IsMuted(content)) return false;
            if (asking || BasisConnectionService.ConnectInProgress)
            {
                refusal = Result.Busy;
                return false;
            }
            return true;
        }

        public static bool IsMuted(BasisContentBase content)
        {
            return content != null && strikes.TryGetValue(content, out Strike strike) && (strike.Count >= DeclinesBeforeMute || Time.realtimeSinceStartup < strike.Until);
        }

        public static void Forgive(BasisContentBase content)
        {
            if (content != null) strikes.Remove(content);
        }

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

        private static void Request(ServerDirectoryEntry entry, string server, GameObject source, Action<Result> onResult)
        {
            if (!Admit(source, out BasisContentBase content, out Result refusal))
            {
                Deliver(onResult, refusal);
                return;
            }
            if (entry == null && !BasisNetworkConnection.LocalPlayerIsConnected)
            {
                Deliver(onResult, Result.NotConnected);
                return;
            }
            Ask ask = new Ask
            {
                Entry = entry,
                Server = server,
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
                BasisDebug.LogError($"[ServerRequests] Could not ask about a server change: {e}", BasisDebug.LogTag.Networking);
                Finish(ask, Result.Unsupported);
            }
        }

        private static void Show(Ask ask)
        {
            if (ask.Done) return;
            bool connect = ask.Entry != null;
            string title = BasisLocalization.Get(connect ? "serverRequests.connect.title" : "serverRequests.disconnect.title");
            string body = BasisLocalization.Get(connect ? "serverRequests.connect.body" : "serverRequests.disconnect.body", ask.Name, ask.Kind);
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
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(
                title,
                body,
                BasisLocalization.Get(connect ? "serverRequests.connect.accept" : "serverRequests.disconnect.accept"),
                BasisLocalization.Get("ui.cancel"),
                accepted =>
                {
                    answered = true;
                    Answer(ask, accepted);
                },
                false,
                BasisPanelSeverity.Caution,
                BasisNotificationCategory.Content);
            if (panel == null)
            {
                Defer(ask, title, body);
                return;
            }

            BasisMainMenu.Instance.Dialogue = panel;
            panel.CaptureOnClose = false;
            if (connect)
            {
                panel.ShowDetails(new List<BasisMenuDialoguePanel.DetailRow>
                {
                    new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("serverRequests.prompt.server"), ask.Server, true),
                });
            }
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
                ask.Server == null ? body : body + "\n" + ask.Server,
                AddressableAssets.Sprites.Network,
                reopen: () => Show(ask),
                onDismiss: () => Answer(ask, false),
                category: BasisNotificationCategory.Content);
        }

        private static void Answer(Ask ask, bool accepted)
        {
            if (ask.Done) return;
            Result result = accepted ? Ready(ask) : Result.Declined;
            Finish(ask, result);
            if (result == Result.Accepted) Begin(ask);
        }

        private static Result Ready(Ask ask)
        {
            if (BasisConnectionService.ConnectInProgress) return Result.Busy;
            if (ask.Entry == null) return BasisNetworkConnection.LocalPlayerIsConnected ? Result.Accepted : Result.NotConnected;
            string userName = BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty);
            if (string.IsNullOrWhiteSpace(userName) && BasisLocalPlayer.Instance != null) userName = BasisLocalPlayer.Instance.DisplayName;
            if (string.IsNullOrWhiteSpace(userName))
            {
                BasisConnectionService.ReportConnectionError(BasisLocalization.Get("menu.servers.deepLink.usernameRequired.body"));
                return Result.Unsupported;
            }
            ask.UserName = userName;
            return Result.Accepted;
        }

        private static async void Begin(Ask ask)
        {
            try
            {
                if (ask.Entry == null) await BasisConnectionService.DisconnectAsync();
                else await BasisConnectionService.ConnectAsync(ask.Entry, ask.UserName);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[ServerRequests] The server change failed: {e}", BasisDebug.LogTag.Networking);
            }
        }

        private static void Finish(Ask ask, Result result)
        {
            if (ask.Done) return;
            ask.Done = true;
            asking = false;
            if (result == Result.Declined) StrikeOut(ask.Content);
            else if (result == Result.Accepted) Forgive(ask.Content);
            BasisDebug.Log($"[ServerRequests] The {ask.Kind} \"{ask.Name}\" asked to {(ask.Entry == null ? "disconnect" : "move to " + ask.Server)}: {result}", BasisDebug.LogTag.Networking);
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
                BasisDebug.LogErrorUnreported($"[ServerRequests] A server request callback threw: {e}", BasisDebug.LogTag.Networking);
            }
        }

        private static string NameOf(BasisContentBase content)
        {
            string name = content.BasisBundleDescription != null ? content.BasisBundleDescription.AssetBundleName : null;
            if (string.IsNullOrWhiteSpace(name) && content is BasisScene) name = content.gameObject.scene.name;
            string clean = BasisExternalLinks.CleanName(name);
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
