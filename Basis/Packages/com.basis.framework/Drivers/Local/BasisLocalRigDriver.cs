using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Local rig driver that wires up Unity Animation Rigging constraints for a player avatar,
    /// filters tracker noise (One Euro Filter), and manually evaluates the rig graph each frame.
    /// Sets up spine, head, hands, feet, and toes, and toggles layers based on available rigs.
    /// </summary>
    [Serializable]
    public class BasisLocalRigDriver
    {
        /// <summary>
        /// Lower = more smoothing; Higher = more responsive. (0.01f, 10f)
        /// </summary>
        public static float MinCutoff = 5.5f;

        /// <summary>
        /// How much to raise cutoff when motion is fast (reduces lag during quick moves). (0f, 10f)
        /// </summary>
        public static float Beta = 3.25f;

        /// <summary>
        /// Cutoff for derivative smoothing. (0.01f, 10f)
        /// </summary>
        public static float DerivativeCutoff = 3f;

        public RigBuilder Builder;
        public List<RigTransform> AdditionalTransforms = new List<RigTransform>();
        public PlayableGraph PlayableGraph;
        public Rig MainRig;
        public RigLayer RigLayer;
        public BasisFullBodyIK BasisFullIKConstraint;

        private BasisLocalPlayer localPlayer;
        private BasisTransformMapping basisTransformMapping;
        private readonly OneEuroFilterVector3 oneEuroFilter = new OneEuroFilterVector3(MinCutoff, Beta, DerivativeCutoff);

        private float timeAccumulator;

        #region Initialization / Setup

        public void Initialize(BasisLocalPlayer localPlayer, BasisTransformMapping references)
        {
            this.localPlayer = localPlayer;
            basisTransformMapping = references;
            timeAccumulator = 0f;
        }

        public void BuildBuilder()
        {
            if (localPlayer?.BasisAvatar?.Animator == null || Builder == null)
            {
                return;
            }

            PlayableGraph = localPlayer.BasisAvatar.Animator.playableGraph;
            PlayableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Builder.Build(PlayableGraph);
        }

        public void SetBodySettings()
        {
            var rigGO = CreateOrGetRig("Main IK", true, out MainRig, out RigLayer);
            Spine(rigGO);
            BasisLocalBoneControl.HasEvents = true;
        }

        public void CleanupBeforeContinue()
        {
            if (MainRig == null)
            {
                return;
            }

            GameObject.Destroy(MainRig.gameObject);
            MainRig = null;
            RigLayer = default;
        }

        #endregion

        #region T-Pose Handling

        public void OnTPose() => OnTPose(BasisLocalAvatarDriver.CurrentlyTposing);

        public void OnTPose(bool currentlyTposing)
        {
            if (Builder == null)
            {
                BasisDebug.LogWarning($"{nameof(BasisLocalRigDriver)}: Trying to T-pose while Builder is null!");
                return;
            }

            // While in T-pose, disable all rig layers
            if (currentlyTposing)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer != null)
                    {
                        layer.active = false;
                    }
                }

                return;
            }

            // Notify controls when exiting T-pose
            var driver = BasisLocalPlayer.Instance?.LocalBoneDriver;
            if (driver?.Controls == null)
            {
                return;
            }

            foreach (var control in driver.Controls)
            {
                control?.OnHasRigChanged?.Invoke();
            }
        }

        #endregion

        #region IK Simulation

        public void SimulateIKDestinations(float deltaTime)
        {
            if (BasisFullIKConstraint == null || Builder == null)
            {
                return;
            }

            if (!PlayableGraph.IsValid())
            {
                return;
            }

            // Keep time going forward, avoid zero
            timeAccumulator += Mathf.Max(deltaTime, 1e-6f);

            UpdateFilterSettings();

            var hipsControl = BasisLocalBoneDriver.HipsControl;
            var hipsCoords = hipsControl.OutgoingWorldData;
            var hipsPositionFiltered = oneEuroFilter.Filter(hipsCoords.position, timeAccumulator);

            BasisFullBodyData data = BasisFullIKConstraint.data;

            // Hips
            data.PositionHips = hipsPositionFiltered;
            data.RotationEulerHips = hipsCoords.rotation;

            // Global hint direction (knee/neck)
            data.m_HintDirection = hipsCoords.rotation * Vector3.right;

            // Head
            var temp = BasisLocalBoneDriver.HeadControl.OutgoingWorldData;
            data.PositionHead = temp.position;
            data.RotationHead = temp.rotation;

            // Feet
            temp = BasisLocalBoneDriver.LeftFootControl.OutgoingWorldData;
            data.LeftFootPosition = temp.position;
            data.LeftFootRotation = temp.rotation;

            temp = BasisLocalBoneDriver.RightFootControl.OutgoingWorldData;
            data.RightFootPosition = temp.position;
            data.RightFootRotation = temp.rotation;

            // Chest (head hint)
            temp = BasisLocalBoneDriver.ChestControl.OutgoingWorldData;
            data.HintPositionHead = temp.position;
            data.HintRotationHead = temp.rotation;

            // Leg hints
            temp = BasisLocalBoneDriver.LeftLowerLegControl.OutgoingWorldData;
            data.HintPositionLeftLowerLeg = temp.position;
            data.HintRotationLeftLowerLeg = temp.rotation;

            temp = BasisLocalBoneDriver.RightLowerLegControl.OutgoingWorldData;
            data.HintPositionRightFoot = temp.position;
            data.HintRotationRightFoot = temp.rotation;

            // Hands (targets)
            var leftHand = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
            data.PositionLeftHand = leftHand.position;
            data.RotationLeftHand = leftHand.rotation;

            var rightHand = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;
            data.PositionRightHand = rightHand.position;
            data.RotationRightHand = rightHand.rotation;

            // Hand hints (forearms)
            var leftLowerArm = BasisLocalBoneDriver.LeftLowerArmControl.OutgoingWorldData;
            data.HintPositionLeftHand = leftLowerArm.position;
            data.HintRotationLeftHand = leftLowerArm.rotation;

            var rightLowerArm = BasisLocalBoneDriver.RightLowerArmControl.OutgoingWorldData;
            data.HintPositionRightHand = rightLowerArm.position;
            data.HintRotationRightHand = rightLowerArm.rotation;

            // Toes
            var rightToe = BasisLocalBoneDriver.RightToeControl.OutgoingWorldData;
            data.OutGoingRightToePosition = rightToe.position;
            data.OutGoingRightToeRotation = rightToe.rotation;

            var leftToe = BasisLocalBoneDriver.LeftToeControl.OutgoingWorldData;
            data.OutGoingLeftToePosition = leftToe.position;
            data.OutGoingLeftToeRotation = leftToe.rotation;

            temp = BasisLocalBoneDriver.LeftShoulderControl.OutgoingWorldData;
            //   data.LeftFootPosition = temp.position;
            data.m_TargetRotationLeftShoulder = temp.rotation;

            temp = BasisLocalBoneDriver.RightShoulderControl.OutgoingWorldData;
            //  data.RightFootPosition = temp.position;
            data.m_TargetRotationRightShoulder = temp.rotation;

            BasisFullIKConstraint.data = data;

            Builder.SyncLayers();
            PlayableGraph.Evaluate(deltaTime);
        }

        private void UpdateFilterSettings()
        {
            oneEuroFilter.minCutoff = MinCutoff;
            oneEuroFilter.beta = Beta;
            oneEuroFilter.dCutoff = DerivativeCutoff;
        }

        #endregion

        #region Rig Creation / Spine Setup
        private void OnPlayersHeightChangedNextFrame()
        {
            var Data = BasisFullIKConstraint.data;
            SetHandCollisionScale(ref Data, BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale);
            BasisFullIKConstraint.data = Data;
        }
        public static void SetHandCollisionScale(ref BasisFullBodyData BodyData, float Scale)
        {
            //1.6m is the default values for below..
            BodyData.handSkin = 0.03f * Scale;
            BodyData.handRadius = 0.01f * Scale;
            BodyData.chestRadius = 0.07f * Scale;
            BodyData.collisionSkin = 0.05f * Scale;

            var hips = BasisLocalBoneDriver.HipsControl.TposeLocalScaled;
            var spine = BasisLocalBoneDriver.SpineControl.TposeLocalScaled;
            var chest = BasisLocalBoneDriver.ChestControl.TposeLocalScaled;

            var neck = BasisLocalBoneDriver.NeckControl.TposeLocalScaled;
            var head = BasisLocalBoneDriver.HeadControl.TposeLocalScaled;


            float d = 0f;
            d += Vector3.Distance(hips.position, spine.position);
            d += Vector3.Distance(spine.position, chest.position);
            d += Vector3.Distance(chest.position, neck.position);
            d += Vector3.Distance(neck.position, head.position);

            BodyData.minHeadSpineHeight = d;
        }
        public void Spine(GameObject mainRig)
        {
            if (localPlayer == null || mainRig == null)
            {
                return;
            }

            BasisAnimationRiggingHelper.CreateBasisFullBodyRIG(
                localPlayer,
                mainRig,
                basisTransformMapping,
                out BasisFullIKConstraint
            );
            BasisLocalHeight.OnHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;
            OnPlayersHeightChangedNextFrame();

            var data = BasisFullIKConstraint.data;

            // Legs enabled by presence
            BasisLocalBoneDriver.LeftFootControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableLeftLeg = HasRigLayer(BasisLocalBoneDriver.LeftFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableLeftLeg = HasRigLayer(BasisLocalBoneDriver.LeftFootControl);

            BasisLocalBoneDriver.RightFootControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnableRightLeg = HasRigLayer(BasisLocalBoneDriver.RightFootControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnableRightLeg = HasRigLayer(BasisLocalBoneDriver.RightFootControl);

            BasisLocalBoneDriver.LeftLowerLegControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.HintWeightLeftLowerLeg = HasRigLayer(BasisLocalBoneDriver.LeftLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightLeftLowerLeg = HasRigLayer(BasisLocalBoneDriver.LeftLowerLegControl);

            BasisLocalBoneDriver.RightLowerLegControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.HintWeightRightLowerLeg = HasRigLayer(BasisLocalBoneDriver.RightLowerLegControl);
                BasisFullIKConstraint.data = d;
            };
            data.HintWeightRightLowerLeg = HasRigLayer(BasisLocalBoneDriver.RightLowerLegControl);

            // Toes
            BasisLocalBoneDriver.LeftToeControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.LeftToeEnabled = HasRigLayer(BasisLocalBoneDriver.LeftToeControl);

            BasisLocalBoneDriver.RightToeControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);
                BasisFullIKConstraint.data = d;
            };
            data.RightToeEnabled = HasRigLayer(BasisLocalBoneDriver.RightToeControl);

            // Hands
            BasisLocalBoneDriver.LeftHandControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.enabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.enabledLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftHandControl);

            BasisLocalBoneDriver.RightHandControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.enabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);
                BasisFullIKConstraint.data = d;
            };
            data.enabledRightHand = HasRigLayer(BasisLocalBoneDriver.RightHandControl);

            // Lower arms (hand hints)
            BasisLocalBoneDriver.LeftLowerArmControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.hintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.hintWeightLeftHand = HasRigLayer(BasisLocalBoneDriver.LeftLowerArmControl);

            BasisLocalBoneDriver.RightLowerArmControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.hintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);
                BasisFullIKConstraint.data = d;
            };
            data.hintWeightRightHand = HasRigLayer(BasisLocalBoneDriver.RightLowerArmControl);

            // Chest (head hint)
            BasisLocalBoneDriver.ChestControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.hintWeightHead = HasRigLayer(BasisLocalBoneDriver.ChestControl);
                BasisFullIKConstraint.data = d;
            };
            data.hintWeightHead = HasRigLayer(BasisLocalBoneDriver.ChestControl);

            // Chest (head hint)
            BasisLocalBoneDriver.LeftShoulderControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledLeftShoulder = HasRigLayer(BasisLocalBoneDriver.LeftShoulderControl);

            // Chest (head hint)
            BasisLocalBoneDriver.RightShoulderControl.OnHasRigChanged += () =>
            {
                var d = BasisFullIKConstraint.data;
                d.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);
                BasisFullIKConstraint.data = d;
            };
            data.EnabledRightShoulder = HasRigLayer(BasisLocalBoneDriver.RightShoulderControl);

            // Initialize offsets and weights per humanoid bone
            int totalBones = BasisFullBodyData.Count;
            for (int slot = 0; slot < totalBones; slot++)
            {
                var bone = (HumanBodyBones)slot;
                var t = ResolveHumanoidBoneTransform(bone);
                if (t == null)
                {
                    continue;
                }

                data.SetWeight(slot, false);
                data.SetOffsetRotation(slot, t.rotation);
                data.SetTargetRotation(slot, t.rotation);
            }
            data.maxBendDeg = 90;
            data.minFactor = 0.4f;
            data.maxFactor = 1f;
            data.struggleStart = 0.9f;
            data.struggleEnd = 1;
            data.maxChestDelta = 90;

            BasisFullIKConstraint.data = data;
        }

        private static bool HasRigLayer(BasisLocalBoneControl control)
        {
            return control.HasRigLayer == BasisHasRigLayer.HasRigLayer;
        }

        public GameObject CreateOrGetRig(string role, bool enabled, out Rig rig, out RigLayer rigLayer)
        {
            rig = null;
            rigLayer = default;

            if (localPlayer?.BasisAvatar?.Animator == null)
            {
                return null;
            }

            if (Builder != null)
            {
                foreach (var layer in Builder.layers)
                {
                    if (layer?.rig != null && layer.rig.name == $"Rig {role}")
                    {
                        rig = layer.rig;
                        rigLayer = layer;
                        return layer.rig.gameObject;
                    }
                }
            }

            var anim = localPlayer.BasisAvatar.Animator;
            GameObject rigGO = BasisAnimationRiggingHelper.CreateAndSetParent(anim.transform, $"Rig {role}");

            rig = BasisHelpers.GetOrAddComponent<Rig>(rigGO);
            rigLayer = new RigLayer(rig, enabled);

            if (Builder == null)
            {
                Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(anim.gameObject);
            }

            Builder.layers.Add(rigLayer);

            return rigGO;
        }

        #endregion

        #region Overrides API

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideUsage(HumanBodyBones bone, bool enabled)
        {
            var data = BasisFullIKConstraint.data;
            data.SetWeight((int)bone, enabled);
            BasisFullIKConstraint.data = data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOverrideData(HumanBodyBones bone, in Vector3 position, in Quaternion rotation)
        {
            var data = BasisFullIKConstraint.data;
            data.SetTargetPosition((int)bone, position);
            data.SetTargetRotation((int)bone, rotation);
            BasisFullIKConstraint.data = data;
        }

        #endregion

        #region Helpers

        private Transform ResolveHumanoidBoneTransform(HumanBodyBones bone)
        {
            // Prefer references map if available
            if (BasisLocalAvatarDriver.References != null &&
                BasisLocalAvatarDriver.References.GetTransform(bone, out Transform refT))
            {
                return refT;
            }

            // Fallback to Animator
            var animator = localPlayer?.BasisAvatar?.Animator;
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }

        #endregion
    }
}
