namespace Basis.Network.Core
{
    public static class BasisCaptureProtocol
    {
        public const string MessageName = "basis.capture";
        public const string DataMessageId = "basis.capture.data";
        public const string PermissionNode = "basis.capture.collect";
        public const string RecordNode = "basis.capture.record";
        public const string LockNode = "basis.capture.lock";
        public const byte OpStart = 1, OpStop = 2, OpCollect = 3, OpDenied = 4, OpLockState = 5, OpSetLock = 6, OpQueryLock = 7, OpAsk = 8, OpConsent = 9;
        public const int AskIntervalMs = 3000;
        public const byte DataStatus = 1, DataOffer = 2, DataChunk = 3, DataAck = 4, DataDone = 5, DataCancel = 6;
        public const byte StatusRecording = 1, StatusNoAvatar = 2, StatusFailed = 3, StatusNothing = 4, StatusPacking = 5;
        public const int ChunkBytes = 32 * 1024, WindowChunks = 32;
        public const long MaxTakeBytes = 2L * 1024 * 1024 * 1024;
    }
}
