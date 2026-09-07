using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Instance-wide locomotion policy: the jump height, walk/run speed, gravity and movement mode
    /// this server dictates for everyone in it. Seeded from Configuration at boot, persisted on
    /// change, and pushed to clients via GlobalGetLocomotionPolicy — on join as well as on change,
    /// which is what separates it from the one-shot SetLocomotionOverrideAll fan-out: a player who
    /// arrives an hour later lands on the same policy as everyone already here.
    ///
    /// Clients apply it under a reserved key, ranked below a moderator's own live override, so a
    /// moderator can still single someone out without the policy fighting them for it.
    /// </summary>
    public static class BasisLocomotionPolicyManager
    {
        /// <summary>Every bit the policy mask may carry: jumpHeight, walkSpeed, runSpeed, gravity, mode.</summary>
        public const byte AllFields = 31;

        /// <summary>Highest movement mode the client understands (0 Walk, 1 Fly, 2 NoClip).</summary>
        public const byte MaxMode = 2;

        private const float DefaultJumpHeight = 1f;
        private const float DefaultWalkSpeed = 2.5f;
        private const float DefaultRunSpeed = 4f;
        private const float DefaultGravity = -9.81f;

        /// <summary>
        /// Speeds and jump height are metres and metres per second; nothing playable lives above
        /// this, and a wild value from a hand-edited config.xml would only be a way to fling every
        /// player in the instance out of the world.
        /// </summary>
        private const float MaxDistance = 1000f;

        /// <summary>Gravity is clamped non-positive — the client's jump is sqrt(-2gh), so a
        /// positive value would hand every player a NaN vertical speed.</summary>
        private const float MinGravity = -1000f;

        private static int _fields;
        private static float _jumpHeight = DefaultJumpHeight;
        private static float _walkSpeed = DefaultWalkSpeed;
        private static float _runSpeed = DefaultRunSpeed;
        private static float _gravity = DefaultGravity;
        private static int _mode;

        public static byte Fields => (byte)Interlocked.CompareExchange(ref _fields, 0, 0);
        public static float JumpHeight => Interlocked.CompareExchange(ref _jumpHeight, 0f, 0f);
        public static float WalkSpeed => Interlocked.CompareExchange(ref _walkSpeed, 0f, 0f);
        public static float RunSpeed => Interlocked.CompareExchange(ref _runSpeed, 0f, 0f);
        public static float Gravity => Interlocked.CompareExchange(ref _gravity, 0f, 0f);
        public static byte Mode => (byte)Interlocked.CompareExchange(ref _mode, 0, 0);

        public static void InitializeFromConfig(Configuration config)
        {
            SetPolicy(
                config.LocomotionPolicyFields,
                config.LocomotionPolicyJumpHeight,
                config.LocomotionPolicyWalkSpeed,
                config.LocomotionPolicyRunSpeed,
                config.LocomotionPolicyGravity,
                config.LocomotionPolicyMode);
        }

        /// <summary>Sanitize, set, and report whether any field actually changed.</summary>
        public static bool SetPolicy(byte fields, float jumpHeight, float walkSpeed, float runSpeed, float gravity, byte mode)
        {
            Sanitize(ref fields, ref jumpHeight, ref walkSpeed, ref runSpeed, ref gravity, ref mode);

            int prevFields = Interlocked.Exchange(ref _fields, fields);
            float prevJump = Interlocked.Exchange(ref _jumpHeight, jumpHeight);
            float prevWalk = Interlocked.Exchange(ref _walkSpeed, walkSpeed);
            float prevRun = Interlocked.Exchange(ref _runSpeed, runSpeed);
            float prevGravity = Interlocked.Exchange(ref _gravity, gravity);
            int prevMode = Interlocked.Exchange(ref _mode, mode);

            return prevFields != fields || prevJump != jumpHeight || prevWalk != walkSpeed
                || prevRun != runSpeed || prevGravity != gravity || prevMode != mode;
        }

        /// <summary>Copy the sanitized policy back onto the configuration so it persists as stored.</summary>
        public static void WriteToConfig(Configuration config)
        {
            config.LocomotionPolicyFields = Fields;
            config.LocomotionPolicyJumpHeight = JumpHeight;
            config.LocomotionPolicyWalkSpeed = WalkSpeed;
            config.LocomotionPolicyRunSpeed = RunSpeed;
            config.LocomotionPolicyGravity = Gravity;
            config.LocomotionPolicyMode = Mode;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                Write(writer);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        public static void BroadcastState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                Write(writer);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.AdminChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static void Write(NetDataWriter writer)
        {
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetLocomotionPolicy);
            writer.Put(Fields);
            writer.Put(JumpHeight);
            writer.Put(WalkSpeed);
            writer.Put(RunSpeed);
            writer.Put(Gravity);
            writer.Put(Mode);
        }

        /// <summary>
        /// Every value reaches a CharacterController on the far side, so a non-finite one is a
        /// corrupted root transform on every client at once rather than a bad setting. Unknown mask
        /// bits and modes are dropped instead of forwarded, and unclaimed fields still travel with
        /// sane numbers so the admin panel has something to show.
        /// </summary>
        private static void Sanitize(ref byte fields, ref float jumpHeight, ref float walkSpeed, ref float runSpeed, ref float gravity, ref byte mode)
        {
            fields &= AllFields;

            jumpHeight = Distance(jumpHeight, DefaultJumpHeight);
            walkSpeed = Distance(walkSpeed, DefaultWalkSpeed);
            runSpeed = Distance(runSpeed, DefaultRunSpeed);

            if (float.IsNaN(gravity) || float.IsInfinity(gravity)) gravity = DefaultGravity;
            if (gravity > 0f) gravity = 0f;
            if (gravity < MinGravity) gravity = MinGravity;

            if (mode > MaxMode) mode = 0;
        }

        private static float Distance(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            if (value < 0f) return 0f;
            return value > MaxDistance ? MaxDistance : value;
        }
    }
}
