using System;
using System.Collections.Generic;
using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    public class BasisCameraModifierStackTests
    {
        [Test]
        public void AFreshStackHandsBothChannelsToTheOperator()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();

            Assert.That(stack.positionModifier, Is.EqualTo(BasisCameraPositionModifier.FreeFly));
            Assert.That(stack.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.Hold));
            Assert.That(stack.DrivesAnything, Is.False,
                "A camera that has never been configured must sit in your hand, not fly off.");
        }

        [Test]
        public void AnEffectCanOnlyBeFittedOnce()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();

            Assert.That(stack.AddEffect(BasisCameraEffectModifier.Shake), Is.True);
            Assert.That(stack.AddEffect(BasisCameraEffectModifier.Shake), Is.False);
            Assert.That(stack.EffectCount, Is.EqualTo(1));
        }

        [Test]
        public void RemovingAnEffectThatIsNotFittedChangesNothing()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.AddEffect(BasisCameraEffectModifier.Shake);

            Assert.That(stack.RemoveEffect(BasisCameraEffectModifier.LookAhead), Is.False);
            Assert.That(stack.EffectCount, Is.EqualTo(1));
            Assert.That(stack.HasEffect(BasisCameraEffectModifier.Shake), Is.True);
        }

        [Test]
        public void FittingAnyEffectMakesTheStackDriveTheCamera()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.AddEffect(BasisCameraEffectModifier.Shake);

            Assert.That(stack.DrivesPosition, Is.False);
            Assert.That(stack.DrivesRotation, Is.False);
            Assert.That(stack.DrivesAnything, Is.True,
                "Shake on a hand-flown camera is still the stack doing something.");
        }

        [Test]
        public void SanitizeDropsAnUnknownModifier()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = (BasisCameraPositionModifier)99,
                rotationModifier = (BasisCameraRotationModifier)99,
            };

            stack.Sanitize();

            Assert.That(stack.positionModifier, Is.EqualTo(BasisCameraPositionModifier.FreeFly));
            Assert.That(stack.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.Hold));
        }

        [Test]
        public void SanitizeDropsAnUnknownAimPoint()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.subject.aimPoint = (BasisCameraAimPoint)99;

            stack.Sanitize();

            Assert.That(stack.subject.aimPoint, Is.EqualTo(BasisCameraAimPoint.Normal));
        }

        [Test]
        public void SanitizeDropsDuplicateAndUnknownEffects()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.effects = new List<int>
            {
                (int)BasisCameraEffectModifier.Shake,
                (int)BasisCameraEffectModifier.Shake,
                99,
                (int)BasisCameraEffectModifier.LookAhead,
            };

            stack.Sanitize();

            Assert.That(stack.EffectCount, Is.EqualTo(2));
            Assert.That(stack.HasEffect(BasisCameraEffectModifier.Shake), Is.True);
            Assert.That(stack.HasEffect(BasisCameraEffectModifier.LookAhead), Is.True);
        }

        [Test]
        public void SanitizeRepairsValuesAHandEditedFileCouldMakeHarmful()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            stack.follow.damping = new Vector3(-1f, -2f, -3f);
            stack.follow.teleportDistance = 0f;
            stack.follow.lateralTracking = 5f;
            stack.framing.maxDistance = 0.01f;
            stack.occlusion.probeRadius = 0f;
            stack.lens.fov = 500f;

            stack.Sanitize();

            Assert.That(stack.follow.damping.x, Is.GreaterThanOrEqualTo(0f));
            Assert.That(stack.follow.teleportDistance, Is.GreaterThan(0f),
                "A zero snap distance would make the camera cut every frame.");
            Assert.That(stack.follow.lateralTracking, Is.EqualTo(1f));
            Assert.That(stack.framing.maxDistance, Is.GreaterThanOrEqualTo(stack.framing.minDistance));
            Assert.That(stack.occlusion.probeRadius, Is.GreaterThan(0f));
            Assert.That(stack.lens.fov, Is.LessThanOrEqualTo(120f));
        }

        [Test]
        public void AStackUnityNeverStoredComesBackAsTheShippedDefaults()
        {
            // Unity fills a serialized class field by type default rather than by constructor, so a
            // prefab that predates the stack hands one over with every number zeroed. Without the
            // repair that is a follow offset of (0,0,0) and a snap distance of 0.
            BasisCameraModifierStack zeroed =
                (BasisCameraModifierStack)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(BasisCameraModifierStack));

            zeroed.Sanitize();

            Assert.That(zeroed.follow.positionOffset, Is.EqualTo(new Vector3(0.5f, 0f, 1.4f)));
            Assert.That(zeroed.follow.teleportDistance, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(zeroed.DrivesAnything, Is.False);
        }

        [Test]
        public void CloningProducesAnIndependentCopy()
        {
            BasisCameraModifierStack original = BasisCameraSettingsRig.DistinctiveModifiers();
            BasisCameraModifierStack copy = original.Clone();

            copy.follow.positionOffset = new Vector3(9f, 9f, 9f);
            copy.AddEffect(BasisCameraEffectModifier.AvoidOcclusion);

            Assert.That(original.follow.positionOffset, Is.Not.EqualTo(new Vector3(9f, 9f, 9f)));
            Assert.That(original.HasEffect(BasisCameraEffectModifier.AvoidOcclusion), Is.False,
                "The clone must not share the effects list.");
        }

        [Test]
        public void MatchesNoticesADifferenceInEveryBlock()
        {
            BasisCameraModifierStack left = BasisCameraSettingsRig.DistinctiveModifiers();
            Assert.That(BasisCameraModifierStack.Matches(left, left.Clone()), Is.True);

            Action<BasisCameraModifierStack>[] perturbations =
            {
                s => s.subject.modifier = BasisCameraSubjectModifier.FixedPoint,
                s => s.subject.aimPoint = BasisCameraAimPoint.FullBody,
                s => s.subject.anchorToBody = !s.subject.anchorToBody,
                s => s.subject.groupIncludesLocal = !s.subject.groupIncludesLocal,
                s => s.subject.aimHeightOffset += 1f,
                s => s.subject.framingRadius += 0.5f,
                s => s.subject.fixedPoint += Vector3.one,
                s => s.positionModifier = BasisCameraPositionModifier.LockedOff,
                s => s.rotationModifier = BasisCameraRotationModifier.LookAtSubject,
                s => s.AddEffect(BasisCameraEffectModifier.AvoidOcclusion),
                s => s.follow.positionOffset += Vector3.one,
                s => s.follow.bindingMode = BasisCameraBindingMode.SubjectYaw,
                s => s.follow.damping += Vector3.one,
                s => s.follow.lateralTracking = 0.1f,
                s => s.follow.teleportDistance += 5f,
                s => s.framing.directionOffset += Vector3.one,
                s => s.framing.screenFraction = 0.9f,
                s => s.framing.usesZoom = !s.framing.usesZoom,
                s => s.framing.minDistance += 1f,
                s => s.framing.maxDistance += 1f,
                s => s.framing.teleportDistance += 1f,
                s => s.dolly.position += 1f,
                s => s.dolly.mode = BasisCameraDollyMode.FollowSubject,
                s => s.dolly.playing = !s.dolly.playing,
                s => s.dolly.damping += 1f,
                s => s.dolly.speed += 1f,
                s => s.dolly.offset += Vector3.one,
                s => s.orbit.heading += 10f,
                s => s.orbit.verticalAxis = 0.1f,
                s => s.orbit.headingDamping += 1f,
                s => s.orbit.verticalDamping += 1f,
                s => s.orbit.followSubjectHeading = !s.orbit.followSubjectHeading,
                s => s.orbit.top = new BasisCameraOrbitRig(9f, 9f),
                s => s.orbit.middle = new BasisCameraOrbitRig(9f, 9f),
                s => s.orbit.bottom = new BasisCameraOrbitRig(9f, 9f),
                s => s.lookAt.rotationOffset += Vector3.one,
                s => s.lookAt.damping += Vector3.one,
                s => s.compose.rotationOffset += Vector3.one,
                s => s.compose.composer.screenX = 0.9f,
                s => s.compose.composer.screenY = 0.9f,
                s => s.compose.composer.deadZoneWidth = 0.9f,
                s => s.compose.composer.deadZoneHeight = 0.9f,
                s => s.compose.composer.softZoneWidth = 1.9f,
                s => s.compose.composer.softZoneHeight = 1.9f,
                s => s.compose.composer.biasX = -0.4f,
                s => s.compose.composer.biasY = 0.4f,
                s => s.compose.composer.horizontalDamping += 1f,
                s => s.compose.composer.verticalDamping += 1f,
                s => s.matchSubject.rotationOffset += Vector3.one,
                s => s.matchSubject.damping += Vector3.one,
                s => s.trackAim.rotationOffset += Vector3.one,
                s => s.trackAim.damping += Vector3.one,
                s => s.lookAhead.time += 1f,
                s => s.lookAhead.limit += 1f,
                s => s.occlusion.padding += 1f,
                s => s.occlusion.minDistance += 1f,
                s => s.occlusion.returnDamping += 1f,
                s => s.occlusion.probeRadius = 0.05f,
                s => s.shake.amplitudeGain += 1f,
                s => s.shake.frequencyGain += 1f,
                s => s.shake.profile = BasisCameraNoiseProfile.Shaky,
                s => s.shake.positionAmplitude += Vector3.one,
                s => s.shake.rotationFrequency += Vector3.one,
                s => s.lens.fov += 10f,
                s => s.lens.damping += 1f,
                s => s.steady.smoothing += 1f,
                s => s.steady.verticalDeadZone += 1f,
                s => s.collision.radius = 0.05f,
                s => s.collision.padding += 1f,
                s => s.dollyZoom.minFov += 5f,
                s => s.dollyZoom.maxFov += 5f,
                s => s.rigWeight.responsiveness += 1f,
                s => s.rigWeight.bounce = 0.1f,
            };

            for (int Index = 0; Index < perturbations.Length; Index++)
            {
                BasisCameraModifierStack moved = left.Clone();
                perturbations[Index](moved);

                Assert.That(BasisCameraModifierStack.Matches(left, moved), Is.False,
                    $"Perturbation {Index} went unnoticed, so a saved mode will go on claiming a " +
                    "value that has since been changed.");
            }
        }

        [Test]
        public void EveryModifierInTheCatalogueHasANameAndADescription()
        {
            foreach (BasisCameraPositionModifier modifier in BasisCameraModifiers.PositionModifiers)
            {
                Assert.That(BasisCameraModifiers.NameKey(modifier), Is.Not.Empty);
                Assert.That(BasisCameraModifiers.DescriptionKey(modifier), Does.EndWith(".description"));
            }

            foreach (BasisCameraRotationModifier modifier in BasisCameraModifiers.RotationModifiers)
            {
                Assert.That(BasisCameraModifiers.NameKey(modifier), Is.Not.Empty);
                Assert.That(BasisCameraModifiers.DescriptionKey(modifier), Does.EndWith(".description"));
            }

            foreach (BasisCameraEffectDescriptor descriptor in BasisCameraModifiers.Effects)
            {
                Assert.That(descriptor.NameKey, Is.Not.Empty);
                Assert.That(descriptor.DescriptionKey, Does.EndWith(".description"));
                Assert.That(descriptor.Channel, Is.Not.EqualTo(BasisCameraModifierChannel.None),
                    "An effect that writes nothing has nothing to be.");
            }
        }

        [Test]
        public void TheCatalogueListsEveryModifierTheEnumDefines()
        {
            Assert.That(BasisCameraModifiers.PositionModifiers.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraPositionModifier)).Length),
                "A modifier missing from the catalogue never reaches the panel's dropdown.");
            Assert.That(BasisCameraModifiers.RotationModifiers.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraRotationModifier)).Length));
            Assert.That(BasisCameraModifiers.Effects.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraEffectModifier)).Length));
            Assert.That(BasisCameraModifiers.SubjectModifiers.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraSubjectModifier)).Length));
            Assert.That(BasisCameraModifiers.AimPoints.Length,
                Is.EqualTo(Enum.GetValues(typeof(BasisCameraAimPoint)).Length));
        }

        [Test]
        public void EffectsRunInStageOrderRatherThanTheOrderTheyWereAdded()
        {
            // Occlusion has to see the solved position and shake has to be the last thing on top of
            // it, so the stage is fixed per effect and not something the list can reorder.
            Assert.That(BasisCameraModifiers.StageOf(BasisCameraEffectModifier.LookAhead),
                Is.EqualTo(BasisCameraEffectStage.Subject));
            Assert.That(BasisCameraModifiers.StageOf(BasisCameraEffectModifier.AvoidOcclusion),
                Is.EqualTo(BasisCameraEffectStage.Position));
            Assert.That(BasisCameraModifiers.StageOf(BasisCameraEffectModifier.LensOverride),
                Is.EqualTo(BasisCameraEffectStage.Lens));
            Assert.That(BasisCameraModifiers.StageOf(BasisCameraEffectModifier.Shake),
                Is.EqualTo(BasisCameraEffectStage.Output));
        }

        [Test]
        public void OnlyTheModifiersThatFilmSomebodyNeedASubject()
        {
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.FollowSubject), Is.True);
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.Orbit), Is.True);
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.FrameSubject), Is.True);

            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.FreeFly), Is.False);
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.LockedOff), Is.False);
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraPositionModifier.DollyTrack), Is.False,
                "A track is authored in the world, so it rides whether or not anybody is being filmed.");

            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraRotationModifier.Hold), Is.False);
            Assert.That(BasisCameraModifiers.NeedsSubject(BasisCameraRotationModifier.AimAlongTrack), Is.False,
                "It reads the path, not a person, so it has to survive an empty subject slot.");
        }

        [Test]
        public void CopyFromTakesTheWholeStackWithoutSharingIt()
        {
            BasisCameraModifierStack source = BasisCameraSettingsRig.DistinctiveModifiers();
            BasisCameraModifierStack target = new BasisCameraModifierStack();

            target.CopyFrom(source);

            Assert.That(BasisCameraModifierStack.Matches(source, target), Is.True);

            target.AddEffect(BasisCameraEffectModifier.AvoidOcclusion);
            Assert.That(source.HasEffect(BasisCameraEffectModifier.AvoidOcclusion), Is.False);
        }

        [Test]
        public void TheLegacyFollowBlockUpgradesOntoTheStackWithoutArmingIt()
        {
            BasisCameraLegacyFollow legacy = new BasisCameraLegacyFollow
            {
                autoFollowPositionOffset = new Vector3(1f, 2f, 3f),
                autoFollowRotationOffset = new Vector3(4f, 5f, 6f),
                autoFollowPlayspace = false,
                autoFollowLookAtHeightOffset = -0.5f,
                autoFollowLateralTracking = 0.25f,
                subjectFramingRadius = 0.9f,
            };

            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            legacy.ApplyTo(stack);

            Assert.That(stack.follow.positionOffset, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(stack.follow.lateralTracking, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(stack.lookAt.rotationOffset, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            Assert.That(stack.subject.anchorToBody, Is.False);
            Assert.That(stack.subject.aimHeightOffset, Is.EqualTo(-0.5f).Within(1e-4f));
            Assert.That(stack.subject.framingRadius, Is.EqualTo(0.9f).Within(1e-4f));

            Assert.That(stack.DrivesAnything, Is.False,
                "Whether follow was armed was never saved, so an upgraded file must stay put.");
        }

        [Test]
        public void ALegacyFramingRadiusOfZeroIsNotCarriedOver()
        {
            // Zero would dolly the camera into the subject's face the moment Frame Subject was used.
            BasisCameraLegacyFollow legacy = new BasisCameraLegacyFollow { subjectFramingRadius = 0f };

            BasisCameraModifierStack stack = new BasisCameraModifierStack();
            legacy.ApplyTo(stack);

            Assert.That(stack.subject.framingRadius, Is.GreaterThan(0f));
        }

        [Test]
        public void UnreadableLegacyTextUpgradesToTheDefaultsRatherThanThrowing()
        {
            Assert.That(BasisCameraLegacyFollow.TryRead(null, out _), Is.False);
            Assert.That(BasisCameraLegacyFollow.TryRead(string.Empty, out _), Is.False);
        }

        [Test]
        public void NobodyToFilmUnfitsEverythingThatNeedsSomebody()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.FollowSubject,
                rotationModifier = BasisCameraRotationModifier.LookAtSubject,
            };
            stack.dolly.mode = BasisCameraDollyMode.FollowSubject;
            stack.AddEffect(BasisCameraEffectModifier.LookAhead);
            stack.AddEffect(BasisCameraEffectModifier.Shake);
            stack.subject.modifier = BasisCameraSubjectModifier.None;

            stack.Sanitize();

            Assert.That(stack.positionModifier, Is.EqualTo(BasisCameraPositionModifier.FreeFly));
            Assert.That(stack.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.Hold));
            Assert.That(stack.dolly.mode, Is.EqualTo(BasisCameraDollyMode.Manual));
            Assert.That(stack.HasEffect(BasisCameraEffectModifier.LookAhead), Is.False,
                "A stack cannot say it films somebody and then film nobody.");
            Assert.That(stack.HasEffect(BasisCameraEffectModifier.Shake), Is.True,
                "Only what needs a subject goes; the rest of the stack is left alone.");
        }

        [Test]
        public void AFixedPointHasNoFacingToMatch()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.Orbit,
                rotationModifier = BasisCameraRotationModifier.MatchSubject,
            };
            stack.subject.modifier = BasisCameraSubjectModifier.FixedPoint;

            stack.Sanitize();

            Assert.That(stack.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.Hold));
            Assert.That(stack.positionModifier, Is.EqualTo(BasisCameraPositionModifier.Orbit),
                "A place can still be orbited; it just cannot be faced.");
        }

        [Test]
        public void AimingDownTheTrackSurvivesAnEmptySubjectSlot()
        {
            // The pair a travelling shot is built from: a track to ride and the track's own heading
            // to aim by. Neither films anybody, so emptying the subject slot must leave both fitted.
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.DollyTrack,
                rotationModifier = BasisCameraRotationModifier.AimAlongTrack,
            };
            stack.subject.modifier = BasisCameraSubjectModifier.None;

            stack.Sanitize();

            Assert.That(stack.positionModifier, Is.EqualTo(BasisCameraPositionModifier.DollyTrack));
            Assert.That(stack.rotationModifier, Is.EqualTo(BasisCameraRotationModifier.AimAlongTrack));
        }
    }
}
