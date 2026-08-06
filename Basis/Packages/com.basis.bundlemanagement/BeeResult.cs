/// <summary>
/// Why a load failed, recorded where the failure was observed rather than re-derived downstream
/// from the message text.
///
/// <para><see cref="Corrupt"/> is the only value that means THE BYTES ARE BAD, and it is the only
/// value any caller may treat as grounds for destroying the user's cached content or their library
/// entry. Everything else — a refusal to fetch, an unreachable host, a local disk fault, a
/// cancellation — means we did not obtain the bytes, which is not the same as the bytes being
/// wrong.</para>
///
/// <para><see cref="Unspecified"/> is deliberately the default so a newly added <c>Fail(...)</c>
/// cannot silently acquire the power to delete user data. Marking a failure destructive has to be
/// something someone typed. New members added here inherit the same protection: only
/// <see cref="Corrupt"/> ever evicts.</para>
/// </summary>
public enum BasisLoadFailureKind
{
    Unspecified = 0,
    Corrupt = 1,
}

public readonly struct BeeResult<T>
{
    public bool IsSuccess { get; }
    public T Value { get; }
    public string Error { get; }
    public long ResponseCode { get; }
    /// <summary>See <see cref="BasisLoadFailureKind"/>. Unspecified for every failure that did not
    /// positively identify unusable bytes, which is most of them.</summary>
    public BasisLoadFailureKind FailureKind { get; }
    private BeeResult(bool ok, T value, string error, long code, BasisLoadFailureKind kind)
    {
        IsSuccess = ok; Value = value; Error = error; ResponseCode = code; FailureKind = kind;
    }
    public static BeeResult<T> Ok(T value) => new(true, value, null, -1, BasisLoadFailureKind.Unspecified);
    public static BeeResult<T> Fail(string error, long responseCode = -1) => new(false, default, error, responseCode, BasisLoadFailureKind.Unspecified);
    /// <summary>
    /// A failure backed by positive evidence the bytes are unusable: a header that cannot describe
    /// this format, a length the file cannot satisfy, a short read of a region the file claims to
    /// contain, or a connector block that will not decrypt/parse. Use this ONLY for those. It is
    /// what authorizes the library to delete the user's cached bee and their saved item.
    /// </summary>
    public static BeeResult<T> FailCorrupt(string error, long responseCode = -1) => new(false, default, error, responseCode, BasisLoadFailureKind.Corrupt);
    /// <summary>
    /// Re-wraps another result's failure under a new message, carrying its verdict and response
    /// code forward. Every wrap site must use this instead of <see cref="Fail"/>, or the inner
    /// reader's verdict is silently downgraded to Unspecified.
    /// </summary>
    public static BeeResult<T> FailFrom<TInner>(BeeResult<TInner> inner, string error) => new(false, default, error, inner.ResponseCode, inner.FailureKind);
    public override string ToString() => IsSuccess ? $"OK: {Value}" : $"FAIL[{ResponseCode.ToString() ?? "-"}]: {Error}";
}
