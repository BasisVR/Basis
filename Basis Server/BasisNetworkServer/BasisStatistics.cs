using LiteNetLib;
using System;
using System.Threading;

namespace Basis.Network.Server
{
    public static class BasisStatistics
    {
        public static NetManager Manager;
        private static Thread workerThread;
        private static volatile bool keepPolling = true; // Used to control the thread lifecycle

        public static void StartWorkerThread(NetManager manager)
        {
          //  Manager = manager;

            // Start the worker thread
           // workerThread = new Thread(PollStatistics);
          //  workerThread.IsBackground = true; // Background thread will be killed automatically when the main application ends
          //  workerThread.Start();
        }

        public static void StopWorkerThread()
        {
          //  keepPolling = false;

          // Wait for the worker thread to finish gracefully
           // if (workerThread != null && workerThread.IsAlive)
          //  {
            //    workerThread.Join();
           // }
        }

        private static void PollStatistics()
        {
           /// while (keepPolling)
           // {
           //     // Poll the statistics from the manager
             //   PollLatestStatistics();

            //    // Wait for some time before polling again (e.g., every second)
             //   Thread.Sleep(15000); // You can adjust the delay as needed
        //    }
        }

        public static void PollLatestStatistics()
        {
        //  BNL.Log("Packet Loss: " + Manager.Statistics.PacketLoss + "Packet Loss Percent: " + Manager.Statistics.PacketLossPercent + "Bytes Received: " + Manager.Statistics.BytesReceived + "Bytes Sent: " + Manager.Statistics.BytesSent + "Packets Sent: " + Manager.Statistics.PacketsSent);
        }
    }
}
