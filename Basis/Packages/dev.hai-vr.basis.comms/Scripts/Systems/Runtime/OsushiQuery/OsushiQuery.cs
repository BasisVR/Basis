using Unity.Scripting.LifecycleManagement;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using Newtonsoft.Json.Linq;

namespace HVR.Osushi
{
    internal class OsushiQuery
    {
        private const string OscJsonService = "_oscjson._tcp";
        private const string OscService = "_osc._udp";
        private const string VrcftInstancePrefix = "VRCFT-";
        private readonly Func<string, string> _responseResolver;
        private readonly int _oscPort;
        [NoAutoStaticsCleanup] private static readonly HttpClient VrcftHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private volatile int _vrcftOscPort;
        private int _vrcftHttpPort;
        private bool _isStarted;
        private volatile bool _isStopping;
        private ServiceDiscovery _serviceDiscovery;
        private Thread _httpThread;
        private HttpListener _listener;
        private readonly object _serverLock = new object();

        public int VrcftOscPort => _vrcftOscPort;

        public OsushiQuery(Func<string, string> responseResolver, int oscPort)
        {
            _responseResolver = responseResolver ?? throw new ArgumentNullException(nameof(responseResolver));
            _oscPort = oscPort;
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

            string instanceName = $"VRChat-Client-{new Random().Next(100_000, 999_999)}";
            _serviceDiscovery = new ServiceDiscovery();
            _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            _serviceDiscovery.Advertise(new ServiceProfile(instanceName, OscJsonService, (ushort)httpPort, new[] { IPAddress.Loopback }));
            _serviceDiscovery.Advertise(new ServiceProfile(instanceName, OscService, (ushort)_oscPort, new[] { IPAddress.Loopback }));

            // The code above is enough so that VRCFaceTracking can detect us if we started before VRCFaceTracking.
            // We need this so that VRCFaceTracking can detect us if our code runs AFTER VRCFaceTracking has already started.
            Rediscover();
        }

        public void Rediscover()
        {
            try
            {
                _serviceDiscovery?.QueryServiceInstances(OscJsonService);
                _serviceDiscovery?.QueryServiceInstances(OscService);
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"OSCQuery discovery query failed: {e.Message}", BasisDebug.LogTag.LocalNetwork);
            }
        }

        private void OnServiceInstanceDiscovered(object sender, ServiceInstanceDiscoveryEventArgs e)
        {
            try
            {
                var labels = e.ServiceInstanceName?.Labels;
                if (labels == null || labels.Count < 3 || !labels[0].StartsWith(VrcftInstancePrefix, StringComparison.Ordinal)) return;

                var records = e.Message?.AdditionalRecords;
                var srv = records?.OfType<SRVRecord>().FirstOrDefault(r => r.Name == e.ServiceInstanceName) ?? records?.OfType<SRVRecord>().FirstOrDefault();
                if (srv == null || srv.Port == 0) return;

                if (labels[1] == "_osc" && labels[2] == "_udp")
                {
                    SetVrcftOscPort(srv.Port);
                }
                else if (labels[1] == "_oscjson" && labels[2] == "_tcp" && Interlocked.Exchange(ref _vrcftHttpPort, srv.Port) != srv.Port)
                {
                    _ = QueryVrcftHostInfoAsync(srv.Port);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"OSCQuery discovery answer ignored: {ex.Message}", BasisDebug.LogTag.LocalNetwork);
            }
        }

        private void SetVrcftOscPort(int port)
        {
            if (port <= 0 || port > 65535) return;
            _vrcftOscPort = port;
        }

        // VRChat queries the HOST_INFO of every OSCQuery peer it finds; VRCFaceTracking re-reads our avatar when that happens.
        private async System.Threading.Tasks.Task QueryVrcftHostInfoAsync(int httpPort)
        {
            try
            {
                string json = await VrcftHttp.GetStringAsync($"http://127.0.0.1:{httpPort}/?HOST_INFO").ConfigureAwait(false);
                if (JObject.Parse(json).TryGetValue("OSC_PORT", out JToken port) && port.Type == JTokenType.Integer)
                {
                    SetVrcftOscPort(port.Value<int>());
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref _vrcftHttpPort, 0, httpPort);
                if (!_isStopping)
                {
                    BasisDebug.LogWarning($"VRCFaceTracking HOST_INFO query failed: {e.Message}", BasisDebug.LogTag.LocalNetwork);
                }
            }
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

            if (_serviceDiscovery != null)
            {
                _serviceDiscovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
                _serviceDiscovery.Dispose();
            }
            _vrcftOscPort = 0;
            _vrcftHttpPort = 0;
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
                                var json = _responseResolver(ctx.Request.RawUrl);
                                if (json == null)
                                {
                                    res.StatusCode = 404;
                                    json = "{}";
                                }
                                var buffer = Encoding.UTF8.GetBytes(json);
                                res.ContentType = "application/json";
                                res.AddHeader("Pragma", "no-cache");
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
