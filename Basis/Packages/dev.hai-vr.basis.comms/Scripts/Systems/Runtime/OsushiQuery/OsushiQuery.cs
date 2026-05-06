using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MeaMod.DNS.Multicast;

namespace HVR.Osushi
{
    internal class OsushiQuery
    {
        private readonly Func<string, string> _responseResolver;
        private bool _isStarted;
        private volatile bool _isStopping;
        private ServiceDiscovery _serviceDiscovery;
        private Thread _httpThread;
        private HttpListener _listener;
        private readonly object _serverLock = new object();

        public OsushiQuery(Func<string, string> responseResolver)
        {
            _responseResolver = responseResolver ?? throw new ArgumentNullException(nameof(responseResolver));
        }

        public void Start()
        {
            if (_isStarted) return;

            _isStarted = true;

            var httpPort = GetRandomFreePort();

            _httpThread = new Thread(() =>
            {
                try
                {
                    StartHttpServer(httpPort);
                }
                catch (ThreadAbortException)
                {
                    // Expected during script/domain reload and shutdown on Mono.
                }
                catch (ThreadInterruptedException)
                {
                    if (!_isStopping)
                    {
                        BasisDebug.LogError("ThreadInterruptedException", BasisDebug.LogTag.LocalNetwork);
                    }
                }
                catch (Exception e)
                {
                    if (!_isStopping)
                    {
                        BasisDebug.LogError($"HTTP server thread died: {e}", BasisDebug.LogTag.LocalNetwork);
                    }
                }
            });
            _httpThread.IsBackground = true;
            _httpThread.Start();

            var oscQueryService = new ServiceProfile(
                instanceName: $"VRChat-Client-{new Random().Next(100_000, 999_999)}",
                serviceName: "_oscjson._tcp",
                port: (ushort)httpPort,
                addresses: new[] { IPAddress.Loopback }
            );

            _serviceDiscovery = new ServiceDiscovery();
            _serviceDiscovery.Advertise(oscQueryService);

            // The code above is enough so that VRCFaceTracking can detect us if we started before VRCFaceTracking.
            // We need this so that VRCFaceTracking can detect us if our code runs AFTER VRCFaceTracking has already started.
            _serviceDiscovery.QueryServiceInstances("_oscjson._tcp");
        }

        public void Stop()
        {
            if (!_isStarted) return;
            _isStarted = false;
            _isStopping = true;

            try
            {
                lock (_serverLock)
                {
                    _listener?.Close();
                    _listener = null;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"HTTP server stop failed: {e}", BasisDebug.LogTag.LocalNetwork);
            }

            try
            {
                if (_httpThread != null && _httpThread.IsAlive)
                {
                    _httpThread.Join(250);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"HTTP server join failed: {e}", BasisDebug.LogTag.LocalNetwork);
            }

            _httpThread = null;

            _serviceDiscovery?.Dispose();
            _serviceDiscovery = null;
            _isStopping = false;
        }

        static int GetRandomFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private void StartHttpServer(int port)
        {
            if (!HttpListener.IsSupported)
            {
                BasisDebug.LogError($"HttpListener.IsSupported was false", BasisDebug.LogTag.LocalNetwork);
                return;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            lock (_serverLock)
            {
                _listener = listener;
            }
            BasisDebug.Log($"HTTP server listening on http://localhost:{port}/", BasisDebug.LogTag.LocalNetwork);

            try
            {
                while (!_isStopping)
                {
                    try
                    {
                        var ctx = listener.GetContext();
                        BasisDebug.Log($"HTTP request: {ctx.Request.RawUrl}", BasisDebug.LogTag.LocalNetwork);

                        try
                        {
                            using (var res = ctx.Response)
                            {
                                var json = _responseResolver(ctx.Request.RawUrl) ?? "{}";
                                var buffer = Encoding.UTF8.GetBytes(json);
                                res.ContentType = "application/json";
                                res.ContentLength64 = buffer.Length;
                                res.OutputStream.Write(buffer, 0, buffer.Length);
                            }
                        }
                        catch (HttpListenerException e)
                        {
                            if (!_isStopping)
                            {
                                BasisDebug.Log($"HTTP client disconnected before response completed: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                            }
                        }
                        catch (IOException e)
                        {
                            if (!_isStopping)
                            {
                                BasisDebug.Log($"HTTP client disconnected during response write: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                            }
                        }
                        catch (ObjectDisposedException e)
                        {
                            if (!_isStopping)
                            {
                                BasisDebug.Log($"HTTP response disposed before write completed: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                            }
                        }
                    }
                    catch (ThreadAbortException)
                    {
                        // Expected during script/domain reload and shutdown on Mono.
                        break;
                    }
                    catch (HttpListenerException e)
                    {
                        if (!_isStopping)
                        {
                            BasisDebug.LogError($"HttpListener closed unexpectedly: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                        }
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (!_isStopping)
                        {
                            BasisDebug.LogError("HttpListener disposed unexpectedly", BasisDebug.LogTag.LocalNetwork);
                        }
                        break;
                    }
                    catch (Exception e)
                    {
                        if (!_isStopping)
                        {
                            BasisDebug.LogError($"Request error (continuing): {e.Message}", BasisDebug.LogTag.LocalNetwork);
                        }
                    }
                }
            }
            finally
            {
                lock (_serverLock)
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                    }
                }

                listener.Close();
            }
        }
    }
}
