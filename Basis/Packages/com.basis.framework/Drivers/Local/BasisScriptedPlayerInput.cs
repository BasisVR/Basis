using Basis.BasisUI;
using Basis.Scripts.BasisCharacterController;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// How synthetic input from a sandboxed script combines with the player's real controller input.
    /// </summary>
    public enum BasisScriptedInputBlend
    {
        /// <summary>Script input is summed with the player's own input; the player keeps control.</summary>
        Additive = 0,
        /// <summary>Script input replaces the player's own input for the frames it is driving.</summary>
        Override = 1,
    }

    /// <summary>
    /// Frame-scoped buffer of synthetic locomotion / play-space input supplied by a sandboxed script.
    ///
    /// This type is deliberately NOT whitelisted for cilbox. Sandboxed code reaches it only through
    /// <c>Basis.Shims.BasisPlayspaceInputShim</c>, which gates every write on the writing avatar being
    /// the one the local player is currently wearing. Values expire after
    /// <see cref="FreshnessFrames"/> frames so a script that stops writing releases control instead of
    /// latching it.
    /// </summary>
    public static class BasisScriptedPlayerInput
    {
        private const int FreshnessFrames = 1;

        private static int _locomotionFrame = -1;
        private static int _leftFrame = -1;
        private static int _rightFrame = -1;
        private static int _verticalFrame = -1;

        private static BasisScriptedInputBlend _locomotionBlend;
        private static BasisScriptedInputBlend _leftBlend;
        private static BasisScriptedInputBlend _rightBlend;
        private static BasisScriptedInputBlend _verticalBlend;

        private static Vector2 _movement;
        private static float _turn;
        private static bool _jump;
        private static float _crouchDelta;

        private static Vector3 _leftLocal;
        private static Vector3 _leftUnscaled;
        private static bool _leftGrab;
        private static bool _leftRotate;

        private static Vector3 _rightLocal;
        private static Vector3 _rightUnscaled;
        private static bool _rightGrab;
        private static bool _rightRotate;

        private static float _verticalDelta;
        private static bool _crouchApplied;

        private static int _horizontalFrame = -1;
        private static int _scaleFrame = -1;
        private static BasisScriptedInputBlend _horizontalBlend;
        private static BasisScriptedInputBlend _scaleBlend;
        private static Vector3 _horizontal;
        private static float _scale;

        public static BasisScriptedInputBlend LocomotionBlend => _locomotionBlend;

        public static bool LocomotionActive => IsFresh(_locomotionFrame);
        public static bool VerticalActive => IsFresh(_verticalFrame);
        public static bool HorizontalActive => IsFresh(_horizontalFrame);
        public static bool ScaleActive => IsFresh(_scaleFrame);
        public static bool MoverActive => IsFresh(_leftFrame) || IsFresh(_rightFrame) || VerticalActive || HorizontalActive;

        private static bool IsFresh(int frame)
        {
            if (BasisSettingsDefaults.EnableScriptedPlayerInput.RawValue == false)
            {
                return false;
            }

            return frame >= 0 && Time.frameCount - frame <= FreshnessFrames;
        }

        public static void SetLocomotion(BasisScriptedInputBlend blend, Vector2 movement, float turn, bool jump, float crouchDelta)
        {
            _locomotionBlend = blend;
            _movement = movement;
            _turn = turn;
            _jump = jump;
            _crouchDelta = crouchDelta;
            _locomotionFrame = Time.frameCount;
        }

        public static void SetHand(BasisScriptedInputBlend blend, bool isLeft, Vector3 localPosition, Vector3 unscaledPosition, bool grab, bool rotate)
        {
            if (isLeft)
            {
                _leftBlend = blend;
                _leftLocal = localPosition;
                _leftUnscaled = unscaledPosition;
                _leftGrab = grab;
                _leftRotate = rotate;
                _leftFrame = Time.frameCount;
            }
            else
            {
                _rightBlend = blend;
                _rightLocal = localPosition;
                _rightUnscaled = unscaledPosition;
                _rightGrab = grab;
                _rightRotate = rotate;
                _rightFrame = Time.frameCount;
            }
        }

        public static void SetVerticalDelta(BasisScriptedInputBlend blend, float metres)
        {
            _verticalBlend = blend;
            _verticalDelta = metres;
            _verticalFrame = Time.frameCount;
        }

        public static void SetHorizontal(BasisScriptedInputBlend blend, Vector3 value)
        {
            _horizontalBlend = blend;
            _horizontal = value;
            _horizontalFrame = Time.frameCount;
        }

        public static void SetScale(BasisScriptedInputBlend blend, float height)
        {
            _scaleBlend = blend;
            _scale = height;
            _scaleFrame = Time.frameCount;
        }

        public static bool TryConsumeHorizontal(out BasisScriptedInputBlend blend, out Vector3 value)
        {
            if (IsFresh(_horizontalFrame) == false)
            {
                blend = BasisScriptedInputBlend.Additive;
                value = Vector3.zero;
                return false;
            }

            _horizontalFrame = -1;
            blend = _horizontalBlend;
            value = _horizontal;
            _horizontal = Vector3.zero;
            return true;
        }

        public static bool TryGetScale(out BasisScriptedInputBlend blend, out float height)
        {
            if (IsFresh(_scaleFrame) == false)
            {
                blend = BasisScriptedInputBlend.Additive;
                height = 0f;
                return false;
            }

            blend = _scaleBlend;
            height = _scale;
            return true;
        }

        public static void Clear()
        {
            _locomotionFrame = -1;
            _leftFrame = -1;
            _rightFrame = -1;
            _verticalFrame = -1;
            _horizontalFrame = -1;
            _scaleFrame = -1;
            _horizontal = Vector3.zero;
            _movement = Vector2.zero;
            _turn = 0f;
            _jump = false;
            _crouchDelta = 0f;
            _verticalDelta = 0f;
            _leftGrab = false;
            _leftRotate = false;
            _rightGrab = false;
            _rightRotate = false;
        }

        public static void ApplyLocomotion(BasisLocalCharacterDriver driver)
        {
            if (driver == null)
            {
                return;
            }

            // CrouchBlendDelta is a latched value the real input system clears on its stop event, so a
            // script that sets one and then goes quiet would crouch the player indefinitely. Release it
            // the moment scripted locomotion goes stale.
            if (IsFresh(_locomotionFrame) == false)
            {
                if (_crouchApplied)
                {
                    _crouchApplied = false;
                    driver.SetCrouchBlendDelta(0f);
                }
                return;
            }

            Vector2 movement = _movement;
            float turn = _turn;
            if (_locomotionBlend == BasisScriptedInputBlend.Additive)
            {
                movement += driver.MovementVector;
                turn += driver.Rotation.x;
            }

            if (movement.sqrMagnitude > 1f)
            {
                movement = movement.normalized;
            }

            driver.SetMovementVector(movement);
            driver.Rotation = new Vector2(Mathf.Clamp(turn, -1f, 1f), driver.Rotation.y);
            driver.UpdateMovementSpeed(driver.UseMaxSpeed);

            if (_crouchDelta != 0f)
            {
                driver.SetCrouchBlendDelta(_crouchDelta);
                _crouchApplied = true;
            }
            else if (_crouchApplied)
            {
                _crouchApplied = false;
                driver.SetCrouchBlendDelta(0f);
            }

            // Consumed rather than left to the freshness window: jump is an edge, and re-applying it on
            // the trailing frame turns one scripted request into a double jump.
            if (_jump)
            {
                _jump = false;
                driver.HandleJumpRequest();
            }
        }

        public static bool TryGetHand(bool isLeft, out BasisScriptedInputBlend blend, out Vector3 localPosition, out Vector3 unscaledPosition, out bool grab, out bool rotate)
        {
            if (isLeft && IsFresh(_leftFrame))
            {
                blend = _leftBlend;
                localPosition = _leftLocal;
                unscaledPosition = _leftUnscaled;
                grab = _leftGrab;
                rotate = _leftRotate;
                return true;
            }

            if (isLeft == false && IsFresh(_rightFrame))
            {
                blend = _rightBlend;
                localPosition = _rightLocal;
                unscaledPosition = _rightUnscaled;
                grab = _rightGrab;
                rotate = _rightRotate;
                return true;
            }

            blend = BasisScriptedInputBlend.Additive;
            localPosition = Vector3.zero;
            unscaledPosition = Vector3.zero;
            grab = false;
            rotate = false;
            return false;
        }

        /// <summary>
        /// Folds any pending scripted vertical into <paramref name="verticalOffset"/> exactly once per
        /// write: Additive nudges the current offset, Override pins it to an absolute height.
        /// </summary>
        public static void ConsumeVertical(ref float verticalOffset)
        {
            if (IsFresh(_verticalFrame) == false)
            {
                return;
            }

            _verticalFrame = -1;
            verticalOffset = _verticalBlend == BasisScriptedInputBlend.Override
                ? _verticalDelta
                : verticalOffset + _verticalDelta;
            _verticalDelta = 0f;
        }
    }
}
