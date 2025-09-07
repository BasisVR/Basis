public readonly struct BeeResult<T>
{
    public bool IsSuccess { get; }
    public T Value { get; }
    public string Error { get; }
    public long ResponseCode { get; }
    private BeeResult(bool ok, T value, string error, long code)
    {
        IsSuccess = ok; Value = value; Error = error; ResponseCode = code;
    }
    public static BeeResult<T> Ok(T value) => new(true, value, null, -1);
    public static BeeResult<T> Fail(string error, long responseCode = -1) => new(false, default, error, responseCode);
    public override string ToString() => IsSuccess ? $"OK: {Value}" : $"FAIL[{ResponseCode.ToString() ?? "-"}]: {Error}";
}
