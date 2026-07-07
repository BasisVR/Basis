using System;
using UnityEngine;

namespace Basis.Setup
{
    public abstract class BasisSetupModuleBase : IBasisSetupModule
    {
        public abstract string Key { get; }
        public abstract string DisplayName { get; }
        public virtual string Category => "General";
        public abstract int Version { get; }
        public virtual int Order => 0;
        public virtual bool IsAvailable => true;
        public virtual bool AutoApplyOnImport => false;

        protected abstract bool Exists();

        protected abstract bool Build(BasisSetupMode mode);

        public BasisSetupStatus GetStatus()
        {
            if (!IsAvailable)
            {
                return BasisSetupStatus.NotApplicable;
            }

            if (!Exists())
            {
                return BasisSetupStatus.Missing;
            }

            return BasisSetupState.GetVersion(Key) >= Version
                ? BasisSetupStatus.UpToDate
                : BasisSetupStatus.Outdated;
        }

        public BasisSetupReport Apply(BasisSetupMode mode)
        {
            if (!IsAvailable)
            {
                return BasisSetupReport.Result(Key, BasisSetupStatus.NotApplicable, false, "Dependency not present.");
            }

            bool existed = Exists();
            if (mode == BasisSetupMode.EnsureExists && existed)
            {
                return BasisSetupReport.Result(Key, GetStatus(), false, "Already present.");
            }

            try
            {
                bool changed = Build(mode);
                BasisSetupState.SetVersion(Key, Version);
                string message = changed ? (existed ? "Updated." : "Created.") : "No changes needed.";
                return BasisSetupReport.Result(Key, BasisSetupStatus.UpToDate, changed, message);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BasisSetup] {DisplayName} failed: {e}");
                return BasisSetupReport.Result(Key, BasisSetupStatus.Error, false, e.Message);
            }
        }
    }
}
