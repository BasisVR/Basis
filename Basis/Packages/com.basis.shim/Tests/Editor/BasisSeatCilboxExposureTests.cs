using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using NUnit.Framework;

namespace Basis.Tests.Sync
{
    public sealed class BasisSeatCilboxExposureTests
    {
        const string SeatTypeName = "Basis.Scripts.BasisSdk.Interactions.BasisSeat";

        static HashSet<string> TypeList(Type box)
        {
            FieldInfo field = box.GetField("extraWhiteListType", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"{box.Name} no longer has an extraWhiteListType field; this test cannot see what it exposes.");
            return (HashSet<string>)field.GetValue(null);
        }

        static Dictionary<Type, HashSet<string>> MethodDict(Type box)
        {
            FieldInfo field = box.GetField("extraMethodWhitelist", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"{box.Name} no longer has an extraMethodWhitelist field; this test cannot see what it restricts.");
            return (Dictionary<Type, HashSet<string>>)field.GetValue(null);
        }

        [Test]
        public void TheSceneBox_PinsTheSeatsCallableSurface()
        {
            Assert.IsTrue(TypeList(typeof(CilboxSceneBasis)).Contains(SeatTypeName),
                "BasisSeat is no longer type-whitelisted for scene scripts, so a world cannot reach the "
                + "occupant rotation API that issue #538 asked for.");

            Assert.IsTrue(MethodDict(typeof(CilboxSceneBasis)).ContainsKey(typeof(BasisSeat)),
                "BasisSeat is type-whitelisted with NO method-whitelist entry. The gate is default-allow, so "
                + "that hands sandboxed world scripts the seat's entire public surface — including "
                + "ApplyNetworkedOccupantYaw, SetSeatOccupied and OnEnterSeat.");
        }

        [Test]
        public void TheSceneBox_AllowsTheOccupantRotationApi()
        {
            HashSet<string> allowed = MethodDict(typeof(CilboxSceneBasis))[typeof(BasisSeat)];

            var expected = new[]
            {
                $"get_{nameof(BasisSeat.OccupantRotationRangeDegrees)}",
                $"set_{nameof(BasisSeat.OccupantRotationRangeDegrees)}",
                $"get_{nameof(BasisSeat.OccupantRotationSnapDegrees)}",
                $"set_{nameof(BasisSeat.OccupantRotationSnapDegrees)}",
                $"get_{nameof(BasisSeat.OccupantYawDegrees)}",
                nameof(BasisSeat.TurnOccupant),
                nameof(BasisSeat.SetOccupantYaw),
                nameof(BasisSeat.ResetOccupantYaw),
                $"get_{nameof(BasisSeat.HasOccupant)}",
                $"get_{nameof(BasisSeat.IsAvailable)}",
                $"get_{nameof(BasisSeat.IsLocalPlayerSeated)}",
                nameof(BasisSeat.TryGetOccupant),
                nameof(BasisSeat.TrySeatLocalPlayer),
                nameof(BasisSeat.EjectLocalPlayer),
            };

            foreach (string member in expected)
            {
                Assert.IsTrue(allowed.Contains(member),
                    $"{member} is not callable from a scene script, so a world cannot drive occupant rotation.");
            }
        }

        [Test]
        public void TheSceneBox_BlocksTheAuthorityAndOccupancyPaths()
        {
            HashSet<string> allowed = MethodDict(typeof(CilboxSceneBasis))[typeof(BasisSeat)];

            var forbidden = new[]
            {
                nameof(BasisSeat.ApplyNetworkedOccupantYaw),
                nameof(BasisSeat.SetSeatOccupied),
                nameof(BasisSeat.SetOccupantRecord),
                nameof(BasisSeat.OnEnterSeat),
                nameof(BasisSeat.OnExitSeat),
                nameof(BasisSeat.SetPoints),
                nameof(BasisSeat.GetFitFrame),
                nameof(BasisSeat.CalculateSeatPositionRotation),
                nameof(BasisSeat.HighlightSeat),
                nameof(BasisSeat.OnInteractStart),
                nameof(BasisSeat.OnInteractEnd),
            };

            foreach (string member in forbidden)
            {
                Assert.IsFalse(allowed.Contains(member),
                    $"{member} became callable from a sandboxed world script. It is not part of the occupant "
                    + "rotation surface and it bypasses either the seat's authored limits or the network "
                    + "arbitration.");
            }
        }

        [Test]
        public void AvatarAndPropBoxes_CannotReachSeatsAtAll()
        {
            foreach (Type box in new[] { typeof(CilboxAvatarBasis), typeof(CilboxPropBasis) })
            {
                HashSet<string> types = TypeList(box);

                Assert.IsFalse(types.Contains(SeatTypeName),
                    $"{box.Name} now exposes BasisSeat. Avatar scripts run on remote players' avatars, so "
                    + "this would let anyone turn whoever is sitting near them — the same shape as the "
                    + "BasisLocalPlayer hole already on record.");

                foreach (string entry in types.Where(t => t.EndsWith("*", StringComparison.Ordinal)))
                {
                    string prefix = entry.Substring(0, entry.Length - 1);
                    Assert.IsFalse(SeatTypeName.StartsWith(prefix, StringComparison.Ordinal),
                        $"{box.Name} wildcard \"{entry}\" now covers BasisSeat, exposing it with no method "
                        + "restriction at all.");
                }
            }
        }

        [Test]
        public void TheSeatFitSolver_IsNotExposedToAnyBox()
        {
            foreach (Type box in new[] { typeof(CilboxSceneBasis), typeof(CilboxAvatarBasis), typeof(CilboxPropBasis) })
            {
                foreach (string entry in TypeList(box))
                {
                    Assert.AreNotEqual(typeof(BasisSeatFit).FullName, entry,
                        $"{box.Name} now exposes {nameof(BasisSeatFit)}; scripts should go through the seat.");
                    Assert.AreNotEqual(typeof(BasisSeatRotationLimits).FullName, entry,
                        $"{box.Name} now exposes {nameof(BasisSeatRotationLimits)}; the two float properties "
                        + "on the seat are the authored surface.");
                }
            }
        }
    }
}
