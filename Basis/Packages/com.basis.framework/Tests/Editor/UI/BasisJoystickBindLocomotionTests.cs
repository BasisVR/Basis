using System.Reflection;
using System.Runtime.Serialization;
using Basis.BasisUI;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.UI
{
    /// <summary>
    /// A thumbstick handed to a settings slider must stop driving the player.
    ///
    /// <para>The gate used to drop only forward/back, which left the same stick strafing through
    /// <c>MovementVector.x</c> and turning through <c>Rotation.x</c> — snap turn reads that axis
    /// and so was never suppressed at all — and left <c>IsLocomoting</c> true for the length of a
    /// sweep, which is what unplants the feet. A stick is never pushed exactly forward, so every
    /// sweep leaked some of it.</para>
    ///
    /// <para>Driven by reflection: the bind's state and the dispatch role are both private, and
    /// the alternative is standing up a local player, a device and a live panel to assert one
    /// axis. Both are restored in teardown so the statics do not leak into other fixtures.</para>
    /// </summary>
    public class BasisJoystickBindLocomotionTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly FieldInfo BindingField = typeof(BasisPanelJoystickBind).GetField("_binding", Private);
        private static readonly FieldInfo RoleField = typeof(BasisPanelJoystickBind).GetField("_role", Private);
        private static readonly FieldInfo DispatchRoleField = typeof(BasisActionDriver).GetField("s_DispatchRole", Private);
        private static readonly MethodInfo LocomotionAxisMethod = typeof(BasisActionDriver).GetMethod("LocomotionAxis", Private);

        private object _savedBinding;
        private object _savedRole;
        private object _savedDispatchRole;

        [SetUp]
        public void SetUp()
        {
            Assert.That(BindingField, Is.Not.Null, "BasisPanelJoystickBind._binding was renamed; this test needs updating.");
            Assert.That(RoleField, Is.Not.Null, "BasisPanelJoystickBind._role was renamed; this test needs updating.");
            Assert.That(DispatchRoleField, Is.Not.Null, "BasisActionDriver.s_DispatchRole was renamed; this test needs updating.");
            Assert.That(LocomotionAxisMethod, Is.Not.Null, "BasisActionDriver.LocomotionAxis was renamed; this test needs updating.");

            _savedBinding = BindingField.GetValue(null);
            _savedRole = RoleField.GetValue(null);
            _savedDispatchRole = DispatchRoleField.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            BindingField.SetValue(null, _savedBinding);
            RoleField.SetValue(null, _savedRole);
            DispatchRoleField.SetValue(null, _savedDispatchRole);
        }

        /// <summary>
        /// Puts a bind on <paramref name="role"/> without a slider, a device, or a loaded settings
        /// store behind it. The binding is left unconstructed on purpose: what is under test only
        /// asks whether a stick is spoken for, and building a real one would pull the settings
        /// system into a fixture that has nothing to do with it.
        /// </summary>
        private static void BindStickTo(BasisBoneTrackedRole role)
        {
            BindingField.SetValue(null, FormatterServices.GetUninitializedObject(BindingField.FieldType));
            RoleField.SetValue(null, role);
        }

        private static Vector2 DispatchedAxis(BasisBoneTrackedRole role, Vector2 axis)
        {
            DispatchRoleField.SetValue(null, role);
            return (Vector2)LocomotionAxisMethod.Invoke(null, new object[] { axis });
        }

        [Test]
        public void AnUnboundStickIsPassedThrough()
        {
            BindingField.SetValue(null, null);

            Vector2 pushed = new Vector2(0.4f, 0.9f);
            Assert.That(DispatchedAxis(BasisBoneTrackedRole.LeftHand, pushed), Is.EqualTo(pushed));
        }

        [Test]
        public void ABoundStickDrivesNothing()
        {
            BindStickTo(BasisBoneTrackedRole.LeftHand);

            // A real sweep: pushed forward to move the value, with the sideways wobble a thumb
            // always adds. Every component of it has to be dropped, not just the forward one.
            Assert.That(DispatchedAxis(BasisBoneTrackedRole.LeftHand, new Vector2(0.3f, 0.9f)), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void ABoundStickCannotStrafe()
        {
            BindStickTo(BasisBoneTrackedRole.LeftHand);

            // x alone is what reaches MovementVector.x. It survived the old gate untouched.
            Assert.That(DispatchedAxis(BasisBoneTrackedRole.LeftHand, new Vector2(1f, 0f)), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void ABoundStickCannotTurn()
        {
            BindStickTo(BasisBoneTrackedRole.RightHand);

            // Turning reads the same x, so the old gate suppressed none of it.
            Assert.That(DispatchedAxis(BasisBoneTrackedRole.RightHand, new Vector2(-1f, 0.2f)), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void TheOtherHandKeepsItsStick()
        {
            BindStickTo(BasisBoneTrackedRole.LeftHand);

            Vector2 pushed = new Vector2(0.6f, -0.8f);
            Assert.That(DispatchedAxis(BasisBoneTrackedRole.RightHand, pushed), Is.EqualTo(pushed),
                "A bind costs locomotion on the hand that was given away and nothing else.");
        }
    }
}
