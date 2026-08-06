using NUnit.Framework;

/// <summary>
/// Covers what is allowed to destroy a user's saved worlds and avatars.
///
/// <para>A failed meta-only load hands the library a <see cref="BasisMetaLoadResult"/>, and the
/// library deletes the cached bee AND the ItemKeyStore entry — the url and the password, the only
/// copy the client holds — whenever that result reads as corruption. The verdict used to be a
/// substring match for "Network error:", "Cancelled", "Timeout" or "SSL" against the error text.
/// Only one of those is ever produced by a machine (UnityWebRequest failures are prefixed
/// "Network error: "), so every failure the client generates ITSELF carried none of them and was
/// read as corruption: a URL-security refusal, a fail-closed DNS lookup, a size cap, a redirect
/// budget, a full disk. One DNS blip permanently deleted the item.</para>
///
/// <para>These tests drive <see cref="BasisMetaLoadResult.FromFailure"/>, the single function both
/// load paths in HandleMetaOnlyLoad now route through. Asserting the factories directly would be a
/// tautology — <c>Fail</c> and <c>FailCorrupt</c> hard-code their verdicts and never read the
/// message — so the mapping is what gets pinned here: given the verdict a reader reported, does the
/// library get permission to delete? Re-deriving corruption from message text is exactly the bug,
/// and doing so again would fail these tests.</para>
/// </summary>
public class BasisMetaLoadClassificationTests
{
    // Composed the way the pipeline builds them, hop by hop:
    //   BasisUrlSecurity.ValidateResolvedHostAsync -> DownloadRangeInternal ("Blocked URL: ")
    //   -> DownloadConnectorOnlyEx ("Failed to read header. ") -> BuildResultError.
    // No HTTP code is set on the gate path, so there is no " (HTTP n)" suffix.
    private const string PolicyRefusal =
        "DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Failed to read header. Blocked URL: host 'cdn.example.com' resolves to a blocked address (benchmarking 198.18/15).";
    private const string DnsLookupFailed =
        "DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Failed to read header. Blocked URL: host 'cdn.example.com' could not be validated (DNS lookup failed: No such host is known.).";
    private const string DnsReturnedNothing =
        "DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Failed to read header. Blocked URL: host 'cdn.example.com' could not be validated (DNS returned no addresses).";
    private const string LiteralAddressRefusal =
        "DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Blocked URL host '10.0.0.5': RFC1918 10/8";
    private const string RangeStrippingProxy =
        "DownloadConnectorOnlyEx failed (HTTP 200): DownloadConnectorOnlyEx: Failed to read header. Server returned 200 (full file). Host must support HTTP range requests (206).";
    private const string TruncatedTransfer =
        "DownloadConnectorOnlyEx failed (HTTP 206): DownloadConnectorOnlyEx: Failed to read header. Content-Length mismatch. Header=8, Received=4.";
    private const string DiskFull =
        "DownloadConnectorOnlyEx failed: DownloadBEEEx: WriteBeeFileAsync: IOException: There is not enough space on the disk.";
    // BasisBeeValidator.IsValidUrl rejects the scheme in DownloadConnectorFile BEFORE
    // DownloadConnectorOnlyEx runs, so this one reaches the classifier unwrapped.
    private const string UnmountedLocalBee =
        "Unsupported URL scheme 'file'. Only HTTP/HTTPS are supported.";
    private const string CacheIndexDisagreesWithDisk =
        "ReadBEEFileEx failed. ReadBEEFileEx: File not found: /cache/AssetBundles/abc123.bec";
    private const string DecryptReturnedNull =
        "DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Failed to parse connector metadata (null).";

    // ---- failures we could not attribute to bad bytes: never grounds for deletion ----

    [TestCase(PolicyRefusal)]
    [TestCase(DnsLookupFailed)]
    [TestCase(DnsReturnedNothing)]
    [TestCase(LiteralAddressRefusal)]
    [TestCase(RangeStrippingProxy)]
    [TestCase(TruncatedTransfer)]
    [TestCase(DiskFull)]
    [TestCase(UnmountedLocalBee)]
    [TestCase(CacheIndexDisagreesWithDisk)]
    [TestCase(DecryptReturnedNull)]
    public void AnUnattributedFailureNeverAuthorizesDeletion(string error)
    {
        // The premise of the whole bug: none of these carry a token the old text classifier looked
        // for, which is why "we could not fetch it" was read as "the bytes are bad".
        Assert.That(BasisMetaLoadResult.LooksLikeTransientError(error), Is.False,
            "premise: this is exactly the text that made the old classifier call a failure to fetch corruption");

        BasisMetaLoadResult result = BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Unspecified, error);
        Assert.That(result.IsCorrupt, Is.False,
            "the library reads IsCorrupt as permission to DeleteStoredFile and RemoveKey");
        Assert.That(result.Loaded, Is.False, "a failure must not report itself as loaded");
    }

    // ---- and a reader that positively identified unusable bytes: still evicts ----

    [TestCase("ReadBEEFileEx failed. ReadBEEFileEx: File too small to contain header. Size=3 bytes.")]
    [TestCase("ReadBEEFileEx failed. ReadBEEFileEx: Invalid connector size -1. Remaining file bytes: 4096. File may be corrupt.")]
    [TestCase("ReadBEEFileEx failed. ReadBEEFileEx: Failed to read full connector block. Expected 512, got 128.")]
    [TestCase("DownloadConnectorOnlyEx failed: DownloadConnectorOnlyEx: Invalid connector length -4.")]
    public void BytesAReaderRejectedStillAuthorizeDeletion(string error)
    {
        Assert.That(BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Corrupt, error).IsCorrupt, Is.True,
            "a file that cannot satisfy its own declared sizes must stay evictable, or a bad cache entry is permanent");
    }

    [Test]
    public void TheVerdictComesFromTheReaderNotFromWordsInTheMessage()
    {
        // Both directions of the text heuristic, against the real mapping. The error strings
        // interpolate urls and file paths, so a bee served from a host containing "ssl" makes every
        // failure for that item look transient — and a refusal contains none of the tokens at all.
        const string corruptFromAnSslLookingHost =
            "ReadBEEFileEx failed. ReadBEEFileEx: File too small to contain header. Size=3 bytes. url=https://assets-ssl.example.com/x.bee";

        Assert.That(BasisMetaLoadResult.LooksLikeTransientError(corruptFromAnSslLookingHost), Is.True,
            "premise: the text classifier really does misread this one as transient");
        Assert.That(BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Corrupt, corruptFromAnSslLookingHost).IsCorrupt,
            Is.True, "text that looks transient must not downgrade a reader's Corrupt verdict");

        Assert.That(BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Unspecified, corruptFromAnSslLookingHost).IsCorrupt,
            Is.False, "and text that looks corrupt must not manufacture a verdict no reader gave");
    }

    // ---- the fail-safe default ----

    [TestCase(null)]
    [TestCase("")]
    [TestCase("something nobody has written a case for yet")]
    [TestCase("Server returned 418. Accept-Ranges=, Content-Range=, Content-Length=")]
    public void AnUnclassifiedFailureIsNonDestructiveByDefault(string error)
    {
        Assert.That(BeeResult<bool>.Fail(error).FailureKind, Is.EqualTo(BasisLoadFailureKind.Unspecified),
            "Unspecified must be the zero value so a newly added Fail() cannot quietly gain the power to delete user data");
        Assert.That(BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Unspecified, error).IsCorrupt, Is.False,
            "absence of evidence of transience is not evidence of corruption");
    }

    [Test]
    public void DefaultConstructedBeeResultCannotAuthorizeDeletion()
    {
        Assert.That(default(BeeResult<bool>).FailureKind, Is.EqualTo(BasisLoadFailureKind.Unspecified),
            "the enum's zero value is what protects every failure site nobody has classified");
    }

    [Test]
    public void WrappingAFailureCarriesItsVerdictAndCode()
    {
        BeeResult<bool> corrupt = BeeResult<bool>.FailCorrupt("inner: bad header", 206);
        BeeResult<int> wrappedCorrupt = BeeResult<int>.FailFrom(corrupt, "outer: read failed. inner: bad header");
        Assert.That(wrappedCorrupt.FailureKind, Is.EqualTo(BasisLoadFailureKind.Corrupt),
            "a wrap site that drops the verdict silently makes a bad cache entry permanent");
        Assert.That(wrappedCorrupt.ResponseCode, Is.EqualTo(206), "the response code must survive the wrap too");

        BeeResult<bool> refused = BeeResult<bool>.Fail(PolicyRefusal);
        Assert.That(BeeResult<int>.FailFrom(refused, "outer: " + PolicyRefusal).FailureKind,
            Is.EqualTo(BasisLoadFailureKind.Unspecified),
            "wrapping must not invent corruption that the inner reader never claimed");
    }

    [Test]
    public void LegacyBoolConversionStillReportsOnlyWhetherTheLoadSucceeded()
    {
        // LibraryProvider.TryDetectModeFromUrl still does `bool isValid = await HandleMetaOnlyLoad(...)`.
        Assert.That((bool)BasisMetaLoadResult.Success, Is.True);
        Assert.That((bool)BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Unspecified, PolicyRefusal), Is.False);
        Assert.That((bool)BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Corrupt, "bad header"), Is.False);
    }

    [Test]
    public void RetryabilityIsCarriedSeparatelyFromTheEvictionDecision()
    {
        // BasisAvatarFarLOD spends its second of two attempts on IsTransient. That budget decision
        // is allowed to use a heuristic; the deletion decision is not.
        BasisMetaLoadResult networkBlip = BasisMetaLoadResult.FromFailure(
            BasisLoadFailureKind.Unspecified, "Network error: Cannot resolve destination host. ");
        Assert.That(networkBlip.IsTransient, Is.True, "a network blip should still buy a retry");
        Assert.That(networkBlip.IsCorrupt, Is.False, "and must never delete");

        BasisMetaLoadResult refusal = BasisMetaLoadResult.FromFailure(BasisLoadFailureKind.Unspecified, PolicyRefusal);
        Assert.That(refusal.IsTransient, Is.False, "retrying a refused host in the same tick cannot help");
        Assert.That(refusal.IsCorrupt, Is.False, "but not retrying is still not a reason to delete");
    }
}
