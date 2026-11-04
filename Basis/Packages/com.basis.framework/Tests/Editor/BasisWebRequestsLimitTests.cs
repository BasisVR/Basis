using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests
{
    public class BasisWebRequestsLimitTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<string> owners = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string owner in owners) BasisWebRequests.Forgive(owner);
            owners.Clear();
            foreach (GameObject go in spawned)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            spawned.Clear();
            Drain();
        }

        private static void Drain()
        {
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action)) action();
        }

        private string Owner()
        {
            string owner = "web-test-" + Guid.NewGuid().ToString("N");
            owners.Add(owner);
            return owner;
        }

        private static void EndCooldown(string owner)
        {
            FieldInfo field = typeof(BasisWebRequests).GetField("strikes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "BasisWebRequests.strikes moved; this test cannot end a cooldown.");
            object strike = ((IDictionary)field.GetValue(null))[owner];
            Assert.IsNotNull(strike);
            FieldInfo until = strike.GetType().GetField("Until");
            Assert.IsNotNull(until, "Strike.Until moved; this test cannot end a cooldown.");
            until.SetValue(strike, 0f);
        }

        [Test]
        public void ADeclineCoolsTheContentDown_AndThreeMuteIt()
        {
            string owner = Owner();
            Assert.IsFalse(BasisWebRequests.IsMuted(owner));

            BasisWebRequests.StrikeOut(owner);
            Assert.IsTrue(BasisWebRequests.IsMuted(owner), "a decline starts a cooldown");
            EndCooldown(owner);
            Assert.IsFalse(BasisWebRequests.IsMuted(owner), "one decline only cools the content down");

            for (int i = 1; i < BasisWebRequests.DeclinesBeforeMute; i++) BasisWebRequests.StrikeOut(owner);
            EndCooldown(owner);
            Assert.IsTrue(BasisWebRequests.IsMuted(owner), "enough declines mute the content for the session");

            BasisWebRequests.Forgive(owner);
            Assert.IsFalse(BasisWebRequests.IsMuted(owner));
        }

        [Test]
        public void StrikesBelongToOneContent()
        {
            string first = Owner();
            string second = Owner();
            for (int i = 0; i < BasisWebRequests.DeclinesBeforeMute; i++) BasisWebRequests.StrikeOut(first);

            Assert.IsTrue(BasisWebRequests.IsMuted(first));
            Assert.IsFalse(BasisWebRequests.IsMuted(second));
            Assert.IsFalse(BasisWebRequests.IsMuted(null));
        }

        [Test]
        public void AMutedContentIsRefusedWithoutANewSitePrompt()
        {
            GameObject prop = new GameObject("web request prop");
            spawned.Add(prop);
            prop.AddComponent<BasisProp>();
            string owner = Owner();
            BasisWebRequests.StrikeOut(owner);

            List<BasisWebRequests.Status> results = new List<BasisWebRequests.Status>();
            BasisWebRequests.Send(prop, owner, prop, "https://" + Guid.NewGuid().ToString("N") + ".example.com/", null, null, reply => results.Add(reply.Status));
            BasisWebRequests.Send(prop, owner, prop, "https://" + Guid.NewGuid().ToString("N") + ".example.com/", null, null, reply => results.Add(reply.Status));
            Assert.IsEmpty(results, "results arrive on a later frame");
            Drain();

            CollectionAssert.AreEqual(new[] { BasisWebRequests.Status.Declined, BasisWebRequests.Status.Declined }, results);
        }

        [Test]
        public void DownloadsStopAtTheCapWhileReceiving()
        {
            using (BasisWebRequests.CappedDownload download = new BasisWebRequests.CappedDownload())
            {
                byte[] chunk = new byte[BasisWebRequests.ReceiveChunkBytes];
                int received = 0;
                while (received + chunk.Length <= BasisWebRequests.MaxDownloadBytes)
                {
                    Assert.IsTrue(download.Accept(chunk, chunk.Length));
                    received += chunk.Length;
                }
                Assert.IsFalse(download.Exceeded);
                Assert.AreEqual(received, download.Length);

                Assert.IsFalse(download.Accept(chunk, BasisWebRequests.MaxDownloadBytes - received + 1), "a chunk that crosses the cap aborts the transfer");
                Assert.IsTrue(download.Exceeded);
                Assert.IsFalse(download.Accept(chunk, 1), "nothing more is taken once over the cap");
                Assert.AreEqual(received, download.Length);
                Assert.LessOrEqual(download.Take().Length, BasisWebRequests.MaxDownloadBytes);
            }
        }

        [Test]
        public void ADeclaredLengthOverTheCapIsRefusedBeforeAnyData()
        {
            using (BasisWebRequests.CappedDownload download = new BasisWebRequests.CappedDownload())
            {
                download.Expect((ulong)BasisWebRequests.MaxDownloadBytes + 1);
                Assert.IsTrue(download.Exceeded);
                Assert.IsFalse(download.Accept(new byte[16], 16));
                Assert.AreEqual(0, download.Length);
            }
        }

        [Test]
        public void TheBodyIsReturnedExactly()
        {
            using (BasisWebRequests.CappedDownload download = new BasisWebRequests.CappedDownload())
            {
                download.Expect(5);
                Assert.IsTrue(download.Accept(new byte[] { 1, 2, 3, 9, 9 }, 3));
                Assert.IsTrue(download.Accept(new byte[] { 4, 5 }, 2));
                Assert.IsTrue(download.Accept(new byte[0], 0));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, download.Take());
            }
        }

        [Test]
        public void AChunkLongerThanItsBufferIsRejected()
        {
            using (BasisWebRequests.CappedDownload download = new BasisWebRequests.CappedDownload())
            {
                Assert.IsFalse(download.Accept(new byte[4], 8));
                Assert.IsFalse(download.Exceeded);
                Assert.AreEqual(0, download.Length);
            }
        }
    }
}
