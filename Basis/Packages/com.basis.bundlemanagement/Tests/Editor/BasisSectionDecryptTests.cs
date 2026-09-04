using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

/// <summary>
/// Covers decrypting a bee section straight out of the cache file instead of out of a byte[] the
/// caller already read. The cache path is the common one — every warm avatar and world load takes
/// it — and it previously held the encrypted bundle, a defensive copy of it, and the plaintext all
/// at once, so a large world peaked at several times its own size on the large object heap.
///
/// What has to hold: the file path produces exactly the bytes the array path does, it stops at the
/// declared end of its section rather than running on into whatever follows (CryptoStream drains
/// its source to EOF given the chance), and a section too large to fit a byte[] is reported rather
/// than thrown out of an integer cast.
/// </summary>
public class BasisSectionDecryptTests
{
    private const string Password = "section-decrypt-test-password";

    // Every await runs on the pool rather than the editor's synchronization context, so blocking
    // the test thread on the result cannot deadlock against a continuation that wants the main one.
    private static T Run<T>(Func<Task<T>> work)
    {
        return Task.Run(work).GetAwaiter().GetResult();
    }

    private static BasisEncryptionWrapper.BasisPassword Key => new BasisEncryptionWrapper.BasisPassword { VP = Password };

    private static byte[] Payload(int length, int seed)
    {
        byte[] data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    private static Task<byte[]> Encrypt(byte[] plaintext)
    {
        return BasisEncryptionWrapper.EncryptToBytesAsync("encrypt", Key, plaintext, new BasisProgressReport());
    }

    private static Task<BasisEncryptionWrapper.BasisDecryptResult> DecryptFile(string path, long offset, long length)
    {
        return BasisEncryptionWrapper.DecryptFromFileAsync("decrypt", Key, path, offset, length, new BasisProgressReport());
    }

    private static string TempFile()
    {
        return Path.Combine(Path.GetTempPath(), "basis-section-" + Guid.NewGuid().ToString("N") + ".bin");
    }

    [Test]
    public void FileAndBytesPathsAgree()
    {
        Run(async () =>
        {
            byte[] plaintext = Payload(300_000, 11);
            byte[] encrypted = await Encrypt(plaintext);
            string path = TempFile();
            try
            {
                File.WriteAllBytes(path, encrypted);

                var fromFile = await DecryptFile(path, 0, encrypted.LongLength);
                var fromBytes = await BasisEncryptionWrapper.DecryptFromBytesAsync("decrypt", Key, encrypted, new BasisProgressReport());

                Assert.IsTrue(fromFile.Success, "file decrypt failed: " + fromFile.Message);
                Assert.IsTrue(fromBytes.Success, "bytes decrypt failed: " + fromBytes.Message);
                CollectionAssert.AreEqual(plaintext, fromFile.Data, "file path did not reproduce the plaintext");
                CollectionAssert.AreEqual(fromBytes.Data, fromFile.Data, "file and bytes paths disagree");
            }
            finally
            {
                File.Delete(path);
            }
            return true;
        });
    }

    [Test]
    public void SectionAtAnOffsetIgnoresWhatSurroundsIt()
    {
        // The cache format puts the section last, but the descriptor carries an explicit offset and
        // length, so decryption must respect them rather than reading to end of file.
        Run(async () =>
        {
            byte[] plaintext = Payload(120_000, 22);
            byte[] encrypted = await Encrypt(plaintext);
            byte[] before = Payload(4096, 33);
            byte[] after = Payload(8192, 44);
            string path = TempFile();
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(before, 0, before.Length);
                    fs.Write(encrypted, 0, encrypted.Length);
                    fs.Write(after, 0, after.Length);
                }

                var result = await DecryptFile(path, before.LongLength, encrypted.LongLength);

                Assert.IsTrue(result.Success, "offset decrypt failed: " + result.Message);
                CollectionAssert.AreEqual(plaintext, result.Data);
            }
            finally
            {
                File.Delete(path);
            }
            return true;
        });
    }

    [Test]
    public void ARangeOutsideTheFileIsRejected()
    {
        Run(async () =>
        {
            byte[] encrypted = await Encrypt(Payload(1024, 55));
            string path = TempFile();
            try
            {
                File.WriteAllBytes(path, encrypted);

                var result = await DecryptFile(path, 0, encrypted.LongLength + 4096);

                Assert.IsFalse(result.Success);
                Assert.AreEqual(BasisEncryptionWrapper.BasisDecryptError.WrongFormatOrCorruptHeader, result.Error);
            }
            finally
            {
                File.Delete(path);
            }
            return true;
        });
    }

    [Test]
    public void ASectionTooLargeForAByteArrayIsReportedNotThrown()
    {
        // Nothing this large is written; the guard has to answer from the declared length alone,
        // because the whole point is to refuse before allocating.
        Run(async () =>
        {
            byte[] encrypted = await Encrypt(Payload(1024, 66));
            string path = TempFile();
            try
            {
                File.WriteAllBytes(path, encrypted);
                long oversized = BasisEncryptionWrapper.MaxPlaintextBytes + 4096;

                var result = await DecryptFile(path, 0, oversized);

                Assert.IsFalse(result.Success, "an oversized section must not report success");
                StringAssert.Contains("array limit", result.Message ?? string.Empty);
            }
            finally
            {
                File.Delete(path);
            }
            return true;
        });
    }

    [Test]
    public void TheSectionDescriptorKeepsLengthsThatDoNotFitAnInt()
    {
        // MaxSectionBytes is 4 GB, so every size the descriptor carries has to stay 64-bit; a
        // narrowing here would silently mis-address a large world rather than fail.
        long large = 3L * 1024 * 1024 * 1024;
        BasisBundleSection section = BasisBundleSection.FromFile("some.bee", large, large);

        Assert.AreEqual(large, section.Offset);
        Assert.AreEqual(large, section.Length);
        Assert.IsTrue(section.HasPayload);
    }

    [Test]
    public void AnEmptySectionHasNoPayload()
    {
        Assert.IsFalse(default(BasisBundleSection).HasPayload);
        Assert.IsFalse(BasisBundleSection.FromBytes(Array.Empty<byte>()).HasPayload);
        Assert.IsFalse(BasisBundleSection.FromFile("some.bee", 0, 0).HasPayload);
        Assert.IsTrue(BasisBundleSection.FromBytes(new byte[8]).HasPayload);
    }
}
