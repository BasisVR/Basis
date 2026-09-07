using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Framework.Tests
{
    public class BasisLocomotionOverrideTests
    {
        private const string WorldKey = "World";
        private const string ZoneKey = "Zone";

        private static BasisLocomotionBaseline Baseline => new BasisLocomotionBaseline
        {
            JumpHeight = 1.0f,
            WalkSpeed = 2.5f,
            RunSpeed = 4.0f,
            MinimumSpeed = 0.5f,
            Gravity = -9.81f,
            Mode = BasisLocalCharacterDriver.Mode.Walk,
        };

        private static BasisLocomotionValues Walk(float speed) => new BasisLocomotionValues
        {
            Fields = BasisLocomotionField.WalkSpeed,
            WalkSpeed = speed,
        };

        private static BasisLocomotionValues Jump(float height) => new BasisLocomotionValues
        {
            Fields = BasisLocomotionField.JumpHeight,
            JumpHeight = height,
        };

        private static BasisLocomotionValues ModeOf(BasisLocalCharacterDriver.Mode mode) => new BasisLocomotionValues
        {
            Fields = BasisLocomotionField.Mode,
            Mode = mode,
        };

        [SetUp]
        public void SetUp() => BasisLocomotionOverrides.RemoveAll(true);

        [TearDown]
        public void TearDown() => BasisLocomotionOverrides.RemoveAll(true);

        [Test]
        public void EmptyStackResolvesToNothing()
        {
            BasisLocomotionValues resolved = BasisLocomotionOverrides.Resolve();

            Assert.That(resolved.Fields, Is.EqualTo(BasisLocomotionField.None));
            Assert.That(BasisLocomotionOverrides.Count, Is.Zero);
        }

        [Test]
        public void UnclaimedFieldsFallBackToBaseline()
        {
            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(default, Baseline);

            Assert.That(effective.JumpHeight, Is.EqualTo(1.0f));
            Assert.That(effective.WalkSpeed, Is.EqualTo(2.5f));
            Assert.That(effective.RunSpeed, Is.EqualTo(4.0f));
            Assert.That(effective.MinimumSpeed, Is.EqualTo(0.5f));
            Assert.That(effective.Gravity, Is.EqualTo(-9.81f));
            Assert.That(effective.Mode, Is.EqualTo(BasisLocalCharacterDriver.Mode.Walk));
        }

        [Test]
        public void LastRegisteredKeyWinsTheField()
        {
            BasisLocomotionOverrides.Set(WorldKey, Walk(1.2f));
            BasisLocomotionOverrides.Set(ZoneKey, Walk(6.0f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(6.0f));
        }

        [Test]
        public void RemovingAKeyRestoresTheValueBeneathIt()
        {
            BasisLocomotionOverrides.Set(WorldKey, Walk(1.2f));
            BasisLocomotionOverrides.Set(ZoneKey, Walk(6.0f));

            Assert.That(BasisLocomotionOverrides.Remove(ZoneKey), Is.True);

            BasisLocomotionValues resolved = BasisLocomotionOverrides.Resolve();
            Assert.That(resolved.Has(BasisLocomotionField.WalkSpeed), Is.True);
            Assert.That(resolved.WalkSpeed, Is.EqualTo(1.2f));
        }

        [Test]
        public void RemovingTheLastKeyReturnsTheFieldToBaseline()
        {
            BasisLocomotionOverrides.Set(WorldKey, Walk(1.2f));
            BasisLocomotionOverrides.Remove(WorldKey);

            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(BasisLocomotionOverrides.Resolve(), Baseline);
            Assert.That(effective.WalkSpeed, Is.EqualTo(2.5f));
        }

        [Test]
        public void KeysLayerPerFieldRatherThanWholesale()
        {
            BasisLocomotionOverrides.Set(WorldKey, Jump(3.0f));
            BasisLocomotionOverrides.Set(ZoneKey, Walk(1.2f));

            BasisLocomotionValues resolved = BasisLocomotionOverrides.Resolve();
            Assert.That(resolved.JumpHeight, Is.EqualTo(3.0f));
            Assert.That(resolved.WalkSpeed, Is.EqualTo(1.2f));
        }

        [Test]
        public void RepeatedSetsUnderOneKeyAccumulateFields()
        {
            BasisLocomotionOverrides.Set(WorldKey, Jump(3.0f));
            BasisLocomotionOverrides.Set(WorldKey, Walk(1.2f));

            BasisLocomotionValues resolved = BasisLocomotionOverrides.Resolve();
            Assert.That(BasisLocomotionOverrides.Count, Is.EqualTo(1));
            Assert.That(resolved.Fields, Is.EqualTo(BasisLocomotionField.JumpHeight | BasisLocomotionField.WalkSpeed));
            Assert.That(resolved.JumpHeight, Is.EqualTo(3.0f));
        }

        [Test]
        public void ClearingTheLastFieldDropsTheEntry()
        {
            BasisLocomotionOverrides.Set(WorldKey, Jump(3.0f));

            Assert.That(BasisLocomotionOverrides.ClearField(WorldKey, BasisLocomotionField.JumpHeight), Is.True);
            Assert.That(BasisLocomotionOverrides.Count, Is.Zero);
        }

        [Test]
        public void AdminPriorityOutranksAWorldKeyRegisteredAfterIt()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Walk(0.0f));
            BasisLocomotionOverrides.Set(WorldKey, Walk(8.0f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(0.0f));
        }

        [Test]
        public void ClearingContentOverridesLeavesTheAdminEntryStanding()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Walk(0.0f));
            BasisLocomotionOverrides.Set(WorldKey, Jump(3.0f));

            BasisLocomotionOverrides.RemoveAll(false);

            Assert.That(BasisLocomotionOverrides.Contains(BasisLocomotionOverrides.AdminKey), Is.True);
            Assert.That(BasisLocomotionOverrides.Contains(WorldKey), Is.False);
            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(0.0f));
        }

        [Test]
        public void ModeOverrideResolvesAndReleases()
        {
            BasisLocomotionOverrides.Set(WorldKey, ModeOf(BasisLocalCharacterDriver.Mode.Fly));
            Assert.That(BasisLocomotionOverrides.Flatten(BasisLocomotionOverrides.Resolve(), Baseline).Mode,
                Is.EqualTo(BasisLocalCharacterDriver.Mode.Fly));

            BasisLocomotionOverrides.Remove(WorldKey);
            Assert.That(BasisLocomotionOverrides.Flatten(BasisLocomotionOverrides.Resolve(), Baseline).Mode,
                Is.EqualTo(BasisLocalCharacterDriver.Mode.Walk));
        }

        [Test]
        public void WalkSpeedBelowTheBaselineFloorDragsTheFloorDown()
        {
            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(Walk(0.2f), Baseline);

            Assert.That(effective.WalkSpeed, Is.EqualTo(0.2f));
            Assert.That(effective.MinimumSpeed, Is.LessThanOrEqualTo(0.2f));
        }

        [Test]
        public void RunSpeedIsNeverBelowWalkSpeed()
        {
            BasisLocomotionValues values = new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.WalkSpeed | BasisLocomotionField.RunSpeed,
                WalkSpeed = 6.0f,
                RunSpeed = 1.0f,
            };

            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(values, Baseline);
            Assert.That(effective.RunSpeed, Is.GreaterThanOrEqualTo(effective.WalkSpeed));
        }

        [Test]
        public void FrozenSpeedsKeepANonZeroBandSoTheUnlerpCannotDivideByZero()
        {
            BasisLocomotionValues values = new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.WalkSpeed | BasisLocomotionField.RunSpeed,
                WalkSpeed = 0f,
                RunSpeed = 0f,
            };

            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(values, Baseline);

            Assert.That(effective.WalkSpeed, Is.Zero);
            Assert.That(effective.RunSpeed - effective.MinimumSpeed,
                Is.GreaterThanOrEqualTo(BasisLocomotionOverrides.MinimumSpeedSpan));
        }

        [Test]
        public void NegativeSpeedsAndJumpHeightsAreFlooredAtZero()
        {
            BasisLocomotionEffective effective = BasisLocomotionOverrides.Flatten(Walk(-5f), Baseline);
            Assert.That(effective.WalkSpeed, Is.Zero);

            Assert.That(BasisLocomotionOverrides.Flatten(Jump(-5f), Baseline).JumpHeight, Is.Zero);
        }

        [Test]
        public void PositiveGravityIsClampedSoTheJumpRootStaysReal()
        {
            BasisLocomotionValues values = new BasisLocomotionValues
            {
                Fields = BasisLocomotionField.Gravity,
                Gravity = 9.81f,
            };

            Assert.That(BasisLocomotionOverrides.Flatten(values, Baseline).Gravity, Is.LessThanOrEqualTo(0f));
        }

        [Test]
        public void NonFiniteValuesFallBackToBaseline()
        {
            Assert.That(BasisLocomotionOverrides.Flatten(Walk(float.NaN), Baseline).WalkSpeed, Is.EqualTo(2.5f));
            Assert.That(BasisLocomotionOverrides.Flatten(Jump(float.PositiveInfinity), Baseline).JumpHeight, Is.EqualTo(1.0f));
        }

        [Test]
        public void AnEmptyFieldMaskIsIgnoredRatherThanStored()
        {
            BasisLocomotionOverrides.Set(WorldKey, default);

            Assert.That(BasisLocomotionOverrides.Count, Is.Zero);
        }

        [Test]
        public void BlankKeysAreRejected()
        {
            LogAssert.Expect(LogType.Error, new Regex("Locomotion override rejected"));
            BasisLocomotionOverrides.Set("   ", Walk(1f));

            Assert.That(BasisLocomotionOverrides.Count, Is.Zero);
            Assert.That(BasisLocomotionOverrides.Remove(null), Is.False);
        }

        [Test]
        public void VersionAdvancesOnEveryMutationSoTheDriverReapplies()
        {
            int start = BasisLocomotionOverrides.Version;

            BasisLocomotionOverrides.Set(WorldKey, Walk(1.2f));
            int afterSet = BasisLocomotionOverrides.Version;
            Assert.That(afterSet, Is.GreaterThan(start));

            BasisLocomotionOverrides.Remove(WorldKey);
            Assert.That(BasisLocomotionOverrides.Version, Is.GreaterThan(afterSet));
        }

        [Test]
        public void ResolveOrdersByPriorityThenRegistration()
        {
            BasisLocomotionOverrides.Set("A", 5, Walk(1f));
            BasisLocomotionOverrides.Set("B", 1, Walk(2f));
            BasisLocomotionOverrides.Set("C", 5, Walk(3f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(3f));

            List<string> keys = BasisLocomotionOverrides.ToList();
            Assert.That(keys, Has.Count.EqualTo(3));
        }

        [Test]
        public void TheAdminKeyIsReservedAndOtherKeysAreNot()
        {
            Assert.That(BasisLocomotionOverrides.IsReservedKey(BasisLocomotionOverrides.AdminKey), Is.True);
            Assert.That(BasisLocomotionOverrides.IsReservedKey(WorldKey), Is.False);
        }

        [Test]
        public void TheServerPolicyKeyIsReservedToo()
        {
            Assert.That(BasisLocomotionOverrides.IsReservedKey(BasisLocomotionOverrides.ServerPolicyKey), Is.True);
        }

        [Test]
        public void TheServerPolicyOutranksWorldContentRegisteredAfterIt()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));
            BasisLocomotionOverrides.Set(WorldKey, Walk(8.0f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(1.5f));
        }

        [Test]
        public void AModeratorOverrideWinsOverTheServerPolicy()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Walk(0.0f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(0.0f));

            // Order of registration must not decide it — priority does.
            BasisLocomotionOverrides.RemoveAll(true);
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Walk(0.0f));
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));

            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(0.0f));
        }

        [Test]
        public void TheServerPolicyStillCoversFieldsTheModeratorDidNotClaim()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Jump(3.0f));

            BasisLocomotionValues resolved = BasisLocomotionOverrides.Resolve();
            Assert.That(resolved.WalkSpeed, Is.EqualTo(1.5f));
            Assert.That(resolved.JumpHeight, Is.EqualTo(3.0f));
        }

        [Test]
        public void ClearingContentOverridesLeavesTheServerPolicyStanding()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));
            BasisLocomotionOverrides.Set(WorldKey, Jump(3.0f));

            BasisLocomotionOverrides.RemoveAll(false);

            Assert.That(BasisLocomotionOverrides.Contains(BasisLocomotionOverrides.ServerPolicyKey), Is.True);
            Assert.That(BasisLocomotionOverrides.Contains(WorldKey), Is.False);
            Assert.That(BasisLocomotionOverrides.Resolve().WalkSpeed, Is.EqualTo(1.5f));
        }

        [Test]
        public void DisconnectingDropsTheServerPolicyWithEverythingElse()
        {
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.ServerPolicyKey, BasisLocomotionOverrides.ServerPolicyPriority, Walk(1.5f));
            BasisLocomotionOverrides.Set(BasisLocomotionOverrides.AdminKey, BasisLocomotionOverrides.AdminPriority, Jump(3.0f));

            BasisLocomotionOverrides.RemoveAll(true);

            Assert.That(BasisLocomotionOverrides.Count, Is.EqualTo(0));
        }
    }
}
