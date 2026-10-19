using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync
{
    /// <summary>
    /// What one client believes about a held prop's weld this frame.
    ///
    /// A networked hold streams the prop's offset from the holder's hand frame, so it is correct on every
    /// observer as long as both ends build the SAME frame — and wrong, permanently and silently, when they
    /// do not. The holder cannot see that: it never decodes what it sent. This is the readout that makes
    /// the disagreement visible, by having both ends publish the frame they actually used.
    /// </summary>
    public struct BasisPickupWeldReport
    {
        /// <summary>Prop name, for the label.</summary>
        public string Name;

        /// <summary>True on the holder's own client, false on an observer reconstructing the hold.</summary>
        public bool Owner;

        /// <summary>The streamed attach id — which hand, and which space the offset is expressed in.</summary>
        public byte HandId;

        /// <summary>Left hand, per the id.</summary>
        public bool Left;

        /// <summary>
        /// False when this client could not resolve the holder's hand at all (avatar loading, out of range,
        /// or — since the canonical guard — a hand frame it cannot build the way the sender did). The prop
        /// is frozen at its last pose while this is false.
        /// </summary>
        public bool FrameResolved;

        /// <summary>
        /// The frame was built from joint positions rather than falling back to the wrist bind. Both ends
        /// must agree on this: a canonical id decoded against a wrist frame is a different space.
        /// </summary>
        public bool Canonical;

        /// <summary>Wrist-to-knuckle length in metres — the unit the streamed offset travels in.</summary>
        public float HandLength;

        /// <summary>The palm frame this client used.</summary>
        public Vector3 PalmPosition;

        /// <summary>Where the prop ended up (owner: where it is; observer: where this client welded it).</summary>
        public Vector3 PropPosition;

        /// <summary>Distance from palm to prop, in hand lengths. A sane grip is well under ~3.</summary>
        public float OffsetHandLengths;

        /// <summary>
        /// Owner only. Metres between the prop's real pose and the pose rebuilt from the values just written
        /// into the sync channels. Non-zero means the encode itself is lossy on this prefab — the usual cause
        /// is a position or rotation axis left unsynced on the component, which silently drops that component
        /// of the grip offset. Everything downstream inherits it, so nothing else can be trusted until it is 0.
        /// </summary>
        public float SelfCheckError;

        /// <summary>Owner only. Degrees of the same round trip.</summary>
        public float SelfCheckAngle;
    }

    /// <summary>
    /// Per-frame collection point for <see cref="BasisPickupWeldReport"/>. Off unless a debug view turns it
    /// on, so the hold path pays nothing in a normal session; the reader clears it each frame, and the cap
    /// keeps a reader that stops running from growing it without bound.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPickupWeldDiagnostics
    {
        /// <summary>Set by whichever debug view is consuming the reports; that view must also Clear each frame.</summary>
        public static bool Enabled;

        private const int MaxReports = 64;
        private static readonly List<BasisPickupWeldReport> _reports = new List<BasisPickupWeldReport>(MaxReports);

        public static IReadOnlyList<BasisPickupWeldReport> Reports => _reports;

        public static void Report(in BasisPickupWeldReport report)
        {
            if (!Enabled || _reports.Count >= MaxReports)
            {
                return;
            }
            _reports.Add(report);
        }

        public static void Clear() => _reports.Clear();
    }
}
