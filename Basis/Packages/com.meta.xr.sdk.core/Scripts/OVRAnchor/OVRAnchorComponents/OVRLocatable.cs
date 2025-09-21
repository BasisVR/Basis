/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Represents the Pose of the anchor. Enabling it will localize the anchor.
/// </summary>
/// <remarks>
/// This component can be accessed from an <see cref="OVRAnchor"/> that supports it by calling
/// <see cref="OVRAnchor.GetComponent{T}"/> from the anchor.
///
/// This component needs to be enabled before requesting its Pose. See <see cref="IsEnabled"/> and
/// <see cref="SetEnabledAsync"/>
///
/// Read more about anchors generally at
/// [Spatial Anchors Overview](https://developer.oculus.com/documentation/unity/unity-spatial-anchors-overview/)
/// </remarks>
/// <seealso cref="TrackingSpacePose"/>
/// <seealso cref="TryGetSceneAnchorPose"/>
/// <seealso cref="TryGetSpatialAnchorPose"/>
public readonly partial struct OVRLocatable : IOVRAnchorComponent<OVRLocatable>, IEquatable<OVRLocatable>
{
    /// <summary>
    /// Tracking space position and rotation of the anchor.
    /// </summary>
    /// <remarks>
    /// Obtain a <see cref="TrackingSpacePose"/> from <see cref="OVRLocatable.TryGetSceneAnchorPose"/> or
    /// <see cref="OVRLocatable.TryGetSpatialAnchorPose"/> depending on the type of anchor you wish to interpret it as.
    ///
    /// Position and rotation are both nullable [Vector3](https://docs.unity3d.com/ScriptReference/Vector3.html) and
    /// [Quaternion](https://docs.unity3d.com/ScriptReference/Quaternion.html) and might be null independently if one of
    /// them or both are invalid.
    /// </remarks>
    /// <seealso cref="Position"/>
    /// <seealso cref="IsPositionTracked"/>
    /// <seealso cref="ComputeWorldPosition"/>
    /// <seealso cref="Rotation"/>
    /// <seealso cref="IsRotationTracked"/>
    /// <seealso cref="ComputeWorldRotation"/>
    public readonly struct TrackingSpacePose
    {
        /// <summary>
        /// Position in tracking space of the anchor.
        /// </summary>
        /// <remarks>
        /// Null if and when the rotation is invalid. This constitutes the positional aspect of the anchor's pose. See
        /// <see cref="Rotation"/> for the rotational part.
        /// </remarks>
        /// <seealso cref="Rotation"/>
        /// <seealso cref="ComputeWorldPosition"/>
        /// <seealso cref="ComputeWorldRotation"/>
        public Vector3? Position { get; }

        /// <summary>
        /// Rotation in tracking space of the anchor.
        /// </summary>
        /// <remarks>
        /// Null if and when the rotation is invalid. This constitutes the rotational aspect of the anchor's pose. See
        /// <see cref="Position"/> for the positional part.
        /// </remarks>
        /// <seealso cref="Position"/>
        /// <seealso cref="ComputeWorldPosition"/>
        /// <seealso cref="ComputeWorldRotation"/>
        public Quaternion? Rotation { get; }

        /// <summary>
        /// Indicates whether the position is currently tracked.
        /// </summary>
        public bool IsPositionTracked => Flags.IsPositionTracked();

        /// <summary>
        /// Indicates whether the rotation is currently tracked.
        /// </summary>
        public bool IsRotationTracked => Flags.IsOrientationTracked();

        internal readonly OVRPlugin.SpaceLocationFlags Flags;

        internal TrackingSpacePose(Vector3 position, Quaternion rotation, OVRPlugin.SpaceLocationFlags flags)
        {
            Flags = flags;
            Position = Flags.IsPositionValid() ? position : default(Vector3?);
            Rotation = Flags.IsOrientationValid() ? rotation : default(Quaternion?);
        }

        private const string localToWorldPoseDeprecationMessage = "Using this method after 'await locatable.SetEnabledAsync(true);' is error-prone. OVRTask finishes the execution before OVRCameraRig.Update(), " +
                                                                  "so camera will still use a pose from the previous frame. This results in descrepancy when localizing anchors against the stale camera pose.\n" +
                                                                  "Use an overload with the 'trackingSpaceToWorldSpaceTransform' parameter instead.";

        /// <summary>
        /// \deprecated Computes the world space position of the anchor
        /// </summary>
        /// <param name="camera">A <see cref="Camera"/> component that will be use to compute the transform to world space</param>
        /// <returns>
        /// Returns the nullable [Vector3](https://docs.unity3d.com/ScriptReference/Vector3.html) position in world
        /// space which may be null if and when <see cref="Position"/> is invalid or head pose is invalid.
        /// </returns>
        /// <seealso cref="Position"/>
        /// <seealso cref="Rotation"/>
        /// <seealso cref="ComputeWorldRotation"/>
        /// <exception cref="ArgumentNullException">If <paramref name="camera"/> is null</exception>
        [Obsolete(localToWorldPoseDeprecationMessage)]
        public Vector3? ComputeWorldPosition(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!Position.HasValue) return null;

            var headPose = OVRPose.identity;
            if (!OVRNodeStateProperties.GetNodeStatePropertyVector3(UnityEngine.XR.XRNode.Head,
                    NodeStatePropertyType.Position, OVRPlugin.Node.Head, OVRPlugin.Step.Render, out headPose.position))
                return null;

            if (!OVRNodeStateProperties.GetNodeStatePropertyQuaternion(UnityEngine.XR.XRNode.Head,
                    NodeStatePropertyType.Orientation, OVRPlugin.Node.Head, OVRPlugin.Step.Render,
                    out headPose.orientation))
                return null;

            headPose = headPose.Inverse();

            var headTrackingPosition = headPose.position + headPose.orientation * Position.Value;
            return camera.transform.localToWorldMatrix.MultiplyPoint(headTrackingPosition);
        }

        /// <summary>
        /// \deprecated Computes the world space rotation of the anchor
        /// </summary>
        /// <param name="camera">A <see cref="Camera"/> component that will be use to compute the transform to world space</param>
        /// <returns>
        /// The nullable [Quaternion](https://docs.unity3d.com/ScriptReference/Quaternion.html) rotation in world space
        /// which may be null if and when <see cref="Rotation"/> is invalid or if head rotation is invalid.
        /// </returns>
        /// <seealso cref="Position"/>
        /// <seealso cref="Rotation"/>
        /// <seealso cref="ComputeWorldPosition"/>
        /// <exception cref="ArgumentNullException">If <paramref name="camera"/> is null</exception>
        [Obsolete(localToWorldPoseDeprecationMessage)]
        public Quaternion? ComputeWorldRotation(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!Rotation.HasValue) return null;

            if (!OVRNodeStateProperties.GetNodeStatePropertyQuaternion(UnityEngine.XR.XRNode.Head,
                    NodeStatePropertyType.Orientation, OVRPlugin.Node.Head, OVRPlugin.Step.Render,
                    out var headPoseRotation))
                return null;

            headPoseRotation = Quaternion.Inverse(headPoseRotation);

            var headTrackingOrientation = headPoseRotation * Rotation.Value;
            return camera.transform.rotation * headTrackingOrientation;
        }

        /// <summary>
        /// Computes the world-space position of the anchor.
        /// </summary>
        /// <param name="trackingSpaceToWorldSpaceTransform">Uses this transform to convert position from tracking-space to world-space.</param>
        /// <returns>The world-space position of the anchor, or `null` if <see cref="Position"/> does not have a value.</returns>
        public Vector3? ComputeWorldPosition(Transform trackingSpaceToWorldSpaceTransform)
        {
            if (trackingSpaceToWorldSpaceTransform == null)
            {
                throw new ArgumentNullException(nameof(trackingSpaceToWorldSpaceTransform));
            }
            if (!Position.HasValue)
            {
                return null;
            }
            return trackingSpaceToWorldSpaceTransform.TransformPoint(Position.Value);
        }

        /// <summary>
        /// Computes the world-space rotation of the anchor.
        /// </summary>
        /// <param name="trackingSpaceToWorldSpaceTransform">Uses this transform to convert rotation from tracking-space to world-space.</param>
        /// <returns>The world-space rotation of the anchor, or `null` if <see cref="Rotation"/> does not have a value.</returns>
        public Quaternion? ComputeWorldRotation(Transform trackingSpaceToWorldSpaceTransform)
        {
            if (trackingSpaceToWorldSpaceTransform == null)
            {
                throw new ArgumentNullException(nameof(trackingSpaceToWorldSpaceTransform));
            }
            if (!Rotation.HasValue)
            {
                return null;
            }
            return trackingSpaceToWorldSpaceTransform.rotation * Rotation.Value;
        }
    }

    /// <summary>
    /// Tries to get the <see cref="TrackingSpacePose"/> representing the position and rotation of this anchor, treated as a scene anchor, in tracking space.
    /// </summary>
    /// <param name="pose">The out <see cref="TrackingSpacePose"/> which will get filled in.</param>
    /// <returns>
    /// True if the request was successful, False otherwise.
    /// </returns>
    /// <remarks>
    /// <para>Although the request may succeed and provide a valid <see cref="TrackingSpacePose"/>, actual Position and Rotation provided
    /// may not be valid and/or tracked, see <see cref="TrackingSpacePose"/> for more information on how to use its data.</para>
    /// <para>Scene anchors follow a different transform from the raw OpenXR data than spatial anchors'.</para>
    /// </remarks>
    public bool TryGetSceneAnchorPose(out TrackingSpacePose pose)
    {
        if (!OVRPlugin.TryLocateSpace(Handle, OVRPlugin.GetTrackingOriginType(), out var posef, out var locationFlags))
        {
            pose = default;
            return false;
        }

        // Transform from OpenXR Right-handed coordinate system
        // to Unity Left-handed coordinate system with additional 180 rotation around +y
        var position = posef.Position.FromFlippedZVector3f();
        var rotation = new Quaternion(-posef.Orientation.z, posef.Orientation.w, -posef.Orientation.x,
            posef.Orientation.y);
        pose = new TrackingSpacePose(position, rotation, locationFlags);
        return true;
    }

    /// <summary>
    /// Tries to get the <see cref="TrackingSpacePose"/> representing the position and rotation of this anchor, treated as a spatial anchor, in tracking space.
    /// </summary>
    /// <param name="pose">The out <see cref="TrackingSpacePose"/> which will get filled in.</param>
    /// <returns>
    /// True if the request was successful, False otherwise.
    /// </returns>
    /// <remarks>
    /// <para>Although the request may succeed and provide a valid <see cref="TrackingSpacePose"/>, actual position and rotation provided
    /// may not be valid and/or tracked, see <see cref="TrackingSpacePose"/> for more information on how to use its data.</para>
    /// <para>Spatial anchors follow a different transform from the raw OpenXR data than scene anchors'.</para>
    /// </remarks>
    public bool TryGetSpatialAnchorPose(out TrackingSpacePose pose)
    {
        if (!OVRPlugin.TryLocateSpace(Handle, OVRPlugin.GetTrackingOriginType(), out var posef, out var locationFlags))
        {
            pose = default;
            return false;
        }

        // Transform from OpenXR Right-handed coordinate system
        // to Unity Left-handed coordinate system
        var position = posef.Position.FromFlippedZVector3f();
        var rotation = posef.Orientation.FromFlippedZQuatf();
        pose = new TrackingSpacePose(position, rotation, locationFlags);
        return true;
    }

    /// <summary>
    /// A job for determining the pose of an array of scene anchors.
    /// </summary>
    /// <remarks>
    /// This is a jobified version of <see cref="OVRLocatable.TryGetSceneAnchorPose"/>. The
    /// <see cref="TrackingSpacePose"/> for each <see cref="OVRLocatable"/> in <see cref="Locatables"/> is determined
    /// and written to <see cref="Poses"/>.
    ///
    /// The length of <see cref="Locatables"/> and <see cref="Poses"/> must be equal.
    ///
    /// An element of <see cref="Locatables"/> may be invalid (that is, <see cref="OVRLocatable.IsNull"/> is `true`),
    /// in which case the <see cref="TrackingSpacePose.Position"/> and <see cref="TrackingSpacePose.Rotation"/>
    /// properties corresponding to this <see cref="OVRLocatable"/> will be `null`.
    ///
    /// Read more about Unity's job system [here](https://docs.unity3d.com/Manual/JobSystem.html).
    /// </remarks>
    /// <seealso cref="GetSpatialAnchorPosesJob"/>
    public struct GetSceneAnchorPosesJob : IJobFor
    {
        /// <summary>
        /// The array of locatable components from which to read the anchor's pose.
        /// </summary>
        /// <remarks>
        /// This array must have the same length as <see cref="Poses"/>.
        /// </remarks>
        [ReadOnly]
        public NativeArray<OVRLocatable> Locatables;

        /// <summary>
        /// The array of <see cref="TrackingSpacePose"/>s in which to store the resulting pose.
        /// </summary>
        /// <remarks>
        /// This array must have the same length as <see cref="Locatables"/>.
        /// </remarks>
        [WriteOnly]
        public NativeArray<TrackingSpacePose> Poses;

        void IJobFor.Execute(int index)
        {
            var locatable = Locatables[index];
            Poses[index] = !locatable.IsNull && locatable.TryGetSceneAnchorPose(out var pose)
                ? pose
                : default;
        }
    }

    /// <summary>
    /// A job for determining the pose of an array of spatial anchors.
    /// </summary>
    /// <remarks>
    /// This is a jobified version of <see cref="OVRLocatable.TryGetSpatialAnchorPose"/>. The
    /// <see cref="TrackingSpacePose"/> for each <see cref="OVRLocatable"/> in <see cref="Locatables"/> is determined
    /// and written to <see cref="Poses"/>.
    ///
    /// The length of <see cref="Locatables"/> and <see cref="Poses"/> must be equal.
    ///
    /// An element of <see cref="Locatables"/> may be invalid (that is, <see cref="OVRLocatable.IsNull"/> is `true`),
    /// in which case the <see cref="TrackingSpacePose.Position"/> and <see cref="TrackingSpacePose.Rotation"/>
    /// properties corresponding to this <see cref="OVRLocatable"/> will be `null`.
    ///
    /// Read more about Unity's job system [here](https://docs.unity3d.com/Manual/JobSystem.html).
    /// </remarks>
    /// <seealso cref="GetSceneAnchorPosesJob"/>
    public struct GetSpatialAnchorPosesJob : IJobFor
    {
        /// <summary>
        /// The array of locatable components from which to read the anchor's pose.
        /// </summary>
        /// <remarks>
        /// This array must have the same length as <see cref="Poses"/>.
        /// </remarks>
        [ReadOnly]
        public NativeArray<OVRLocatable> Locatables;

        /// <summary>
        /// The array of <see cref="TrackingSpacePose"/>s in which to store the resulting pose.
        /// </summary>
        /// <remarks>
        /// This array must have the same length as <see cref="Locatables"/>.
        /// </remarks>
        [WriteOnly]
        public NativeArray<TrackingSpacePose> Poses;

        void IJobFor.Execute(int index)
        {
            var locatable = Locatables[index];
            Poses[index] = !locatable.IsNull && locatable.TryGetSpatialAnchorPose(out var pose)
                ? pose
                : default;
        }
    }

    /// <summary>
    /// A job which transforms an array of <see cref="TrackingSpacePose"/> to a new space.
    /// </summary>
    /// <remarks>
    /// This job multiples each <see cref="TrackingSpacePose"/> in <see cref="Poses"/> by <see cref="Transform"/> and
    /// stores the result back into <see cref="Poses"/> (that is, in-place).
    ///
    /// <example>
    /// This job is useful for converting an anchor's tracking space pose into world space:
    /// <code><![CDATA[
    /// void TransformPoses(NativeArray<TrackingSpacePose> poses, Transform transform)
    /// {
    ///     var jobHandle = new TransformPosesJob
    ///     {
    ///         Poses = poses,
    ///         Transform = transform.localToWorldMatrix,
    ///         Rotation = transform.rotation,
    ///     }.Schedule(poses.Length);
    /// }
    /// ]]></code>
    /// </example>
    ///
    /// Read more about Unity's job system [here](https://docs.unity3d.com/Manual/JobSystem.html).
    /// </remarks>
    public struct TransformPosesJob : IJobFor
    {
        /// <summary>
        /// The poses to transform.
        /// </summary>
        public NativeArray<TrackingSpacePose> Poses;

        /// <summary>
        /// The transform to apply to each <see cref="TrackingSpacePose"/> position in <see cref="Poses"/>.
        /// </summary>
        /// <remarks>
        /// You can generate a `Matrix4x4` from a `UnityEngine.Transform` with
        /// [transform.localToWorldMatrix](https://docs.unity3d.com/ScriptReference/Transform-localToWorldMatrix.html).
        /// </remarks>
        public Matrix4x4 Transform;

        /// <summary>
        /// The rotation to apply to each <see cref="TrackingSpacePose"/> rotation in <see cref="Poses"/>.
        /// </summary>
        /// <remarks>
        /// Typically, this should be the rotational component of <see cref="Transform"/>. It is separate because
        /// the rotation is not guaranteed to be extracted from a `Matrix4x4` if, for example, it has non-uniform scale.
        /// </remarks>
        public Quaternion Rotation;

        void IJobFor.Execute(int index)
        {
            var pose = Poses[index];
            Poses[index] = new TrackingSpacePose(
                pose.Position.HasValue ? Transform.MultiplyPoint(pose.Position.Value) : Vector3.zero,
                Rotation * pose.Rotation ?? Quaternion.identity,
                pose.Flags);
        }
    }

    /// <summary>
    /// Sets the world space transform of Unity transforms according to an array of <see cref="TrackingSpacePose"/>.
    /// </summary>
    /// <remarks>
    /// This job reads the poses in <see cref="Poses"/> and uses them to set the world space transform of each
    /// `UnityEngine.Transform` described by a
    /// [TransformAccessArray](https://docs.unity3d.com/ScriptReference/Jobs.TransformAccessArray.html).
    ///
    /// You can chain this job with a <see cref="TransformPosesJob"/> to first transform <see cref="Pose"/> into world
    /// space.
    /// </remarks>
    public struct SetWorldSpaceTransformsJob : IJobParallelForTransform
    {
        /// <summary>
        /// The array of poses to apply to each element of the `TransformAccessArray`.
        /// </summary>
        [ReadOnly]
        public NativeArray<TrackingSpacePose> Poses;

        void IJobParallelForTransform.Execute(int index, TransformAccess transform)
        {
            var pose = Poses[index];

            if (pose.Position.HasValue && pose.Rotation.HasValue)
            {
                transform.SetPositionAndRotation(pose.Position.Value, pose.Rotation.Value);
            }
            else if (pose.Position.HasValue)
            {
                transform.position = pose.Position.Value;
            }
            else if (pose.Rotation.HasValue)
            {
                transform.rotation = pose.Rotation.Value;
            }
        }
    }

    /// <summary>
    /// Sets the local space transform of Unity transforms according to an array of <see cref="TrackingSpacePose"/>.
    /// </summary>
    /// <remarks>
    /// This job reads the poses in <see cref="Poses"/> and uses them to set the local space transform of each
    /// `UnityEngine.Transform` described by a
    /// [TransformAccessArray](https://docs.unity3d.com/ScriptReference/Jobs.TransformAccessArray.html).
    ///
    /// A <see cref="TrackingSpacePose"/> consists of a position and rotation, either of which may be valid or invalid.
    /// This job sets the transform's position and rotation depending on whether they are valid.
    /// </remarks>
    public struct SetLocalSpaceTransformsJob : IJobParallelForTransform
    {
        [ReadOnly]
        public NativeArray<TrackingSpacePose> Poses;

        public void Execute(int index, TransformAccess transform)
        {
            var pose = Poses[index];

            if (pose.Position.HasValue && pose.Rotation.HasValue)
            {
                transform.SetLocalPositionAndRotation(pose.Position.Value, pose.Rotation.Value);
            }
            else if (pose.Position.HasValue)
            {
                transform.localPosition = pose.Position.Value;
            }
            else if (pose.Rotation.HasValue)
            {
                transform.localRotation = pose.Rotation.Value;
            }
        }
    }

    private struct CopyPosesJob : IJobFor
    {
        [ReadOnly]
        public NativeArray<TrackingSpacePose> PosesIn;

        [WriteOnly]
        public NativeArray<TrackingSpacePose> PosesOut;

        public void Execute(int index) => PosesOut[index] = PosesIn[index];
    }

    private
    static JobHandle ScheduleUpdateTransforms(
        NativeArray<OVRLocatable> locatables,
        TransformAccessArray transforms,
        Transform trackingSpaceToWorldSpaceTransform,
        NativeArray<TrackingSpacePose> posesOut,
        JobHandle inputDeps)
    {
        if (transforms.length != locatables.Length)
        {
            throw new InvalidOperationException(
                $"The length of {nameof(transforms)} ({transforms.length}) must be equal to the length of {nameof(locatables)} ({locatables.Length}).");
        }

        if (posesOut.IsCreated && posesOut.Length != locatables.Length)
        {
            throw new InvalidOperationException(
                $"If {nameof(posesOut)} is a valid array ({nameof(posesOut.IsCreated)}=true), then the length of {nameof(posesOut)} ({posesOut.Length}) must be equal to the length of {nameof(locatables)} ({locatables.Length}).");
        }

        if (locatables.Length == 0)
        {
            return inputDeps;
        }

        // Disposed at the end via a job
        var poses = new NativeArray<TrackingSpacePose>(locatables.Length, Allocator.TempJob);

        // 1. Get the poses
        var jobHandle = new GetSceneAnchorPosesJob
        {
            Locatables = locatables,
            Poses = poses,
        }.ScheduleParallel(locatables.Length, 4, inputDeps);

        // 2. Maybe copy the poses to output
        if (posesOut.IsCreated)
        {
            jobHandle = new CopyPosesJob
            {
                PosesIn = poses,
                PosesOut = posesOut,
            }.ScheduleParallel(poses.Length, 4, jobHandle);
        }

        // 3. Set transforms
        if (trackingSpaceToWorldSpaceTransform)
        {
            // 3.5 Convert to world space
            jobHandle = new TransformPosesJob
            {
                Poses = poses,
                Rotation = trackingSpaceToWorldSpaceTransform.rotation,
                Transform = trackingSpaceToWorldSpaceTransform.localToWorldMatrix,
            }.ScheduleParallel(poses.Length, 4, jobHandle);

            jobHandle = new SetWorldSpaceTransformsJob
            {
                Poses = poses,
            }.Schedule(transforms, jobHandle);
        }
        else
        {
            jobHandle = new SetLocalSpaceTransformsJob
            {
                Poses = poses,
            }.Schedule(transforms, jobHandle);
        }

        // Done with poses
        return poses.Dispose(jobHandle);
    }

    /// <summary>
    /// Sets a collection of transforms according to a parallel collection of anchors.
    /// </summary>
    /// <remarks>
    /// This method accepts a collection of <see cref="OVRAnchor"/>-`Transform` pairs and attempts to get each anchor's
    /// pose, then apply it to the corresponding `Transform`.
    ///
    /// A <see cref="TrackingSpacePose"/> has a position and rotation, either of which may be valid and tracked.
    /// If a component is not valid, it is ignored (not written to the corresponding `Transform` in <paramref name="anchors"/>).
    /// If it is valid but not tracked, it is still written to the corresponding `Transform`.
    ///
    /// <example>
    /// Example usage:
    /// <code><![CDATA[
    /// class MyBehaviour : MonoBehaviour
    /// {
    ///     [SerializeField] OVRCameraRig _cameraRig;
    ///
    ///     Dictionary<OVRAnchor, Transform> _anchors = new();
    ///
    ///     void Update()
    ///     {
    ///         OVRLocatable.UpdateSceneAnchorTransforms(_anchors, _cameraRig.transform, null);
    ///     }
    /// }
    /// ]]></code>
    /// </example>
    ///
    /// This method is similar to calling <see cref="TryGetSceneAnchorPose"/> on each <see cref="OVRAnchor"/> in
    /// <paramref name="anchors"/> and applying the result to each `Transform` in <paramref name="anchors"/>. However,
    /// it correctly handles all permutations of pose validity and is more efficient to perform as a batch operation.
    /// </remarks>
    /// <param name="anchors">A collection of anchor-transform pairs. The pose of each <see cref="OVRAnchor"/> is used
    /// to set its corresponding `Transform`.</param>
    /// <param name="trackingSpaceToWorldSpaceTransform">
    /// (Optional) The transform to apply to each pose before setting the corresponding transform in
    /// <paramref name="anchors"/>. If not `null`, this transform is applied to the pose, and this method then sets
    /// the world space transform. If `null`, no transform is applied and the local space transform is set.
    /// </param>
    /// <param name="trackingSpacePoses">
    /// (Optional) If not `null`, <paramref name="trackingSpacePoses"/> is cleared and then the
    /// <see cref="TrackingSpacePose"/> of each <see cref="OVRAnchor"/> is added to it. This is an optional parameter,
    /// but may be useful information since it also tells you whether a pose is valid and tracked.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchors"/> is `null`.</exception>
    public static void UpdateSceneAnchorTransforms(IEnumerable<KeyValuePair<OVRAnchor, Transform>> anchors,
        Transform trackingSpaceToWorldSpaceTransform = null,
        List<TrackingSpacePose> trackingSpacePoses = null)
    {
        if (anchors == null)
            throw new ArgumentNullException(nameof(anchors));

        using var locatables = OVRNativeList
            .WithSuggestedCapacityFrom(anchors)
            .AllocateEmpty<OVRLocatable>(Allocator.TempJob);

        using var transformAccessArray = new TransformAccessArray(capacity: locatables.Capacity);

        static OVRLocatable GetLocatableOrDefault(OVRAnchor anchor) =>
            anchor.TryGetComponent<OVRLocatable>(out var locatable) ? locatable : default;

        if (anchors is Dictionary<OVRAnchor, Transform> dict)
        {
            foreach (var (anchor, transform) in dict)
            {
                locatables.Add(GetLocatableOrDefault(anchor));
                transformAccessArray.Add(transform);
            }
        }
        else
        {
            foreach (var (anchor, transform) in anchors.ToNonAlloc())
            {
                locatables.Add(GetLocatableOrDefault(anchor));
                transformAccessArray.Add(transform);
            }
        }

        // Get tracking space poses out of the update job
        using var poses = new NativeArray<TrackingSpacePose>(locatables.Count, Allocator.TempJob);

        // Blocking call
        ScheduleUpdateTransforms(locatables.AsNativeArray(), transformAccessArray, trackingSpaceToWorldSpaceTransform,
                poses, default).Complete();

        // Copy out the original poses if requested
        if (trackingSpacePoses != null)
        {
            trackingSpacePoses.Clear();
            foreach (var pose in poses)
            {
                trackingSpacePoses.Add(pose);
            }
        }
    }
}
