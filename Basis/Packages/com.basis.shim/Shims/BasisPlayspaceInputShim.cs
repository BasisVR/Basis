using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// How synthetic input from an avatar script combines with the wearer's real controller input.
    /// </summary>
    public enum BasisPlayerInputBlend
    {
        /// <summary>Summed with the wearer's own input; the wearer keeps control.</summary>
        Additive = 0,
        /// <summary>Replaces the wearer's own input for the frames the script is driving.</summary>
        Override = 1,
    }

    /// <summary>
    /// Write-only bridge that lets a cilbox script on the avatar you are WEARING feed synthetic input
    /// into your locomotion and play-space mover. Obtained from <see cref="BasisAvatarShim.PlayspaceInput"/>.
    ///
    /// SECURITY: avatar scripts also tick on every REMOTE player's avatar in the instance, so an
    /// ungated surface here would let anyone drive everyone else's movement. Every write is therefore
    /// checked against the local player's currently worn avatar — a script on a remote avatar, or on a
    /// local avatar that is no longer the active one, is silently dropped. The user can disable the
    /// whole path with the "enablescriptedplayerinput" setting.
    ///
    /// Input expires after one frame, so a script that stops writing releases control rather than
    /// latching it. Non-finite values are rejected, since a NaN reaching CharacterController.Move
    /// corrupts the player transform unrecoverably. Movement locks, seated state, and admin moderation
    /// locks are all still enforced downstream — this cannot bypass them.
    /// </summary>
    public sealed class BasisPlayspaceInputShim
    {
        private const float MaxHorizontal = 10f;
        private const float MaxScale = 5f;

        private readonly BasisAvatarShim owner;

        internal BasisPlayspaceInputShim(BasisAvatarShim owner)
        {
            this.owner = owner;
        }

        private bool CanDrive
        {
            get
            {
                if (owner == null || owner.IsOwnedLocally == false)
                {
                    return false;
                }

                BasisLocalPlayer local = BasisLocalPlayer.Instance;
                if (local == null || local.BasisAvatar == null || owner.Avatar == null)
                {
                    return false;
                }

                return ReferenceEquals(local.BasisAvatar, owner.Avatar);
            }
        }

        public void SetLocomotion(BasisPlayerInputBlend blend, Vector2 movement, float turn, bool jump, float crouchDelta)
        {
            if (CanDrive == false || IsFinite(movement) == false || IsFinite(turn) == false || IsFinite(crouchDelta) == false)
            {
                return;
            }

            BasisScriptedPlayerInput.SetLocomotion(
                Translate(blend),
                new Vector2(Mathf.Clamp(movement.x, -1f, 1f), Mathf.Clamp(movement.y, -1f, 1f)),
                Mathf.Clamp(turn, -1f, 1f),
                jump,
                Mathf.Clamp(crouchDelta, -1f, 1f));
        }

        public void SetHand(BasisPlayerInputBlend blend, bool isLeft, Vector3 localPosition, Vector3 unscaledPosition, bool grab, bool rotate)
        {
            if (CanDrive == false || IsFinite(localPosition) == false || IsFinite(unscaledPosition) == false)
            {
                return;
            }

            BasisScriptedPlayerInput.SetHand(Translate(blend), isLeft, localPosition, unscaledPosition, grab, rotate);
        }

        public void SetVerticalDelta(BasisPlayerInputBlend blend, float metres)
        {
            if (CanDrive == false || IsFinite(metres) == false)
            {
                return;
            }

            BasisScriptedPlayerInput.SetVerticalDelta(Translate(blend), Mathf.Clamp(metres, -1f, 1f));
        }

        /// <summary>
        /// Drags the play space horizontally, the way a one-hand grab does. Additive treats
        /// <paramref name="value"/> as a per-frame world-space nudge; Override treats it as the absolute
        /// net drag offset to hold. Y is ignored — use <see cref="SetVerticalDelta"/> for height.
        /// Applied through the character controller, so walls and floors still stop it.
        /// </summary>
        public void SetHorizontal(BasisPlayerInputBlend blend, Vector3 value)
        {
            if (CanDrive == false || IsFinite(value) == false)
            {
                return;
            }

            value.y = 0f;
            if (value.magnitude > MaxHorizontal)
            {
                value = value.normalized * MaxHorizontal;
            }

            BasisScriptedPlayerInput.SetHorizontal(Translate(blend), value);
        }

        /// <summary>
        /// Drives the wearer's height in metres. Override sets an absolute height; Additive offsets their
        /// configured height. TRANSIENT — never written to the user's CustomScale / SelectedScale
        /// settings, and their configured size is restored as soon as the script stops driving.
        /// </summary>
        public void SetScale(BasisPlayerInputBlend blend, float height)
        {
            if (CanDrive == false || IsFinite(height) == false)
            {
                return;
            }

            BasisScriptedPlayerInput.SetScale(Translate(blend), Mathf.Clamp(height, -MaxScale, MaxScale));
        }

        public void Clear()
        {
            if (CanDrive == false)
            {
                return;
            }

            BasisScriptedPlayerInput.Clear();
        }

        private static BasisScriptedInputBlend Translate(BasisPlayerInputBlend blend)
        {
            return blend == BasisPlayerInputBlend.Override
                ? BasisScriptedInputBlend.Override
                : BasisScriptedInputBlend.Additive;
        }

        private static bool IsFinite(float value)
        {
            return float.IsNaN(value) == false && float.IsInfinity(value) == false;
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
