using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
namespace JigglePhysics
{

    public class JiggleRigBuilder : MonoBehaviour, IJiggleAdvancable, IJiggleBlendable
    {
        public const float VERLET_TIME_STEP = 0.02f;
        public const float MAX_CATCHUP_TIME = VERLET_TIME_STEP * 4f;
        internal const float SETTLE_TIME = 0.2f;
        internal const float SQUARED_VERLET_TIME_STEP = VERLET_TIME_STEP * VERLET_TIME_STEP;

        [Serializable]
        public class JiggleRig
        {
            [SerializeField]
            [Tooltip("The root bone from which an individual JiggleRig will be constructed. The JiggleRig encompasses all children of the specified root.")]
            [FormerlySerializedAs("target")]
            private Transform rootTransform;
            [Tooltip("The settings that the rig should update with, create them using the Create->JigglePhysics->Settings menu option.")]
            public JiggleSettingsBase jiggleSettings;
            [SerializeField]
            [Tooltip("The list of transforms to ignore during the jiggle. Each bone listed will also ignore all the children of the specified bone.")]
            private List<Transform> ignoredTransforms;
            [SerializeField] private Collider[] colliders;
            [Tooltip("Turn this on if you animate the jiggle bones via Animator (or through script). ")]
            public bool animated = false;
            private int boneCount;
            public Collider[] GetColliders() => colliders;

            private bool initialized => simulatedPoints != null;

            public Transform GetRootTransform() => rootTransform;

            /// <summary>
            /// Constructs a realtime JiggleRig
            /// </summary>
            /// <param name="rootTransform">The root transform of the rig</param>
            /// <param name="jiggleSettings">Which jiggle settings to use. You can instantiate one and use SetData() to make one on the fly.</param>
            /// <param name="ignoredTransforms">Transforms to be ignored when constructing the jiggle tree.</param>
            /// <param name="colliders">Colliders that the jiggle rig should collide with.</param>
            public JiggleRig(Transform rootTransform, JiggleSettingsBase jiggleSettings,
                ICollection<Transform> ignoredTransforms, ICollection<Collider> colliders)
            {
                // TODO: This class should handle changing settings at runtime (including instantiating only if needed) (follow sharedMaterial functionality)
                this.rootTransform = rootTransform;
                this.jiggleSettings = jiggleSettings;
                this.ignoredTransforms = new List<Transform>(ignoredTransforms);
                Initialize();
            }

            [HideInInspector]
            protected JiggleBone[] simulatedPoints;

            /// <summary>
            /// Matches the particle signal to the current pose, then undoes the pose such that it doesn't permanently deform the jiggle system. This is useful for confining the jiggles, like they got "grabbed".
            /// I would essentially use IK to set the bones to be grabbed. Then call this function so the virtual jiggle particles also move to the same location.
            /// It would need to be called every frame that the chain is grabbed.
            /// </summary>
            public void SetTargetAndResetToLastValidPose()
            {
                for (int i = boneCount - 1; i >= 0; i--)
                {
                    simulatedPoints[i].SetTargetAndResetToLastValidPose(simulatedPoints);
                }
            }

            /// <summary>
            /// Samples the current pose for use later in stepping the simulation. This should be called fairly regularly. It creates the desired "target pose" that the simulation tries to match.
            /// </summary>
            public void ApplyValidPoseThenSampleTargetPose(double timeAsDouble)
            {
                for (int Index = 0; Index < boneCount; Index++)
                {
                    simulatedPoints[Index].ApplyValidPoseThenSampleTargetPose(simulatedPoints, timeAsDouble, animated);
                }
            }

            /// <summary>
            /// Consumes time into "ticks", from which a verlet simulation is done to simulate the jiggles. Ensure that you've called SampleTargetPose and ApplyTargetPose before this function.
            /// Use Pose to actually display the simulation.
            /// </summary>
            /// <param name="rigPosition">The general position of the rig, used for LOD swapping.</param>
            /// <param name="jiggleRigLOD">The JiggleRigLOD object used for disabling the rig if its too far. Can be null.</param>
            /// <param name="wind">How much wind to apply to every particle.</param>
            /// <param name="time">How much time to simulate forward by.</param>
            public void StepSimulation(JiggleSettingsData data, double time, Vector3 acceleration)
            {
                if (initialized)
                {
                    for (int i = 0; i < boneCount; i++)
                    {
                        var simulatedPoint = simulatedPoints[i];

                        #region VerletIntegrate

                        simulatedPoint.currentFixedAnimatedBonePosition =
                            simulatedPoint.targetAnimatedBoneSignal.SamplePosition(time);
                        if (!simulatedPoint.hasParent)
                        {
                            simulatedPoint.workingPosition = simulatedPoint.currentFixedAnimatedBonePosition;
                            simulatedPoint.parentPose = 2f * simulatedPoint.currentFixedAnimatedBonePosition -
                                                        simulatedPoints[simulatedPoint.childID]
                                                            .currentFixedAnimatedBonePosition;
                            simulatedPoint.parentPosition = simulatedPoint.parentPose;
                            continue;
                        }

                        var parentSimulatedPoint = simulatedPoints[simulatedPoint.parentID];
                        simulatedPoint.parentPose = parentSimulatedPoint.currentFixedAnimatedBonePosition;
                        simulatedPoint.parentPosition = parentSimulatedPoint.workingPosition;

                        Vector3 newPosition = simulatedPoint.particleSignal.GetCurrent();
                        Vector3 delta = newPosition - simulatedPoint.particleSignal.GetPrevious();
                        Vector3 localSpaceVelocity = delta - (parentSimulatedPoint.particleSignal.GetCurrent() -
                                                              parentSimulatedPoint.particleSignal.GetPrevious());
                        Vector3 velocity = delta - localSpaceVelocity;
                        simulatedPoint.workingPosition = newPosition + velocity * data.airDragOneMinus +
                                                         localSpaceVelocity * data.frictionOneMinus + acceleration;

                        #endregion
                    }

                    for (int i = 0; i < boneCount; i++)
                    {
                        var simulatedPoint = simulatedPoints[i];
                        if (!simulatedPoint.hasParent)
                        {
                            continue;
                        }
                        var parentSimulatedPoint = simulatedPoints[simulatedPoint.parentID];
                        var lengthToParent = simulatedPoint.cachedLengthToParent;
                        #region ConstrainAngle
                        if (simulatedPoint.shouldConfineAngle)
                        {
                            Vector3 parentAimTargetPose = parentSimulatedPoint.currentFixedAnimatedBonePosition - parentSimulatedPoint.parentPose;
                            Vector3 parentAim = parentSimulatedPoint.workingPosition - parentSimulatedPoint.parentPosition;
                            Quaternion targetPoseToPose = Quaternion.FromToRotation(parentAimTargetPose, parentAim);
                            Vector3 currentPose = simulatedPoint.currentFixedAnimatedBonePosition - parentSimulatedPoint.parentPose;
                            Vector3 constraintTarget = targetPoseToPose * currentPose;
                            float error = Vector3.Distance(simulatedPoint.workingPosition, parentSimulatedPoint.parentPosition + constraintTarget);
                            error /= lengthToParent;
                            error = Mathf.Min(error, 1f);
                            error = Mathf.Pow(error, data.doubleElasticitySoften);
                            simulatedPoint.workingPosition = Vector3.LerpUnclamped(simulatedPoint.workingPosition, parentSimulatedPoint.parentPosition + constraintTarget, data.squaredAngleElasticity * error);
                        }
                        #endregion


                        Vector3 diff = simulatedPoint.workingPosition - parentSimulatedPoint.workingPosition;
                        Vector3 dir = diff.normalized;
                        simulatedPoint.workingPosition = Vector3.LerpUnclamped(simulatedPoint.workingPosition, parentSimulatedPoint.workingPosition + dir * lengthToParent, data.squaredLengthElasticity);

                    }
                }
            }

            public void WriteSimulatedStep(double timeAsDouble)
            {
                for (int i = 0; i < boneCount; i++)
                {
                    var simulatedPoint = simulatedPoints[i];
                    simulatedPoint.particleSignal.SetPosition(simulatedPoint.workingPosition, timeAsDouble);
                }
            }

            /// <summary>
            /// Creates the virtual particle tree that is used to simulate the jiggles!
            /// </summary>
            public void Initialize()
            {
                var jiggleBoneList = new List<JiggleBone>();
                //simulatedPoints = new List<JiggleBone>();
                if (rootTransform == null)
                {
                    return;
                }

                CreateSimulatedPoints(jiggleBoneList, ignoredTransforms, rootTransform, null, null);
                for (int i = 0; i < jiggleBoneList.Count; i++)
                {
                    jiggleBoneList[i].CalculateNormalizedIndex(jiggleBoneList);
                }
                simulatedPoints = jiggleBoneList.ToArray();
                boneCount = simulatedPoints.Length;
            }

            /// <summary>
            /// Calculates where the virtual particles would be at this exact moment in time (the latest simulation state is in the past and must be extrapolated).
            /// You normally won't call this.
            /// </summary>
            internal void DeriveFinalSolve(double timeAsDoubleOneStepBack)
            {
                Vector3 offset = simulatedPoints[0].cachedPositionForPosing - simulatedPoints[0].DeriveFinalSolvePosition(Vector3.zero, timeAsDoubleOneStepBack);
                for (int i = 0; i < boneCount; i++)
                {
                    simulatedPoints[i].DeriveFinalSolvePosition(offset, timeAsDoubleOneStepBack);
                }
            }

            /// <summary>
            /// Attempts to pose the rig to the virtual particles. The current pose MUST be the last valid pose (set by ApplyValidPoseThenSampleTargetPose usually)
            /// </summary>
            /// <param name="debugDraw">If we should draw the target pose (blue) compared to the virtual particle pose (red).</param>
            public void Pose(bool debugDraw, double timeAsDoubleOneStepBack, float blend)
            {
                DeriveFinalSolve(timeAsDoubleOneStepBack);

                for (int i = 0; i < boneCount; i++)
                {
                    simulatedPoints[i].PoseBonePreCache(simulatedPoints, blend);
                }

                for (int i = 0; i < boneCount; i++)
                {
                    var simulatedPoint = simulatedPoints[i];
                    simulatedPoint.PoseBone(simulatedPoints, animated);
                    if (debugDraw)
                    {
                        simulatedPoint.DebugDraw(simulatedPoints, Color.red, Color.blue, true);
                    }
                }
            }

            /// <summary>
            /// Saves the current state of the particles and animations in preparation for a teleport. Move the rig, then do FinishTeleport().
            /// </summary>
            public void PrepareTeleport()
            {
                for (int i = 0; i < boneCount; i++)
                {
                    simulatedPoints[i].PrepareTeleport(simulatedPoints);
                }
            }

            /// <summary>
            /// Offsets the jiggle signals from the position set with PrepareTeleport to the current position. This prevents jiggles from freaking out from a large movement.
            /// </summary>
            public void FinishTeleport()
            {
                for (int i = 0; i < boneCount; i++)
                {
                    simulatedPoints[i].FinishTeleport(simulatedPoints);
                }
            }

            public void OnDrawGizmos()
            {
                if (!initialized)
                {
                    Initialize();
                }

                simulatedPoints[0].OnDrawGizmos(simulatedPoints, jiggleSettings, true);
                for (int i = 1; i < boneCount; i++)
                {
                    simulatedPoints[i].OnDrawGizmos(simulatedPoints, jiggleSettings);
                }
            }

            protected virtual void CreateSimulatedPoints(List<JiggleBone> outputPoints, ICollection<Transform> ignoredTransforms, Transform currentTransform, JiggleBone? parentJiggleBone, int? parentJiggleBoneID)
            {
                JiggleBone newJiggleBone = new JiggleBone(outputPoints, currentTransform, parentJiggleBone, parentJiggleBoneID);
                outputPoints.Add(newJiggleBone);
                var currentID = outputPoints.Count - 1;
                if (parentJiggleBoneID != null)
                {
                    outputPoints[parentJiggleBoneID.Value].SetChildID(outputPoints.Count - 1);
                }

                bool hasChild = false;
                for (int i = 0; i < currentTransform.childCount; i++)
                {
                    if (ignoredTransforms.Contains(currentTransform.GetChild(i)))
                    {
                        continue;
                    }

                    hasChild = true;
                    CreateSimulatedPoints(outputPoints, ignoredTransforms, currentTransform.GetChild(i), newJiggleBone, currentID);
                }

                // Create an extra purely virtual point if we have no children.
                if (hasChild) return;
                if (!newJiggleBone.hasParent)
                {
                    if (newJiggleBone.transform.parent == null)
                    {
                        throw new UnityException("Can't have a singular jiggle bone with no parents. That doesn't even make sense!");
                    }
                    else
                    {
                        outputPoints.Add(new JiggleBone(outputPoints, null, newJiggleBone, currentID));
                        outputPoints[currentID].SetChildID(outputPoints.Count - 1);
                        return;
                    }
                }
                outputPoints.Add(new JiggleBone(outputPoints, null, newJiggleBone, currentID));
                outputPoints[currentID].SetChildID(outputPoints.Count - 1);
            }
        }
        [Tooltip("Enables interpolation for the simulation, this should be set to LateUpdate unless you *really* need the simulation to only update on FixedUpdate.")]
        [SerializeField]
        private JiggleUpdateMode jiggleUpdateMode = JiggleUpdateMode.LateUpdate;

        private JiggleRigLOD jiggleRigLOD;

        [field: SerializeField, Range(0f, 1f)] public float blend { get; set; } = 1f;

        public List<JiggleRig> jiggleRigs;

        [Tooltip("An air force that is applied to the entire rig, this is useful to plug in some wind volumes from external sources.")]
        public Vector3 wind;

        [Tooltip("Draws some simple lines to show what the simulation is doing. Generally this should be disabled.")]
        [SerializeField] private bool debugDraw;

        public void SetJiggleRigLOD(JiggleRigLOD lod)
        {
            if (jiggleRigLOD != null)
            {
                jiggleRigLOD.RemoveTrackedJiggleRig(this);
            }
            jiggleRigLOD = lod;
            if (jiggleRigLOD != null)
            {
                jiggleRigLOD.AddTrackedJiggleRig(this);
            }
        }

        public JiggleRigLOD GetJiggleRigLOD() => jiggleRigLOD;

        private void Start()
        {
            if (TryGetComponent(out JiggleRigLOD lod))
            {
                SetJiggleRigLOD(lod);
            }
        }

        public void SetJiggleUpdateMode(JiggleUpdateMode mode)
        {
            switch (jiggleUpdateMode)
            {
                case JiggleUpdateMode.LateUpdate: JiggleRigLateUpdateHandler.RemoveJiggleRigAdvancable(this); break;
                default: throw new ArgumentOutOfRangeException();
            }
            jiggleUpdateMode = mode;
            switch (jiggleUpdateMode)
            {
                case JiggleUpdateMode.LateUpdate: JiggleRigLateUpdateHandler.AddJiggleRigAdvancable(this); break;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        private double accumulation;
        private float settleTimer;
        private void Awake()
        {
            Initialize();
            accumulation = UnityEngine.Random.Range(0f, VERLET_TIME_STEP);
        }
        void OnEnable()
        {
            switch (jiggleUpdateMode)
            {
                case JiggleUpdateMode.LateUpdate: JiggleRigLateUpdateHandler.AddJiggleRigAdvancable(this); break;
                default: throw new ArgumentOutOfRangeException();
            }

            if (settleTimer > SETTLE_TIME)
            {
                FinishTeleport();
            }
        }
        void OnDisable()
        {
            switch (jiggleUpdateMode)
            {
                case JiggleUpdateMode.LateUpdate: JiggleRigLateUpdateHandler.RemoveJiggleRigAdvancable(this); break;
                default: throw new ArgumentOutOfRangeException();
            }
            PrepareTeleport();
        }

        public void Initialize()
        {
            accumulation = 0f;
            jiggleRigs ??= new List<JiggleRig>();
            foreach (JiggleRig rig in jiggleRigs)
            {
                rig.Initialize();
            }
            settleTimer = 0f;
        }

        public JiggleUpdateMode GetJiggleUpdateMode()
        {
            return jiggleUpdateMode;
        }

        public virtual void Advance(float deltaTime, Vector3 gravity, double timeAsDouble, double timeAsDoubleOneStepBack)
        {
            #region Settling on spawn, to prevent instant posing jiggles.

            if (settleTimer < SETTLE_TIME)
            {
                settleTimer += deltaTime;
                if (settleTimer >= SETTLE_TIME)
                {
                    FinishTeleport();
                }
                return;
            }

            #endregion

            int JiggleCount = jiggleRigs.Count;
            for (int Index = 0; Index < JiggleCount; Index++)
            {
                jiggleRigs[Index].ApplyValidPoseThenSampleTargetPose(timeAsDouble);
            }
            accumulation = Math.Min(accumulation + deltaTime, MAX_CATCHUP_TIME);
            while (accumulation > VERLET_TIME_STEP)
            {
                accumulation -= VERLET_TIME_STEP;
                double time = timeAsDouble - accumulation;
                for (int Index = 0; Index < JiggleCount; Index++)
                {
                    JiggleRig rig = jiggleRigs[Index];
                    JiggleSettingsData data = rig.jiggleSettings.GetData();
                    data.blend *= blend;
                    rig.StepSimulation(data, time, gravity * (data.gravityMultiplier * SQUARED_VERLET_TIME_STEP) + wind * (VERLET_TIME_STEP * data.airDrag));
                }
                for (int Index = 0; Index < JiggleCount; Index++)
                {
                    jiggleRigs[Index].WriteSimulatedStep(time);
                }
            }

            for (int Index = 0; Index < JiggleCount; Index++)
            {
                JiggleRig rig = jiggleRigs[Index];
                var data = rig.jiggleSettings.GetData();
                rig.Pose(debugDraw, timeAsDoubleOneStepBack, data.blend * blend);
            }
        }

        public JiggleRig GetJiggleRig(Transform rootTransform)
        {
            foreach (var rig in jiggleRigs)
            {
                if (rig.GetRootTransform() == rootTransform)
                {
                    return rig;
                }
            }
            return null;
        }

        public void PrepareTeleport()
        {
            foreach (JiggleRig rig in jiggleRigs)
            {
                rig.PrepareTeleport();
            }
        }

        public void FinishTeleport()
        {
            foreach (JiggleRig rig in jiggleRigs)
            {
                rig.FinishTeleport();
            }
        }

        private void OnDrawGizmos()
        {
            if (jiggleRigs == null || !enabled)
            {
                return;
            }
            foreach (var rig in jiggleRigs)
            {
                rig.OnDrawGizmos();
            }
        }

        private void OnDestroy()
        {
            if (jiggleRigLOD != null)
            {
                jiggleRigLOD.RemoveTrackedJiggleRig(this);
            }
        }
    }
}
