using Microsoft.Data.Sqlite;

namespace PersistentKv
{
    public enum KvError : ushort
    {
        Success = 0,
        // Unknown (fallback)
        Unknown = 1,

        // Bucket errors (1000-1999)
        BucketNotFound = 1000,
        BucketImmutable = 1002,
        BucketIdSize = 1003,
        BucketListLengthInvalid = 1101,
        BucketListOffsetInvalid = 1102,
        KeyNotFound = 1201,
        KeyAlreadyExists = 1202,

        // Validation errors (2000-2999)
        ValidationKeySize = 2000,
        ValidationValueNull = 2010,
        ValidationValueSize = 2011,
        ValidationBucketIdSize = 2020,
        ValidationLimitInvalid = 2021,

        // Quotas (3000-3999)
        QuotaKeys = 3000,
        QuotaBytes = 3001,
        QuotaKeySize = 3002,
        QuotaValueSize = 3003,

        // Rate limits (4000-4999)
        RateWriteMinute = 4000,
        RateWriteHour = 4001,
        RateReadMinute = 4002,
        RateReadHour = 4003,

        // Database Errors (6000 - 6999)
        DatabaseGenericError = 6001,
        DatabaseUnknownConstraint = 6002,

        // Server Error
        ServerInvalidParameter = 7001,
    }

    public struct Unit { }

    public struct KvResult<T>
    {
        public KvError ErrorCode;
        public string Message;
        public T? Value;

        public KvResult(KvError code, string message, T? value)
        {
            ErrorCode = code;
            Message = message;
            Value = value;
        }

        public static KvResult<T> Ok(T value)
        {
            return new KvResult<T>(KvError.Success, "", value);
        }
        public static KvResult<T> Fail(KvError code, string msg)
        {
            return new(code, msg, default);
        }

        public static KvResult<T> FromSqlException(SqliteException ex)
        {
            var message = ex.Message;

            // Map SQLite error codes
            var kvError = ex.SqliteErrorCode switch
            {
                0 => KvError.Success,
                19 => KvError.DatabaseUnknownConstraint, // temp constraint error
                > 0 and < 26 => KvError.DatabaseGenericError, // flatten sqlite errors
                _ => KvError.Unknown,
            };

            // Convert to specific error based on error message
            if (kvError == KvError.DatabaseUnknownConstraint)
            {
                return Fail(FromSqlMessage(message), ex.Message);
            }
            // Dont know specific error, was from database, return generic error
            else if (kvError == KvError.DatabaseGenericError)
            {
                return Fail(kvError, ex.Message);
            }
            // Dont know what errored at all
            else return Fail(kvError, ex.Message);
        }

        private static KvError FromSqlMessage(string message)
        {
            // Check custom error codes first (these start with a prefix followed by :)
            if (message.Contains("U_NOT_FOUND:")) return KvError.BucketNotFound;
            if (message.Contains("U_IMMUTABLE:")) return KvError.BucketImmutable;
            if (message.Contains("Q_KEYS:")) return KvError.QuotaKeys;
            if (message.Contains("Q_BYTES:")) return KvError.QuotaBytes;
            if (message.Contains("Q_KEY_SIZE:")) return KvError.QuotaKeySize;
            if (message.Contains("Q_VAL_SIZE:")) return KvError.QuotaValueSize;

            // Fallback to constraint name matching for constraint errors
            if (message.Contains("value_size")) return KvError.ValidationValueSize;
            if (message.Contains("bucket_id_size")) return KvError.BucketIdSize;
            if (message.Contains("key_size")) return KvError.ValidationKeySize;

            return KvError.Unknown;
        }

    }
}
