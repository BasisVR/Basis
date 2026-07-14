using Basis.Network.Core;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking
{
    /// <summary>Browses Basis DNS-SD records through the shared core mDNS transport.</summary>
    internal sealed class BasisLanMdnsBrowser : IDisposable
    {
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Action<BasisLanAdvertisement, IPAddress> _found;
        private readonly Action<Guid> _removed;
        private readonly BasisLanMdnsTransport _transport;
        private bool _disposed;

        public BasisLanMdnsBrowser(Action<BasisLanAdvertisement, IPAddress> found, Action<Guid> removed)
        {
            _found = found ?? throw new ArgumentNullException(nameof(found));
            _removed = removed ?? throw new ArgumentNullException(nameof(removed));
            try
            {
                _transport = new BasisLanMdnsTransport(OnPacket);
            }
            catch
            {
                _cancellation.Dispose();
                throw;
            }
            _ = Task.Run(() => QueryLoopAsync(_cancellation.Token));
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                byte[] query = BasisLanMdnsWire.BuildQuery();
                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }
                        _transport.SendMulticast(query);
                    }
                    await Task.Delay(BasisLanMdnsWire.QueryIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BasisDebug.LogWarning($"LAN mDNS discovery stopped: {ex.Message}");
                }
            }
        }

        private void OnPacket(byte[] packet, IPEndPoint remote)
        {
            if (!BasisLanMdnsWire.TryParse(packet, out BasisLanMdnsWire.Message message) || !message.IsResponse)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                BasisLanMdnsWire.Extract(message, remote, _found, _removed);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                _transport.Dispose();
                _cancellation.Dispose();
            }
        }
    }
}
