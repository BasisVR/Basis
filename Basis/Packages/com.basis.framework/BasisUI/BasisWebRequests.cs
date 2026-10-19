using Basis.Scripts.BasisSdk;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Networking;
using UrlSecurity = Basis.Scripts.Common.BasisUrlSecurity;

namespace Basis.BasisUI
{
    /// <summary>
    /// Web requests on behalf of world and prop scripts (BasisWebRequestShim). Every request is
    /// https on port 443 to a named public host, re-checked against DNS before each hop, and the
    /// player approves each site per content for the session. No cookies survive a request, no
    /// headers but Content-Type are sent, and redirects may only stay on the approved host.
    /// Results always arrive on a later frame, never inside the caller's own call.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisWebRequests
    {
        public enum Status
        {
            Ok = 0,
            HttpError = 1,
            InvalidUrl = 2,
            Blocked = 3,
            Declined = 4,
            Busy = 5,
            TooLarge = 6,
            TimedOut = 7,
            NetworkError = 8,
            NotAllowed = 9,
            Unsupported = 10,
        }

        public struct Reply
        {
            public Status Status;
            public int Code;
            public byte[] Body;
        }

        public const int MaxUrlLength = 2048;
        public const int MaxUploadBytes = 256 * 1024;
        public const int MaxDownloadBytes = 4 * 1024 * 1024;
        public const float TimeoutSeconds = 15f;
        public const int MaxRedirects = 3;
        public const int MaxInFlightPerOwner = 4;
        public const int MaxInFlight = 16;
        public const int MaxRequestsPerMinute = 30;
        public const int MaxWaitingPerAsk = 8;
        public const int MaxQueuedAsks = 8;
        public const float ArmSeconds = 1f;
        public const float DeclineCooldownSeconds = 30f;
        public const int DeclinesBeforeMute = 3;
        public const int ReceiveChunkBytes = 16 * 1024;

        [NoAutoStaticsCleanup] private static readonly string[] ContentTypes =
        {
            "application/octet-stream",
            "application/json",
            "text/plain",
            "application/x-www-form-urlencoded",
        };

        private sealed class Job
        {
            public UnityEngine.Object Owner;
            public string OwnerKey;
            public Uri Uri;
            public byte[] Body;
            public string ContentType;
            public Action<Reply> Done;
        }

        private sealed class Ask
        {
            public string Key;
            public string OwnerKey;
            public string Host;
            public string Name;
            public string Kind;
            public readonly List<Job> Waiting = new List<Job>();
            public bool Done;
        }

        private sealed class Budget
        {
            public int InFlight;
            public float WindowStart;
            public int WindowCount;
        }

        private sealed class Strike
        {
            public int Count;
            public float Until;
        }

        private static readonly HashSet<string> approved = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> declined = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Strike> strikes = new Dictionary<string, Strike>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Budget> budgets = new Dictionary<string, Budget>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Ask> asks = new Dictionary<string, Ask>(StringComparer.Ordinal);
        private static readonly Queue<Ask> queue = new Queue<Ask>();
        private static int inFlight;
        private static bool showing;

        /// <summary>
        /// True when <paramref name="url"/> is something a script may request: absolute https on
        /// the default port, no user info, a host name (never an IP literal) that is not local.
        /// </summary>
        public static bool TryParse(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)) return false;
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
            if (parsed.HostNameType != UriHostNameType.Dns || string.IsNullOrEmpty(parsed.IdnHost) || parsed.IdnHost.IndexOf('.') < 0) return false;
            if (!UrlSecurity.IsHttpUrlAllowed(parsed.AbsoluteUri, out _)) return false;
            uri = parsed;
            return true;
        }

        public static bool IsAllowedContentType(string contentType)
        {
            for (int i = 0; i < ContentTypes.Length; i++)
            {
                if (string.Equals(ContentTypes[i], contentType, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Starts a request. <paramref name="ownerKey"/> identifies the content (approvals and
        /// budgets are per owner key and host); <paramref name="source"/> must resolve to a world
        /// or prop. <paramref name="body"/> null means GET, otherwise POST. <paramref name="done"/>
        /// runs on a later frame, and not at all once <paramref name="owner"/> is destroyed.
        /// </summary>
        public static void Send(UnityEngine.Object owner, string ownerKey, GameObject source, string url, byte[] body, string contentType, Action<Reply> done)
        {
            Job job = new Job { Owner = owner, OwnerKey = ownerKey, Done = done };
            if (Headless)
            {
                Refuse(job, Status.Unsupported);
                return;
            }
            if (owner == null || string.IsNullOrEmpty(ownerKey) || source == null || !BasisContent.TryResolve(source, out BasisContentBase content) || content is BasisAvatar)
            {
                Refuse(job, Status.NotAllowed);
                return;
            }
            if (!TryParse(url, out Uri uri))
            {
                Refuse(job, Status.InvalidUrl);
                return;
            }
            if (body != null)
            {
                if (body.Length > MaxUploadBytes)
                {
                    Refuse(job, Status.TooLarge);
                    return;
                }
                if (!IsAllowedContentType(contentType))
                {
                    Refuse(job, Status.InvalidUrl);
                    return;
                }
                job.Body = (byte[])body.Clone();
                job.ContentType = contentType;
            }
            job.Uri = uri;

            string key = ApprovalKey(ownerKey, uri.IdnHost);
            bool known = approved.Contains(key);
            if (declined.Contains(key) || (!known && IsMuted(ownerKey)))
            {
                Refuse(job, Status.Declined);
                return;
            }
            if (!Spend(ownerKey))
            {
                Refuse(job, Status.Busy);
                return;
            }
            if (known)
            {
                Run(job);
                return;
            }
            Enqueue(job, key, ownerKey, uri.IdnHost, content);
        }

        public static bool IsMuted(string ownerKey)
        {
            return ownerKey != null && strikes.TryGetValue(ownerKey, out Strike strike) && (strike.Count >= DeclinesBeforeMute || Time.realtimeSinceStartup < strike.Until);
        }

        public static void Forgive(string ownerKey)
        {
            if (ownerKey != null) strikes.Remove(ownerKey);
        }

        internal static void StrikeOut(string ownerKey)
        {
            if (ownerKey == null) return;
            if (!strikes.TryGetValue(ownerKey, out Strike strike))
            {
                strike = new Strike();
                strikes[ownerKey] = strike;
            }
            strike.Count++;
            strike.Until = Time.realtimeSinceStartup + DeclineCooldownSeconds;
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

        private static string ApprovalKey(string ownerKey, string host) => ownerKey + "\n" + host.ToLowerInvariant();

        private static bool Spend(string ownerKey)
        {
            if (inFlight >= MaxInFlight) return false;
            if (!budgets.TryGetValue(ownerKey, out Budget budget))
            {
                budget = new Budget { WindowStart = Time.realtimeSinceStartup };
                budgets[ownerKey] = budget;
            }
            float now = Time.realtimeSinceStartup;
            if (now - budget.WindowStart >= 60f)
            {
                budget.WindowStart = now;
                budget.WindowCount = 0;
            }
            if (budget.InFlight >= MaxInFlightPerOwner || budget.WindowCount >= MaxRequestsPerMinute) return false;
            budget.WindowCount++;
            budget.InFlight++;
            inFlight++;
            return true;
        }

        private static void Release(Job job)
        {
            if (inFlight > 0) inFlight--;
            if (budgets.TryGetValue(job.OwnerKey, out Budget budget) && budget.InFlight > 0) budget.InFlight--;
        }

        // ------------------------------------------------------------------ approval

        private static void Enqueue(Job job, string key, string ownerKey, string host, BasisContentBase content)
        {
            if (asks.TryGetValue(key, out Ask pending))
            {
                if (pending.Waiting.Count >= MaxWaitingPerAsk)
                {
                    Release(job);
                    Refuse(job, Status.Busy);
                    return;
                }
                pending.Waiting.Add(job);
                return;
            }
            if (asks.Count >= MaxQueuedAsks)
            {
                Release(job);
                Refuse(job, Status.Busy);
                return;
            }
            Ask ask = new Ask { Key = key, OwnerKey = ownerKey, Host = host, Name = NameOf(content), Kind = KindOf(content) };
            ask.Waiting.Add(job);
            asks[key] = ask;
            queue.Enqueue(ask);
            Pump();
        }

        private static void Pump()
        {
            if (showing || queue.Count == 0) return;
            Ask ask = queue.Dequeue();
            if (IsMuted(ask.OwnerKey))
            {
                Settle(ask, false);
                return;
            }
            showing = true;
            try
            {
                Show(ask);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[WebRequests] Could not ask about a site: {e}", BasisDebug.LogTag.Networking);
                Answer(ask, false);
            }
        }

        private static void Show(Ask ask)
        {
            if (ask.Done) return;
            string title = BasisLocalization.Get("webRequests.prompt.title");
            string body = BasisLocalization.Get("webRequests.prompt.body", ask.Name, ask.Kind);
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
                BasisLocalization.Get("webRequests.prompt.allow"),
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
            panel.ShowDetails(new List<BasisMenuDialoguePanel.DetailRow>
            {
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("webRequests.prompt.site"), ask.Host, true),
            });
            Arm(panel.AcceptButton);
            panel.OnInstanceReleased += () =>
            {
                if (!answered) Defer(ask, title, body);
            };
        }

        private static void Defer(Ask ask, string title, string body)
        {
            if (ask.Done) return;
            showing = false;
            BasisNotificationCenter.AddPending(
                title,
                body + "\n" + ask.Host,
                AddressableAssets.Sprites.Link,
                reopen: () => Show(ask),
                onDismiss: () => Answer(ask, false),
                category: BasisNotificationCategory.Content);
            Pump();
        }

        private static void Answer(Ask ask, bool accepted)
        {
            if (ask.Done) return;
            (accepted ? approved : declined).Add(ask.Key);
            if (accepted) Forgive(ask.OwnerKey);
            else StrikeOut(ask.OwnerKey);
            BasisDebug.Log($"[WebRequests] The {ask.Kind} \"{ask.Name}\" asked to use {ask.Host}: {(accepted ? "allowed" : "declined")}", BasisDebug.LogTag.Networking);
            Settle(ask, accepted);
        }

        private static void Settle(Ask ask, bool accepted)
        {
            if (ask.Done) return;
            ask.Done = true;
            showing = false;
            asks.Remove(ask.Key);
            for (int i = 0; i < ask.Waiting.Count; i++)
            {
                Job job = ask.Waiting[i];
                if (accepted && job.Owner != null)
                {
                    Run(job);
                }
                else
                {
                    Release(job);
                    Refuse(job, Status.Declined);
                }
            }
            ask.Waiting.Clear();
            Pump();
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

        // ------------------------------------------------------------------ transfer

        private static async void Run(Job job)
        {
            Reply reply = new Reply { Status = Status.NetworkError };
            try
            {
                reply = await Transfer(job);
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[WebRequests] A request to {job.Uri.IdnHost} failed: {e.Message}", BasisDebug.LogTag.Networking);
            }
            Release(job);
            Deliver(job, reply);
        }

        private static async Awaitable<Reply> Transfer(Job job)
        {
            Uri target = job.Uri;
            byte[] body = job.Body;
            for (int hop = 0; ; hop++)
            {
                string dns = await UrlSecurity.ValidateResolvedHostAsync(target.AbsoluteUri);
                if (dns != null) return new Reply { Status = Status.Blocked };
                if (job.Owner == null) return new Reply { Status = Status.NotAllowed };

                Uri hostRoot = new Uri(Uri.UriSchemeHttps + "://" + target.IdnHost + "/");
                UnityWebRequest.ClearCookieCache(hostRoot);
                using (UnityWebRequest www = Build(target, body, job.ContentType, out CappedDownload download))
                {
                    UnityWebRequestAsyncOperation op = www.SendWebRequest();
                    float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                    while (!op.isDone)
                    {
                        if (download.Exceeded || www.downloadedBytes > MaxDownloadBytes || ClaimsTooMuch(www))
                        {
                            www.Abort();
                            UnityWebRequest.ClearCookieCache(hostRoot);
                            return new Reply { Status = Status.TooLarge };
                        }
                        if (Time.realtimeSinceStartup > deadline)
                        {
                            www.Abort();
                            UnityWebRequest.ClearCookieCache(hostRoot);
                            return new Reply { Status = Status.TimedOut };
                        }
                        if (job.Owner == null)
                        {
                            www.Abort();
                            UnityWebRequest.ClearCookieCache(hostRoot);
                            return new Reply { Status = Status.NotAllowed };
                        }
                        await Awaitable.NextFrameAsync();
                    }
                    UnityWebRequest.ClearCookieCache(hostRoot);

                    int code = (int)www.responseCode;
                    if (download.Exceeded) return new Reply { Status = Status.TooLarge, Code = code };
                    if (code >= 300 && code < 400 && code != 304)
                    {
                        if (hop >= MaxRedirects || !TryRedirect(target, www.GetResponseHeader("Location"), job.Uri.IdnHost, out Uri next)) return new Reply { Status = Status.Blocked, Code = code };
                        if (code == 303 || ((code == 301 || code == 302) && body != null)) body = null;
                        target = next;
                        continue;
                    }
                    if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.DataProcessingError)
                    {
                        return new Reply { Status = www.error != null && www.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ? Status.TimedOut : Status.NetworkError, Code = code };
                    }
                    return new Reply
                    {
                        Status = www.result == UnityWebRequest.Result.Success ? Status.Ok : Status.HttpError,
                        Code = code,
                        Body = download.Take(),
                    };
                }
            }
        }

        private static UnityWebRequest Build(Uri target, byte[] body, string contentType, out CappedDownload download)
        {
            download = new CappedDownload();
            UnityWebRequest www = new UnityWebRequest(target, body == null ? UnityWebRequest.kHttpVerbGET : UnityWebRequest.kHttpVerbPOST)
            {
                downloadHandler = download,
                redirectLimit = 0,
                timeout = (int)TimeoutSeconds,
                useHttpContinue = false,
                disposeDownloadHandlerOnDispose = true,
                disposeUploadHandlerOnDispose = true,
            };
            if (body != null)
            {
                www.uploadHandler = new UploadHandlerRaw(body) { contentType = contentType };
                www.SetRequestHeader("Content-Type", contentType);
            }
            return www;
        }

        private static bool ClaimsTooMuch(UnityWebRequest www)
        {
            string length = www.GetResponseHeader("Content-Length");
            return length != null && long.TryParse(length, out long claimed) && claimed > MaxDownloadBytes;
        }

        internal sealed class CappedDownload : DownloadHandlerScript
        {
            private byte[] received = Array.Empty<byte>();
            private int length;

            public CappedDownload() : base(new byte[ReceiveChunkBytes]) { }

            public bool Exceeded { get; private set; }

            public int Length => length;

            internal bool Accept(byte[] data, int dataLength)
            {
                if (Exceeded) return false;
                if (dataLength <= 0) return true;
                if (data == null || dataLength > data.Length) return false;
                if (dataLength > MaxDownloadBytes - length)
                {
                    Exceeded = true;
                    return false;
                }
                int needed = length + dataLength;
                if (needed > received.Length) Array.Resize(ref received, Math.Min(MaxDownloadBytes, Math.Max(needed, received.Length * 2)));
                Buffer.BlockCopy(data, 0, received, length, dataLength);
                length = needed;
                return true;
            }

            internal void Expect(ulong contentLength)
            {
                if (contentLength > MaxDownloadBytes)
                {
                    Exceeded = true;
                    return;
                }
                if ((int)contentLength > received.Length) Array.Resize(ref received, (int)contentLength);
            }

            public byte[] Take()
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(received, 0, copy, 0, length);
                return copy;
            }

            protected override bool ReceiveData(byte[] data, int dataLength) => Accept(data, dataLength);

            protected override void ReceiveContentLengthHeader(ulong contentLength) => Expect(contentLength);

            protected override byte[] GetData() => Take();
        }

        /// <summary>A redirect may only go to another https address on the host the player approved.</summary>
        public static bool TryRedirect(Uri from, string location, string approvedHost, out Uri next)
        {
            next = null;
            if (from == null || string.IsNullOrWhiteSpace(location) || location.Length > MaxUrlLength) return false;
            if (!Uri.TryCreate(from, location, out Uri resolved)) return false;
            if (!TryParse(resolved.AbsoluteUri, out Uri parsed)) return false;
            if (!string.Equals(parsed.IdnHost, approvedHost, StringComparison.OrdinalIgnoreCase)) return false;
            next = parsed;
            return true;
        }

        // ------------------------------------------------------------------ delivery

        private static void Refuse(Job job, Status status)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() => Deliver(job, new Reply { Status = status }));
        }

        private static void Deliver(Job job, Reply reply)
        {
            if (job.Done == null || job.Owner == null) return;
            try
            {
                job.Done(reply);
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[WebRequests] A request callback threw: {e}", BasisDebug.LogTag.Networking);
            }
        }
    }
}
