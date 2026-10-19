using System.Collections.Generic;
using Basis.IK;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public sealed class BasisFitTestRig : System.IDisposable
    {
        public BasisFitBodySpec Spec;
        public GameObject Wrapper;
        public Transform Root, Hips, Spine, Chest, UpperChest, Neck, Head, Eye, LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand, RightShoulder, RightUpperArm, RightLowerArm, RightHand;
        public Transform LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToe, RightUpperLeg, RightLowerLeg, RightFoot, RightToe;
        public Transform HipsTracker, ChestTracker, LeftFootTracker, RightFootTracker, LeftKneeTracker, RightKneeTracker, LeftElbowTracker, RightElbowTracker;
        public BasisTransformMapping Mapping;
        public BasisPoseSkeleton Skeleton;
        public BasisEerieMovement Job;
        public bool JobCreated;
        public static BasisFitTestRig Build(BasisFitBodySpec spec, bool withSolver)
        {
            var rig = new BasisFitTestRig { Spec = spec };
            rig.Wrapper = new GameObject("Fit_" + spec.Name);
            rig.Root = Bone("Root", rig.Wrapper.transform, Vector3.zero);
            rig.Hips = Bone("Hips", rig.Root, new Vector3(0f, spec.HipsY, 0f));
            rig.Spine = Bone("Spine", rig.Hips, new Vector3(0f, spec.SpineY, 0f));
            rig.Chest = Bone("Chest", rig.Spine, new Vector3(0f, spec.ChestY, 0f));
            rig.UpperChest = spec.HasUpperChest ? Bone("UpperChest", rig.Chest, new Vector3(0f, spec.UpperChestY, 0f)) : null;
            Transform neckParent = rig.UpperChest != null ? rig.UpperChest : rig.Chest;
            rig.Neck = spec.HasNeck ? Bone("Neck", neckParent, new Vector3(0f, spec.NeckY, 0f)) : null;
            rig.Head = Bone("Head", rig.Neck != null ? rig.Neck : neckParent, new Vector3(0f, spec.HeadY, 0f));
            rig.Eye = Bone("Eye", rig.Head, new Vector3(0f, spec.EyeY, spec.EyeForward));
            Transform shoulderParent = spec.ShouldersOnChest || rig.UpperChest == null ? rig.Chest : rig.UpperChest;
            float drop = spec.ArmDropDeg * Mathf.Deg2Rad;
            Vector3 leftDir = new Vector3(-Mathf.Cos(drop), -Mathf.Sin(drop), 0f), rightDir = new Vector3(Mathf.Cos(drop), -Mathf.Sin(drop), 0f);
            rig.LeftShoulder = Bone("LeftShoulder", shoulderParent, new Vector3(-spec.ShoulderRootX, spec.ShoulderRootY, 0f));
            rig.LeftUpperArm = Bone("LeftUpperArm", rig.LeftShoulder, new Vector3(-spec.ShoulderJointX, spec.ShoulderJointY, 0f));
            rig.LeftLowerArm = Bone("LeftLowerArm", rig.LeftUpperArm, rig.LeftUpperArm.position + leftDir * spec.UpperArmLength);
            rig.LeftHand = Bone("LeftHand", rig.LeftLowerArm, rig.LeftLowerArm.position + leftDir * spec.ForearmLength);
            rig.RightShoulder = Bone("RightShoulder", shoulderParent, new Vector3(spec.ShoulderRootX, spec.ShoulderRootY, 0f));
            rig.RightUpperArm = Bone("RightUpperArm", rig.RightShoulder, new Vector3(spec.ShoulderJointX, spec.ShoulderJointY, 0f));
            rig.RightLowerArm = Bone("RightLowerArm", rig.RightUpperArm, rig.RightUpperArm.position + rightDir * spec.UpperArmLength);
            rig.RightHand = Bone("RightHand", rig.RightLowerArm, rig.RightLowerArm.position + rightDir * spec.ForearmLength);
            rig.LeftUpperLeg = Bone("LeftUpperLeg", rig.Hips, new Vector3(-spec.HipJointX, spec.HipJointY, 0f));
            rig.LeftLowerLeg = Bone("LeftLowerLeg", rig.LeftUpperLeg, new Vector3(-spec.HipJointX, spec.KneeY, 0f));
            rig.LeftFoot = Bone("LeftFoot", rig.LeftLowerLeg, new Vector3(-spec.HipJointX, spec.AnkleY, 0f));
            rig.LeftToe = Bone("LeftToe", rig.LeftFoot, new Vector3(-spec.HipJointX, spec.ToeY, spec.ToeForward));
            rig.RightUpperLeg = Bone("RightUpperLeg", rig.Hips, new Vector3(spec.HipJointX, spec.HipJointY, 0f));
            rig.RightLowerLeg = Bone("RightLowerLeg", rig.RightUpperLeg, new Vector3(spec.HipJointX, spec.KneeY, 0f));
            rig.RightFoot = Bone("RightFoot", rig.RightLowerLeg, new Vector3(spec.HipJointX, spec.AnkleY, 0f));
            rig.RightToe = Bone("RightToe", rig.RightFoot, new Vector3(spec.HipJointX, spec.ToeY, spec.ToeForward));
            rig.BuildMapping();
            if (withSolver)
            {
                rig.BuildSolver();
            }
            else
            {
                rig.AttachTrackers();
            }
            return rig;
        }
        static Transform Bone(string name, Transform parent, Vector3 worldPosition)
        {
            Transform t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.position = worldPosition;
            return t;
        }
        void BuildMapping()
        {
            var m = new BasisTransformMapping();
            m.AnimatorRoot = Root; m.HasAnimatorRoot = true;
            m.Hips = Hips; m.HasHips = true;
            m.spine = Spine; m.Hasspine = true;
            m.chest = Chest; m.Haschest = true;
            m.Upperchest = UpperChest; m.HasUpperchest = UpperChest != null;
            m.neck = Neck; m.Hasneck = Neck != null;
            m.head = Head; m.Hashead = true;
            m.leftShoulder = LeftShoulder; m.HasleftShoulder = true;
            m.leftUpperArm = LeftUpperArm; m.HasleftUpperArm = true;
            m.leftLowerArm = LeftLowerArm; m.HasleftLowerArm = true;
            m.leftHand = LeftHand; m.HasleftHand = true;
            m.RightShoulder = RightShoulder; m.HasRightShoulder = true;
            m.RightUpperArm = RightUpperArm; m.HasRightUpperArm = true;
            m.RightLowerArm = RightLowerArm; m.HasRightLowerArm = true;
            m.rightHand = RightHand; m.HasrightHand = true;
            m.LeftUpperLeg = LeftUpperLeg; m.HasLeftUpperLeg = true;
            m.LeftLowerLeg = LeftLowerLeg; m.HasLeftLowerLeg = true;
            m.leftFoot = LeftFoot; m.HasleftFoot = true;
            m.leftToe = LeftToe; m.HasleftToes = true;
            m.RightUpperLeg = RightUpperLeg; m.HasRightUpperLeg = true;
            m.RightLowerLeg = RightLowerLeg; m.HasRightLowerLeg = true;
            m.rightFoot = RightFoot; m.HasrightFoot = true;
            m.rightToe = RightToe; m.HasrightToes = true;
            Mapping = m;
        }
        void BuildSolver()
        {
            Skeleton = new BasisPoseSkeleton();
            var bones = new List<Transform> { Hips, Spine, Chest, UpperChest, Neck, Head, LeftShoulder, RightShoulder, LeftUpperArm, LeftLowerArm, LeftHand, RightUpperArm, RightLowerArm, RightHand, LeftUpperLeg, LeftLowerLeg, LeftFoot, RightUpperLeg, RightLowerLeg, RightFoot, LeftToe, RightToe };
            Skeleton.Build(Root, bones);
            Skeleton.SetTranslationFree(Hips);
            BasisEerieMovementSetup.SetDefaultValues(ref Job);
            BasisEerieMovementSetup.Create(ref Job, Skeleton, Mapping);
            JobCreated = true;
            Job.minHeadSpineHeight = Job.RalivRestChainLength();
        }
        void AttachTrackers()
        {
            float h = Spec.EyeY / 0.936f;
            Quaternion mount = Quaternion.Euler(20f, 35f, -10f);
            HipsTracker = Tracker("HipsTracker", Hips, new Vector3(0f, 0.045f * h, -0.05f * h), mount);
            ChestTracker = Tracker("ChestTracker", UpperChest != null ? UpperChest : Chest, new Vector3(0f, -0.01f * h, 0.07f * h), mount);
            LeftFootTracker = Tracker("LeftFootTracker", LeftFoot, new Vector3(0f, 0.02f * h, 0.04f * h), mount);
            RightFootTracker = Tracker("RightFootTracker", RightFoot, new Vector3(0f, 0.02f * h, 0.04f * h), mount);
            LeftKneeTracker = Tracker("LeftKneeTracker", LeftLowerLeg, new Vector3(0f, -0.03f * h, 0.05f * h), mount);
            RightKneeTracker = Tracker("RightKneeTracker", RightLowerLeg, new Vector3(0f, -0.03f * h, 0.05f * h), mount);
            LeftElbowTracker = Tracker("LeftElbowTracker", LeftLowerArm, new Vector3(-0.03f * h, 0.015f * h, -0.02f * h), mount);
            RightElbowTracker = Tracker("RightElbowTracker", RightLowerArm, new Vector3(0.03f * h, 0.015f * h, -0.02f * h), mount);
        }
        static Transform Tracker(string name, Transform parent, Vector3 local, Quaternion rotation)
        {
            Transform t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.SetLocalPositionAndRotation(local, rotation);
            return t;
        }
        public Vector3 RestLocal(Transform bone)
        {
            if (Skeleton != null && Skeleton.IsCreated && Skeleton.TryGetRestPositionRelativeTo(bone, Root, out Vector3 local))
            {
                return local;
            }
            return Root.InverseTransformPoint(bone.position);
        }
        public void ResetToRestPose()
        {
            var stream = Skeleton.Stream;
            for (int i = 0; i < stream.Count; i++)
            {
                stream.LocalPosition[i] = stream.RestLocalPosition[i];
                stream.LocalRotation[i] = stream.RestLocalRotation[i];
            }
            stream.InvalidateWorldCache();
            Skeleton.ScatterNow();
            Skeleton.RefreshRootFromTransform();
        }
        public void Dispose()
        {
            if (JobCreated)
            {
                Job.Destroy();
                JobCreated = false;
            }
            Skeleton?.Dispose();
            if (Wrapper != null)
            {
                Object.DestroyImmediate(Wrapper);
            }
        }
    }
}
