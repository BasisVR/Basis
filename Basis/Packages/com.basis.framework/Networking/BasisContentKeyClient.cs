using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using System;
using System.Threading;
using System.Threading.Tasks;
namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public static partial class BasisContentKeyClient
    {
        [NoAutoStaticsCleanup] private static BasisContentKeyPeer peer;
        public static void Initialize()
        {
            BasisBeeKeyGrant.Resolver = ResolveAsync;
            BasisClientMessageRegistry.RegisterClientPlugin(BasisContentKeyProtocol.MessageName, Handle);
            BasisClientMessageRegistry.OnSupplyApplied -= Announce;
            BasisClientMessageRegistry.OnSupplyApplied += Announce;
        }
        private static void Announce() => Volatile.Read(ref peer)?.Announce();
        public static void Reset()
        {
            Interlocked.Exchange(ref peer, null)?.Dispose();
            BasisBeeKeyGrant.Clear();
        }
        public static string Share(string url, string versionTag, string location, string password)
        {
            if (BasisNetworkManagement.SendsUnlockPasswords || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password) || BasisEncryptionWrapper.IsKeyRequest(password)) return password;
            byte[] contentId = BasisContentKeyProtocol.ContentId(url, versionTag);
            string source = string.IsNullOrEmpty(location) ? url : location;
            Current.Own(contentId, () => BuildOnMainThread(source, password, versionTag));
            return BasisContentKeyProtocol.Marker(contentId);
        }
        public static string Tagged(string password, ushort senderId) => BasisContentKeyProtocol.WithOrigin(password, senderId);
        public static void TagSender(BasisLoadableBundle bundle, ushort senderId)
        {
            if (bundle != null) bundle.UnlockPassword = Tagged(bundle.UnlockPassword, senderId);
        }
        public static async Task<string> ResolvePasswordAsync(string url, string versionTag, string password, CancellationToken cancellationToken)
        {
            if (!BasisEncryptionWrapper.IsKeyRequest(password)) return password;
            if (!BasisContentKeyProtocol.TryParseMarker(password, out ushort origin, out byte[] contentId)) return null;
            contentId ??= BasisContentKeyProtocol.ContentId(url, versionTag);
            Task<byte[]> pending = Current.ResolveAsync(origin, contentId);
            if (cancellationToken.CanBeCanceled && await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, cancellationToken)) != pending) return null;
            byte[] grant = await pending;
            if (grant == null) BasisDebug.LogWarning($"Nobody in this session could share the keys for {url}.", BasisDebug.LogTag.Networking);
            return BasisContentKeyProtocol.Token(grant);
        }
        private static BasisContentKeyPeer Current
        {
            get
            {
                BasisContentKeyPeer current = Volatile.Read(ref peer);
                if (current != null) return current;
                BasisContentKeyPeer created = new BasisContentKeyPeer(write => BasisClientMessageRegistry.Send(BasisContentKeyProtocol.MessageName, write), work => BasisTasks.Run(work));
                current = Interlocked.CompareExchange(ref peer, created, null);
                if (current == null) return created;
                created.Dispose();
                return current;
            }
        }
        private static Task<string> ResolveAsync(BasisLoadableBundle bundle, CancellationToken cancellationToken) => ResolvePasswordAsync(bundle.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation, bundle.BasisRemoteBundleEncrypted?.RemoteVersionTag, bundle.UnlockPassword, cancellationToken);
        private static Task<byte[]> BuildOnMainThread(string location, string password, string versionTag)
        {
            TaskCompletionSource<byte[]> built = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                try
                {
                    (byte[] grant, string error) = await BasisBeeKeyGrant.BuildAsync(location, password, versionTag);
                    if (grant == null) BasisDebug.LogWarning($"Could not prepare the keys to share {location}: {error}", BasisDebug.LogTag.Networking);
                    built.TrySetResult(grant);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"Could not prepare the keys to share {location}: {ex.Message}", BasisDebug.LogTag.Networking);
                    built.TrySetResult(null);
                }
            });
            return built.Task;
        }
        private static void Handle(NetPeer sender, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                Volatile.Read(ref peer)?.Handle(reader);
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
