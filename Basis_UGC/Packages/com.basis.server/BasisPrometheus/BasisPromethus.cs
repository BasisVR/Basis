using Prometheus;

namespace BasisPrometheus
{
    public static class BasisPromethus
    {
        private static KestrelMetricServer _server;

        public static void StartPrometheus(int port = 1234)
        {
            if (_server == null)
            {
                _server = new KestrelMetricServer(port: port);
                _server.Start();
                Console.WriteLine($"Prometheus metrics server started on port {port}");
            }
            else
            {
                Console.WriteLine("Prometheus metrics server is already running.");
            }
        }

        public static void StopPrometheus()
        {
            if (_server != null)
            {
                _server.Stop();
                Console.WriteLine("Prometheus metrics server stopped.");
                _server = null;
            }
        }
    }
}