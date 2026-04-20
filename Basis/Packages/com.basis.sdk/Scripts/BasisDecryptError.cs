public static partial class BasisEncryptionWrapper
{
    public enum BasisDecryptError
    {
        None = 0,
        InvalidPassword,
        DataNullOrEmpty,
        HeaderTooShort,
        WrongFormatOrCorruptHeader,
        WrongPasswordOrCorruptedData,
        CryptoFailed,
        Cancelled,
        Unknown,
    }
}
