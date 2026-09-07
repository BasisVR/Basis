using System;

namespace Basis.BasisUI
{


    /// <summary>
    /// A single entry tracked by <see cref="BasisNotificationCenter"/>.
    ///
    /// A popup that is closed before it has been accepted or denied is captured as a
    /// <see cref="BasisNotificationStatus.Pending"/> entry that keeps the live
    /// <see cref="Reopen"/> factory, so the original prompt can be brought back up and
    /// answered later. Popups that are answered immediately are stored as terminal
    /// history entries instead.
    /// </summary>
    public sealed class BasisNotification
    {
        public readonly Guid Id = Guid.NewGuid();

        public string Title;
        public string Description;
        public string IconAddress;

        public BasisNotificationStatus Status = BasisNotificationStatus.Pending;

        public BasisNotificationCategory Category = BasisNotificationCategory.System;

        public readonly DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime? ResolvedUtc;

        /// <summary>
        /// Re-spawns the original popup with its original callback still attached.
        /// Null when the popup cannot be rebuilt (e.g. a generic dialog whose caller
        /// supplied no reopen factory).
        /// </summary>
        public Action Reopen;

        /// <summary>
        /// Invoked when a pending entry is dismissed without being re-opened, letting
        /// the source resolve its callback (typically a "deny"/"false" response).
        /// </summary>
        public Action OnDismiss;

    }
}
