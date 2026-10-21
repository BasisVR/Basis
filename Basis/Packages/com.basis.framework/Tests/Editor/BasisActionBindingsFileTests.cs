using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;
using static BasisActionDriver;

namespace Basis.Framework.Tests
{
    public class BasisActionBindingsFileTests
    {
        [Test]
        public void AFileWithoutUnboundRecordsLoadsTheDefaultsPlusItsRecords()
        {
            BindingWrapper old = JsonUtility.FromJson<BindingWrapper>(
                "{\"records\":[{\"action\":\"SetMovementVectorFromPrimary2DAxis\",\"role\":\"RightHand\"},{\"action\":\"RotateFromPrimary2DAxis\",\"role\":\"LeftHand\"}]}");

            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = Compose(BasisConstants.OpenXRLoader, old);
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> expected = Defaults(BasisConstants.OpenXRLoader);
            Add(expected, ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.RightHand);
            Add(expected, ActionId.RotateFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);

            AssertSame(expected, loaded);
        }

        [Test]
        public void ALeftHandedSwapIsKeptAcrossAReload()
        {
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> live = Defaults(BasisConstants.OpenXRLoader);
            Move(live, ActionId.SetMovementSpeedMultiplierFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand);
            Move(live, ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand);
            Move(live, ActionId.TickMovementSpeed, BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand);
            Move(live, ActionId.RotateFromPrimary2DAxis, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.LeftHand);

            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = RoundTrip(BasisConstants.OpenXRLoader, live);

            AssertSame(live, loaded);
            Assert.That(loaded[ActionId.SetMovementVectorFromPrimary2DAxis], Is.EquivalentTo(new[] { BasisBoneTrackedRole.RightHand }));
            Assert.That(loaded[ActionId.RotateFromPrimary2DAxis], Is.EquivalentTo(new[] { BasisBoneTrackedRole.LeftHand }));
        }

        [Test]
        public void ADefaultThePlayerRemovedStaysRemoved()
        {
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> live = Defaults(BasisConstants.Desktop);
            live.Remove(ActionId.JumpOnPrimaryButton);

            BindingWrapper written = BuildWrapper(BasisConstants.Desktop, live);
            Assert.That(written.unbound, Has.Length.EqualTo(1));
            Assert.That(written.unbound[0].action, Is.EqualTo(ActionId.JumpOnPrimaryButton.ToString()));

            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = RoundTrip(BasisConstants.Desktop, live);
            Assert.That(loaded.ContainsKey(ActionId.JumpOnPrimaryButton), Is.False);
            AssertSame(live, loaded);
        }

        [Test]
        public void ADefaultTheFileNeverMentionsIsStillAdded()
        {
            BindingWrapper written = BuildWrapper(BasisConstants.OpenXRLoader, Defaults(BasisConstants.OpenXRLoader));
            written.records = Without(written.records, ActionId.DescendOnGripButton);
            written.unbound = Without(written.unbound, ActionId.DescendOnGripButton);

            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = Compose(BasisConstants.OpenXRLoader, Parse(written));

            Assert.That(loaded.TryGetValue(ActionId.DescendOnGripButton, out HashSet<BasisBoneTrackedRole> roles), Is.True);
            Assert.That(roles, Is.EquivalentTo(new[] { BasisBoneTrackedRole.RightHand }));
        }

        [Test]
        public void EveryDefaultUnboundLoadsNothing()
        {
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = RoundTrip(BasisConstants.OpenVRLoader, new Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>());

            Assert.That(loaded, Is.Empty);
        }

        [Test]
        public void RecordsThatNameNoActionOrRoleAreSkipped()
        {
            BindingWrapper wrapper = new BindingWrapper
            {
                records = new[]
                {
                    Record("Count", "LeftHand"),
                    Record("99", "LeftHand"),
                    Record("JumpOnPrimaryButton", "NotARole"),
                    Record(null, null),
                },
                unbound = new[] { Record("NotAnAction", "RightHand") },
            };

            AssertSame(Defaults(BasisConstants.Desktop), Compose(BasisConstants.Desktop, wrapper));
        }

        [Test]
        public void TheMicDefaultFollowsTheMode()
        {
            Assert.That(Defaults(BasisConstants.Desktop)[ActionId.ToggleMicOnPrimaryReleaseIfNoHover], Is.EquivalentTo(new[] { BasisBoneTrackedRole.CenterEye }));
            Assert.That(Defaults(BasisConstants.OpenXRLoader)[ActionId.ToggleMicOnPrimaryReleaseIfNoHover], Is.EquivalentTo(new[] { BasisBoneTrackedRole.LeftHand }));

            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> live = Defaults(BasisConstants.Desktop);
            Move(live, ActionId.ToggleMicOnPrimaryReleaseIfNoHover, BasisBoneTrackedRole.CenterEye, BasisBoneTrackedRole.LeftHand);

            AssertSame(live, RoundTrip(BasisConstants.Desktop, live));
        }

        private static Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> RoundTrip(string mode, Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> live)
        {
            return Compose(mode, Parse(BuildWrapper(mode, live)));
        }

        private static BindingWrapper Parse(BindingWrapper wrapper)
        {
            return JsonUtility.FromJson<BindingWrapper>(JsonUtility.ToJson(wrapper, true));
        }

        private static Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> Defaults(string mode)
        {
            return Compose(mode, default);
        }

        private static Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> Compose(string mode, BindingWrapper wrapper)
        {
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> map = new Dictionary<ActionId, HashSet<BasisBoneTrackedRole>>();
            ComposeBindings(mode, wrapper, (action, role) => Add(map, action, role));
            return map;
        }

        private static void Add(Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> map, ActionId action, BasisBoneTrackedRole role)
        {
            if (!map.TryGetValue(action, out HashSet<BasisBoneTrackedRole> roles))
            {
                roles = new HashSet<BasisBoneTrackedRole>();
                map[action] = roles;
            }
            roles.Add(role);
        }

        private static void Move(Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> map, ActionId action, BasisBoneTrackedRole from, BasisBoneTrackedRole to)
        {
            Assert.That(map[action].Remove(from), Is.True, $"{action} was not on {from} to begin with");
            map[action].Add(to);
        }

        private static BasisBindingRecord Record(string action, string role)
        {
            return new BasisBindingRecord { action = action, role = role };
        }

        private static BasisBindingRecord[] Without(BasisBindingRecord[] records, ActionId action)
        {
            List<BasisBindingRecord> kept = new List<BasisBindingRecord>();
            for (int Index = 0; Index < records.Length; Index++)
            {
                if (records[Index].action != action.ToString())
                {
                    kept.Add(records[Index]);
                }
            }
            return kept.ToArray();
        }

        private static void AssertSame(Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> expected, Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> actual)
        {
            Assert.That(Flatten(actual), Is.EquivalentTo(Flatten(expected)));
        }

        private static List<string> Flatten(Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> map)
        {
            List<string> pairs = new List<string>();
            foreach (KeyValuePair<ActionId, HashSet<BasisBoneTrackedRole>> pair in map)
            {
                foreach (BasisBoneTrackedRole role in pair.Value)
                {
                    pairs.Add(pair.Key + "/" + role);
                }
            }
            return pairs;
        }
    }
}
