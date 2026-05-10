using System.Diagnostics;
using System.Threading;

namespace Basis.Scripts.Networking.Steam
{
    public readonly struct BasisSteamTransportMetricsSnapshot
    {
        public readonly long ReceivePollCount;
        public readonly long ReceiveMessageCount;
        public readonly long ReceiveBudgetUsed;
        public readonly long ReceiveBudgetCapacity;
        public readonly long ReceiveMessageBudgetHits;
        public readonly long ReceiveTimeBudgetHits;
        public readonly int CurrentPendingConnections;
        public readonly int PeakPendingConnections;
        public readonly long SendFailureCount;
        public readonly long SentPacketsTransient;
        public readonly long SentPacketsControl;
        public readonly long SentPacketsResource;
        public readonly long SentBytesTransient;
        public readonly long SentBytesControl;
        public readonly long SentBytesResource;
        public readonly long ReceivedPacketsTransient;
        public readonly long ReceivedPacketsControl;
        public readonly long ReceivedPacketsResource;
        public readonly long ReceivedBytesTransient;
        public readonly long ReceivedBytesControl;
        public readonly long ReceivedBytesResource;

        public BasisSteamTransportMetricsSnapshot(
            long receivePollCount,
            long receiveMessageCount,
            long receiveBudgetUsed,
            long receiveBudgetCapacity,
            long receiveMessageBudgetHits,
            long receiveTimeBudgetHits,
            int currentPendingConnections,
            int peakPendingConnections,
            long sendFailureCount,
            long sentPacketsTransient,
            long sentPacketsControl,
            long sentPacketsResource,
            long sentBytesTransient,
            long sentBytesControl,
            long sentBytesResource,
            long receivedPacketsTransient,
            long receivedPacketsControl,
            long receivedPacketsResource,
            long receivedBytesTransient,
            long receivedBytesControl,
            long receivedBytesResource)
        {
            ReceivePollCount = receivePollCount;
            ReceiveMessageCount = receiveMessageCount;
            ReceiveBudgetUsed = receiveBudgetUsed;
            ReceiveBudgetCapacity = receiveBudgetCapacity;
            ReceiveMessageBudgetHits = receiveMessageBudgetHits;
            ReceiveTimeBudgetHits = receiveTimeBudgetHits;
            CurrentPendingConnections = currentPendingConnections;
            PeakPendingConnections = peakPendingConnections;
            SendFailureCount = sendFailureCount;
            SentPacketsTransient = sentPacketsTransient;
            SentPacketsControl = sentPacketsControl;
            SentPacketsResource = sentPacketsResource;
            SentBytesTransient = sentBytesTransient;
            SentBytesControl = sentBytesControl;
            SentBytesResource = sentBytesResource;
            ReceivedPacketsTransient = receivedPacketsTransient;
            ReceivedPacketsControl = receivedPacketsControl;
            ReceivedPacketsResource = receivedPacketsResource;
            ReceivedBytesTransient = receivedBytesTransient;
            ReceivedBytesControl = receivedBytesControl;
            ReceivedBytesResource = receivedBytesResource;
        }
    }

    public static class BasisSteamTransportMetrics
    {
        private static long receivePollCount;
        private static long receiveMessageCount;
        private static long receiveBudgetUsed;
        private static long receiveBudgetCapacity;
        private static long receiveMessageBudgetHits;
        private static long receiveTimeBudgetHits;
        private static int currentPendingConnections;
        private static int peakPendingConnections;
        private static long sendFailureCount;
        private static long sentPacketsTransient;
        private static long sentPacketsControl;
        private static long sentPacketsResource;
        private static long sentBytesTransient;
        private static long sentBytesControl;
        private static long sentBytesResource;
        private static long receivedPacketsTransient;
        private static long receivedPacketsControl;
        private static long receivedPacketsResource;
        private static long receivedBytesTransient;
        private static long receivedBytesControl;
        private static long receivedBytesResource;

        public static BasisSteamTransportMetricsSnapshot GetSnapshot()
        {
            return new BasisSteamTransportMetricsSnapshot(
                Interlocked.Read(ref receivePollCount),
                Interlocked.Read(ref receiveMessageCount),
                Interlocked.Read(ref receiveBudgetUsed),
                Interlocked.Read(ref receiveBudgetCapacity),
                Interlocked.Read(ref receiveMessageBudgetHits),
                Interlocked.Read(ref receiveTimeBudgetHits),
                Volatile.Read(ref currentPendingConnections),
                Volatile.Read(ref peakPendingConnections),
                Interlocked.Read(ref sendFailureCount),
                Interlocked.Read(ref sentPacketsTransient),
                Interlocked.Read(ref sentPacketsControl),
                Interlocked.Read(ref sentPacketsResource),
                Interlocked.Read(ref sentBytesTransient),
                Interlocked.Read(ref sentBytesControl),
                Interlocked.Read(ref sentBytesResource),
                Interlocked.Read(ref receivedPacketsTransient),
                Interlocked.Read(ref receivedPacketsControl),
                Interlocked.Read(ref receivedPacketsResource),
                Interlocked.Read(ref receivedBytesTransient),
                Interlocked.Read(ref receivedBytesControl),
                Interlocked.Read(ref receivedBytesResource));
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Reset()
        {
            Interlocked.Exchange(ref receivePollCount, 0);
            Interlocked.Exchange(ref receiveMessageCount, 0);
            Interlocked.Exchange(ref receiveBudgetUsed, 0);
            Interlocked.Exchange(ref receiveBudgetCapacity, 0);
            Interlocked.Exchange(ref receiveMessageBudgetHits, 0);
            Interlocked.Exchange(ref receiveTimeBudgetHits, 0);
            Interlocked.Exchange(ref currentPendingConnections, 0);
            Interlocked.Exchange(ref peakPendingConnections, 0);
            Interlocked.Exchange(ref sendFailureCount, 0);
            Interlocked.Exchange(ref sentPacketsTransient, 0);
            Interlocked.Exchange(ref sentPacketsControl, 0);
            Interlocked.Exchange(ref sentPacketsResource, 0);
            Interlocked.Exchange(ref sentBytesTransient, 0);
            Interlocked.Exchange(ref sentBytesControl, 0);
            Interlocked.Exchange(ref sentBytesResource, 0);
            Interlocked.Exchange(ref receivedPacketsTransient, 0);
            Interlocked.Exchange(ref receivedPacketsControl, 0);
            Interlocked.Exchange(ref receivedPacketsResource, 0);
            Interlocked.Exchange(ref receivedBytesTransient, 0);
            Interlocked.Exchange(ref receivedBytesControl, 0);
            Interlocked.Exchange(ref receivedBytesResource, 0);
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void RecordReceivePoll(int processedMessages, int budgetCapacity, bool hitMessageBudget, bool hitTimeBudget)
        {
            Interlocked.Increment(ref receivePollCount);
            Interlocked.Add(ref receiveMessageCount, processedMessages);
            Interlocked.Add(ref receiveBudgetUsed, processedMessages);
            Interlocked.Add(ref receiveBudgetCapacity, budgetCapacity);

            if (hitMessageBudget)
            {
                Interlocked.Increment(ref receiveMessageBudgetHits);
            }

            if (hitTimeBudget)
            {
                Interlocked.Increment(ref receiveTimeBudgetHits);
            }
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void RecordPendingConnections(int count)
        {
            Interlocked.Exchange(ref currentPendingConnections, count);

            int observedPeak;
            do
            {
                observedPeak = Volatile.Read(ref peakPendingConnections);
                if (count <= observedPeak)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref peakPendingConnections, count, observedPeak) != observedPeak);
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void RecordSendSuccess(byte steamLane, int bytes)
        {
            switch (steamLane)
            {
                case 0:
                    Interlocked.Increment(ref sentPacketsTransient);
                    Interlocked.Add(ref sentBytesTransient, bytes);
                    break;
                case 1:
                    Interlocked.Increment(ref sentPacketsControl);
                    Interlocked.Add(ref sentBytesControl, bytes);
                    break;
                default:
                    Interlocked.Increment(ref sentPacketsResource);
                    Interlocked.Add(ref sentBytesResource, bytes);
                    break;
            }
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void RecordReceiveSuccess(int steamLane, int bytes)
        {
            switch (steamLane)
            {
                case 0:
                    Interlocked.Increment(ref receivedPacketsTransient);
                    Interlocked.Add(ref receivedBytesTransient, bytes);
                    break;
                case 1:
                    Interlocked.Increment(ref receivedPacketsControl);
                    Interlocked.Add(ref receivedBytesControl, bytes);
                    break;
                default:
                    Interlocked.Increment(ref receivedPacketsResource);
                    Interlocked.Add(ref receivedBytesResource, bytes);
                    break;
            }
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void RecordSendFailure()
        {
            Interlocked.Increment(ref sendFailureCount);
        }
    }
}
