using System;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;

namespace Basis.Tests.Remote
{
    public class BasisChatHistoryTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void KeepsTheLastFifteenMessagesInArrivalOrder()
        {
            BasisChatHistory history = new BasisChatHistory();
            for (int i = 0; i < 20; i++)
            {
                history.Add("message " + i, Start.AddSeconds(i));
            }

            Assert.AreEqual(15, BasisChatHistory.Capacity);
            Assert.AreEqual(BasisChatHistory.Capacity, history.Count);
            for (int i = 0; i < history.Count; i++)
            {
                Assert.AreEqual("message " + (i + 5), history[i].Message);
                Assert.AreEqual(Start.AddSeconds(i + 5), history[i].ReceivedUtc);
            }
        }

        [Test]
        public void IgnoresEmptyMessages()
        {
            BasisChatHistory history = new BasisChatHistory();
            history.Add("hello", Start);
            int version = history.Version;

            history.Add(string.Empty, Start.AddSeconds(1));
            history.Add(null, Start.AddSeconds(2));

            Assert.AreEqual(1, history.Count);
            Assert.AreEqual("hello", history[0].Message);
            Assert.AreEqual(version, history.Version);
        }

        [Test]
        public void VersionMovesForEveryRecordedMessageIncludingEvictions()
        {
            BasisChatHistory history = new BasisChatHistory();
            int version = history.Version;
            for (int i = 0; i < BasisChatHistory.Capacity + 3; i++)
            {
                history.Add("message " + i, Start.AddSeconds(i));
                Assert.AreNotEqual(version, history.Version);
                version = history.Version;
            }
        }
    }
}
