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
        private readonly string _root;
        private readonly string _avtr;
        private bool _isStarted;
        private ServiceDiscovery _serviceDiscovery;
        private Thread _httpThread;

        public OsushiQuery(string root, string avtr)
        {
            _root = root;
            _avtr = avtr;
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
                    StartHttpServer(httpPort, _root, _avtr);
                }
                catch (Exception _)
                {
                    // ignored
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
            catch (Exception _)
            {
                // ignored
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

        static void StartHttpServer(int port, string root, string avtr)
        {
            if (!HttpListener.IsSupported) return;

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Console.WriteLine($"HTTP server listening on http://localhost:{port}/");

            while (true)
            {
                var ctx = listener.GetContext();
                Console.WriteLine($"HTTP request: {ctx.Request.RawUrl}");

                var res = ctx.Response;
                var json = ctx.Request.RawUrl.EndsWith("/avatar") ? avtr : root;
                var buffer = Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                res.ContentLength64 = buffer.Length;
                res.OutputStream.Write(buffer, 0, buffer.Length);
                res.OutputStream.Close();
            }
        }
    }
}
