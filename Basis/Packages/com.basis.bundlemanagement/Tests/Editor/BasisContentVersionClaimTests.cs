using NUnit.Framework;

/// <summary>
/// Covers the verdict a version-claim mismatch is resolved with. A requested tag that differs from
/// the cache is only a peer's claim, and the claim itself can be the stale side: nginx ETags embed
/// file mtime, so a host re-syncing identical bytes mints a new tag that wearers (whose warm loads
/// never touch the network) keep broadcasting the old value of. The observed failure was a receiver
/// evicting a current multi-megabyte cache and re-downloading it on every load because of exactly
/// such a claim — the cached tag matched what the host served, while the requested tag was the
/// wearer's outdated observation. The host, not the claim, settles the mismatch.
/// </summary>
public class BasisContentVersionClaimTests
{
    // nginx-shaped hex(mtime)-hex(size) pair: same size, different mtime — the signature of a
    // re-synced identical file. Synthetic values, not any real host's tags.
    private const string CurrentTag = "\"11aa22bb-3c4d5e\"";
    private const string StaleClaim = "\"00998877-3c4d5e\"";

    [Test]
    public void NotModifiedConfirmsCache()
    {
        // A 304 carries no obligation to repeat validators; the answer alone is proof.
        var validator = new BasisIOManagement.BasisRemoteValidator(null, null, notModified: true);
        Assert.IsTrue(BasisContentVersion.HostConfirmsCache(CurrentTag, validator));
    }

    [Test]
    public void MatchingEtagConfirmsCache()
    {
        var validator = new BasisIOManagement.BasisRemoteValidator(CurrentTag, null);
        Assert.IsTrue(BasisContentVersion.HostConfirmsCache(CurrentTag, validator));
    }

    [Test]
    public void WeakAndStrongSpellingsOfTheSameEtagConfirmCache()
    {
        // Same entity, different spellings — comparing raw would read a re-served identical
        // file as a change.
        var validator = new BasisIOManagement.BasisRemoteValidator("W/" + CurrentTag, null);
        Assert.IsTrue(BasisContentVersion.HostConfirmsCache(CurrentTag, validator));
    }

    [Test]
    public void DifferentEtagIsAGenuineUpdate()
    {
        var validator = new BasisIOManagement.BasisRemoteValidator(StaleClaim, null);
        Assert.IsFalse(BasisContentVersion.HostConfirmsCache(CurrentTag, validator));
    }

    [Test]
    public void HostWithoutValidatorsCannotConfirm()
    {
        // No validators means the tag scheme is a creator-stamped nonce, where a mismatch
        // means "changed" by construction — the refresh must proceed.
        var validator = new BasisIOManagement.BasisRemoteValidator(null, null);
        Assert.IsFalse(BasisContentVersion.HostConfirmsCache(CurrentTag, validator));
    }

    [Test]
    public void MatchingLastModifiedFallbackConfirmsCache()
    {
        const string lastModified = "Tue, 02 Jan 2024 00:00:00 GMT";
        string cached = BasisContentVersion.LastModifiedPrefix + lastModified;
        var validator = new BasisIOManagement.BasisRemoteValidator(null, lastModified);
        Assert.IsTrue(BasisContentVersion.HostConfirmsCache(cached, validator));
    }

    [Test]
    public void DifferentLastModifiedIsAGenuineUpdate()
    {
        string cached = BasisContentVersion.LastModifiedPrefix + "Mon, 01 Jan 2024 00:00:00 GMT";
        var validator = new BasisIOManagement.BasisRemoteValidator(null, "Tue, 02 Jan 2024 00:00:00 GMT");
        Assert.IsFalse(BasisContentVersion.HostConfirmsCache(cached, validator));
    }

    [Test]
    public void EmptyCachedTagNeverMatchesAValidator()
    {
        var validator = new BasisIOManagement.BasisRemoteValidator(CurrentTag, null);
        Assert.IsFalse(BasisContentVersion.HostConfirmsCache(string.Empty, validator));
    }

    [Test]
    public void TheObservedFailure_StaleClaimAgainstConfirmedCache_ServesCache()
    {
        // The observed loop: the claim mismatches the cache, so the pre-check must fail...
        var meta = new BasisBEEExtensionMeta { CachedVersionTag = CurrentTag };
        Assert.IsFalse(BasisContentVersion.CacheSatisfies(meta, StaleClaim));
        // ...but the host answers the conditional request with 304, which overrules the claim.
        var validator = new BasisIOManagement.BasisRemoteValidator(null, null, notModified: true);
        Assert.IsTrue(BasisContentVersion.HostConfirmsCache(meta.CachedVersionTag, validator));
    }
}
