using System;
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
        private ServiceDiscovery _serviceDiscovery;
        private Thread _httpThread;

        public OsushiQuery(Func<string, string> responseResolver)
        {
            _responseResolver = responseResolver;
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
                    StartHttpServer(httpPort, _responseResolver);
                }
                catch (ThreadAbortException)
                {
                    BasisDebug.LogError($"ThreadAbortException", BasisDebug.LogTag.LocalNetwork);
                }
                catch (ThreadInterruptedException)
                {
                    BasisDebug.LogError($"ThreadInterruptedException", BasisDebug.LogTag.LocalNetwork);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"HTTP server thread died: {e}", BasisDebug.LogTag.LocalNetwork);
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

            try
            {
                _httpThread.Interrupt();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"HTTP server stopped: {e}", BasisDebug.LogTag.LocalNetwork);
            }

            _httpThread = null;

            _serviceDiscovery.Dispose();
            _serviceDiscovery = null;
        }

        static int GetRandomFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        static void StartHttpServer(int port, Func<string, string> responseResolver)
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
            BasisDebug.Log($"HTTP server listening on http://localhost:{port}/", BasisDebug.LogTag.LocalNetwork);

            while (true)
            {
                try
                {
                    var ctx = listener.GetContext();
                    BasisDebug.Log($"HTTP request: {ctx.Request.RawUrl}", BasisDebug.LogTag.LocalNetwork);

                    var res = ctx.Response;
                    var json = responseResolver(ctx.Request.RawUrl);
                    var buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json";
                    res.ContentLength64 = buffer.Length;
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                    res.OutputStream.Close();
                }
                catch (HttpListenerException e)
                {
                    BasisDebug.LogError($"HttpListener closed: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                    break;
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[Request error (continuing): {e.Message}", BasisDebug.LogTag.LocalNetwork);
                }
            }
        }
    }
}
