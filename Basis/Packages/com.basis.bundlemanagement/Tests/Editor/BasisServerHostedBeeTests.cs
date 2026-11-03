using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
public class BasisServerHostedBeeTests
{
    private const string Password = "hosted-bee-test-password";
    private const int AndroidLength = 500, GenericLength = 300;
    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
    private static byte[] Payload(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
    private static async Task<byte[]> EncryptedConnector()
    {
        BasisBundleConnector connector = new BasisBundleConnector
        {
            UniqueVersion = "hosted-bee-test",
            BasisBundleGenerated = new[]
            {
                new BasisBundleGenerated { Platform = "Android", EndByte = AndroidLength },
                new BasisBundleGenerated { Platform = BasisBundleConnector.GenericPlatform, EndByte = GenericLength },
            },
        };
        byte[] plain = BasisSerialization.SerializeValue(connector);
        return await BasisEncryptionWrapper.EncryptToBytesAsync("encrypt", new BasisEncryptionWrapper.BasisPassword { VP = Password }, plain, new BasisProgressReport());
    }
    private static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts) length += part.Length;
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
    private static byte[] Slice(byte[] source, long start, int length)
    {
        byte[] result = new byte[length];
        Buffer.BlockCopy(source, (int)start, result, 0, length);
        return result;
    }
    private static string TempFile(byte[] contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "basis-hosted-" + Guid.NewGuid().ToString("N") + ".BEE");
        File.WriteAllBytes(path, contents);
        return path;
    }
    [Test]
    public void UrlRoundTripsAndRejectsOtherAddresses()
    {
        string id = "0123456789abcdef0123456789abcdef";
        Assert.IsTrue(BasisServerHostedBee.TryParse(BasisServerHostedBee.BuildUrl(id), out string parsed));
        Assert.AreEqual(id, parsed);
        Assert.IsTrue(BasisServerHostedBee.TryParse("basis-host://bee/" + id.ToUpperInvariant(), out parsed));
        Assert.AreEqual(id, parsed);
        Assert.IsFalse(BasisServerHostedBee.IsHostedUrl("https://example.com/bee/" + id));
        Assert.IsFalse(BasisServerHostedBee.IsHostedUrl("basis-host://other/" + id));
        Assert.IsFalse(BasisServerHostedBee.IsHostedUrl("basis-host://bee/" + id.Substring(2)));
        Assert.IsFalse(BasisServerHostedBee.IsHostedUrl("basis-host://bee/zz" + id.Substring(2)));
        Assert.IsFalse(BasisServerHostedBee.IsHostedUrl("file:///C:/avatars/me.BEE"));
    }
    [Test]
    public void RemoteLayoutServesExactFileRanges()
    {
        Run(async () =>
        {
            byte[] connector = await EncryptedConnector();
            byte[] android = Payload(AndroidLength, 1), generic = Payload(GenericLength, 2);
            byte[] file = Concat(BitConverter.GetBytes((long)connector.Length), connector, android, generic);
            string path = TempFile(file);
            try
            {
                (BasisHostedBeeLayout layout, string error) = await BasisHostedBeeLayout.BuildAsync(path, Password);
                Assert.IsNotNull(layout, error);
                Assert.AreEqual(file.LongLength, layout.VirtualLength);
                Assert.AreEqual(2, layout.SharedSectionCount);
                long genericStart = 8 + connector.Length + AndroidLength;
                (byte[] header, _) = await layout.ReadAsync(0, 8);
                CollectionAssert.AreEqual(Slice(file, 0, 8), header);
                (byte[] section, _) = await layout.ReadAsync(genericStart, GenericLength);
                CollectionAssert.AreEqual(generic, section);
                (byte[] beyond, string beyondError) = await layout.ReadAsync(genericStart, GenericLength + 1);
                Assert.IsNull(beyond, beyondError);
                return true;
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
    [Test]
    public void CacheLayoutPresentsRemoteViewWithOnlyItsOwnSection()
    {
        Run(async () =>
        {
            byte[] connector = await EncryptedConnector();
            byte[] generic = Payload(GenericLength, 3);
            string remotePath = TempFile(Concat(BitConverter.GetBytes((long)connector.Length), connector, Payload(AndroidLength, 4), generic));
            string cachePath = TempFile(Concat(BitConverter.GetBytes(connector.Length), connector, generic));
            try
            {
                (BasisHostedBeeLayout cache, string error) = await BasisHostedBeeLayout.BuildAsync(cachePath, Password);
                Assert.IsNotNull(cache, error);
                (BasisHostedBeeLayout remote, _) = await BasisHostedBeeLayout.BuildAsync(remotePath, Password);
                Assert.AreEqual(remote.Id, cache.Id);
                Assert.AreEqual(remote.VirtualLength, cache.VirtualLength);
                Assert.AreEqual(1, cache.SharedSectionCount);
                (byte[] header, _) = await cache.ReadAsync(0, 8);
                Assert.AreEqual((long)connector.Length, BitConverter.ToInt64(header, 0));
                (byte[] connectorRead, _) = await cache.ReadAsync(8, connector.Length);
                CollectionAssert.AreEqual(connector, connectorRead);
                (byte[] genericRead, _) = await cache.ReadAsync(8 + connector.Length + AndroidLength, GenericLength);
                CollectionAssert.AreEqual(generic, genericRead);
                (byte[] androidRead, string androidError) = await cache.ReadAsync(8 + connector.Length, AndroidLength);
                Assert.IsNull(androidRead, androidError);
                return true;
            }
            finally
            {
                File.Delete(remotePath);
                File.Delete(cachePath);
            }
        });
    }
    [Test]
    public void ChangedFileStopsBeingServed()
    {
        Run(async () =>
        {
            byte[] connector = await EncryptedConnector();
            string path = TempFile(Concat(BitConverter.GetBytes((long)connector.Length), connector, Payload(AndroidLength, 5), Payload(GenericLength, 6)));
            try
            {
                (BasisHostedBeeLayout layout, string error) = await BasisHostedBeeLayout.BuildAsync(path, Password);
                Assert.IsNotNull(layout, error);
                File.AppendAllText(path, "x");
                (byte[] data, string readError) = await layout.ReadAsync(0, 8);
                Assert.IsNull(data);
                StringAssert.Contains("changed", readError);
                return true;
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
