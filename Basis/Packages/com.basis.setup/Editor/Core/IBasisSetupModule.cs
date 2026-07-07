namespace Basis.Setup
{
    public enum BasisSetupStatus
    {
        NotApplicable,
        Missing,
        Outdated,
        UpToDate,
        Error
    }

    public enum BasisSetupMode
    {
        EnsureExists,
        Update
    }

    public struct BasisSetupReport
    {
        public string Key;
        public BasisSetupStatus Status;
        public bool Changed;
        public string Message;

        public static BasisSetupReport Result(string key, BasisSetupStatus status, bool changed, string message)
        {
            return new BasisSetupReport { Key = key, Status = status, Changed = changed, Message = message };
        }
    }

    /// <summary>
    /// A unit of Assets-level project configuration that Basis Setup can generate from code
    /// and reconcile on update. Implementations need a public parameterless constructor;
    /// they are discovered by reflection and listed in the setup window.
    /// </summary>
    public interface IBasisSetupModule
    {
        string Key { get; }
        string DisplayName { get; }
        string Category { get; }
        int Version { get; }
        int Order { get; }
        bool IsAvailable { get; }
        bool AutoApplyOnImport { get; }
        BasisSetupStatus GetStatus();
        BasisSetupReport Apply(BasisSetupMode mode);
    }
}
