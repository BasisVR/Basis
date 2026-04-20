using System;

public static partial class BasisEncryptionWrapper
{
    public readonly struct BasisDecryptResult
    {
        public readonly bool Success;
        public readonly byte[] Data;
        public readonly BasisDecryptError Error;
        public readonly string Message;
        public readonly Exception Exception;

        private BasisDecryptResult(bool success, byte[] data, BasisDecryptError error, string message, Exception ex)
        {
            Success = success;
            Data = data;
            Error = error;
            Message = message;
            Exception = ex;
        }

        public static BasisDecryptResult Ok(byte[] data) =>
            new BasisDecryptResult(true, data, BasisDecryptError.None, null, null);

        public static BasisDecryptResult Fail(BasisDecryptError error, string message, Exception ex = null) =>
            new BasisDecryptResult(false, null, error, message, ex);
    }
}
