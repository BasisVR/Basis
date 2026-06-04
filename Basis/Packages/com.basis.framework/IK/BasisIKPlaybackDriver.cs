using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Drives IK tracker targets from an AnimationClip for testing IK quality
/// without wearing VR hardware. Creates a hidden reference skeleton from the
/// local avatar, samples the clip onto it each frame, and feeds bone world
/// positions into the tracker system as simulated devices.
///
/// Usage:
///   1. Add this component to any GameObject in the scene
///   2. Assign a humanoid AnimationClip
///   3. Enter Play mode and call Play() or set IsPlaying = true in the inspector
///   4. The avatar will be driven by the animation through the full IK pipeline
/// </summary>
public class BasisIKPlaybackDriver : MonoBehaviour
{
    [Header("Animation")]
    [Tooltip("Humanoid animation clip to play back through the IK system")]
    public AnimationClip Clip;

    [Tooltip("Playback speed multiplier (1 = normal, 0.5 = half speed, etc.)")]
    public float PlaybackSpeed = 1f;

    [Tooltip("Loop the animation when it reaches the end")]
    public bool Loop = true;

    [Header("Playback State")]
    public bool IsPlaying;

    [Tooltip("Current playback position in seconds")]
    public float PlaybackTime;

    [Header("Tracked Roles")]
    [Tooltip("Which bone roles to drive from the animation")]
    public bool DriveHead = true;
    public bool DriveHips = true;
    public bool DriveHands = true;
    public bool DriveFeet = true;
    public bool DriveChest = false;
    public bool DriveElbows = false;
    public bool DriveKnees = false;

    // Reference skeleton for animation sampling
    private GameObject _refRoot;
    private Animator _refAnimator;
    private PlayableGraph _playableGraph;
    private AnimationClipPlayable _clipPlayable;

    // Bone mapping from HumanBodyBones → reference transform
    private Dictionary<HumanBodyBones, Transform> _refBones = new();

    // Active playback targets: simulated devices we're driving
    private struct PlaybackTarget
    {
        public BasisBoneTrackedRole Role;
        public HumanBodyBones HumanBone;
        public Transform RefBoneTransform;
        public BasisInputXRSimulate Device;
    }
    private List<PlaybackTarget> _targets = new();
    private bool _initialized;

    // Map of which HumanBodyBones we care about for each role toggle
    private static readonly (HumanBodyBones bone, BasisBoneTrackedRole role, string group)[] BoneRoleMap =
    {
        (HumanBodyBones.Head,          BasisBoneTrackedRole.Head,          "head"),
        (HumanBodyBones.Hips,          BasisBoneTrackedRole.Hips,          "hips"),
        (HumanBodyBones.LeftHand,      BasisBoneTrackedRole.LeftHand,      "hands"),
        (HumanBodyBones.RightHand,     BasisBoneTrackedRole.RightHand,     "hands"),
        (HumanBodyBones.LeftFoot,      BasisBoneTrackedRole.LeftFoot,      "feet"),
        (HumanBodyBones.RightFoot,     BasisBoneTrackedRole.RightFoot,     "feet"),
        (HumanBodyBones.Chest,         BasisBoneTrackedRole.Chest,         "chest"),
        (HumanBodyBones.LeftLowerArm,  BasisBoneTrackedRole.LeftLowerArm,  "elbows"),
        (HumanBodyBones.RightLowerArm, BasisBoneTrackedRole.RightLowerArm, "elbows"),
        (HumanBodyBones.LeftLowerLeg,  BasisBoneTrackedRole.LeftLowerLeg,  "knees"),
        (HumanBodyBones.RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg, "knees"),
    };

    /// <summary>
    /// Start playback. Initializes the reference skeleton and simulated devices if needed.
    /// </summary>
    public void Play()
    {
        if (Clip == null)
        {
            BasisDebug.LogError("BasisIKPlaybackDriver: No AnimationClip assigned.", BasisDebug.LogTag.IK);
            return;
        }

        if (!_initialized)
        {
            Initialize();
        }

        PlaybackTime = 0f;
        IsPlaying = true;
    }

    /// <summary>
    /// Pause playback without resetting the playback position.
    /// </summary>
    public void Pause()
    {
        IsPlaying = false;
    }

    /// <summary>
    /// Stop playback and clean up simulated devices.
    /// </summary>
    public void Stop()
    {
        IsPlaying = false;
        PlaybackTime = 0f;
        Cleanup();
    }

    private void Update()
    {
        if (!IsPlaying || Clip == null)
            return;

        if (!_initialized)
        {
            Initialize();
            if (!_initialized) return;
        }

        // Advance playback time
        PlaybackTime += Time.deltaTime * PlaybackSpeed;

        if (PlaybackTime >= Clip.length)
        {
            if (Loop)
            {
                PlaybackTime %= Clip.length;
            }
            else
            {
                PlaybackTime = Clip.length;
                IsPlaying = false;
            }
        }

        // Sample the animation on the reference skeleton
        SampleAnimation(PlaybackTime);

        // Push sampled bone positions to simulated tracker devices
        UpdateTrackerPositions();
    }

    private void Initialize()
    {
        var localPlayer = BasisLocalPlayer.Instance;
        if (localPlayer == null || localPlayer.LocalAvatarDriver == null)
        {
            BasisDebug.LogError("BasisIKPlaybackDriver: No local player/avatar available.", BasisDebug.LogTag.IK);
            return;
        }

        // Create reference skeleton
        if (!CreateReferenceSkeleton(localPlayer))
            return;

        // Create simulated tracker devices for each enabled role
        CreateSimulatedDevices();

        _initialized = true;
        BasisDebug.Log($"BasisIKPlaybackDriver: Initialized with {_targets.Count} tracked roles, clip length = {Clip.length:F2}s", BasisDebug.LogTag.IK);
    }

    private bool CreateReferenceSkeleton(BasisLocalPlayer localPlayer)
    {
        // Get the avatar's Animator to clone its skeleton
        Animator sourceAnimator = localPlayer.BasisAvatar != null ? localPlayer.BasisAvatar.Animator : null;
        if (sourceAnimator == null || sourceAnimator.avatar == null || !sourceAnimator.avatar.isHuman)
        {
            BasisDebug.LogError("BasisIKPlaybackDriver: Avatar must be humanoid.", BasisDebug.LogTag.IK);
            return false;
        }

        // Create a hidden root for the reference skeleton
        _refRoot = new GameObject("IK_Playback_Reference");
        _refRoot.hideFlags = HideFlags.HideAndDontSave;
        _refRoot.SetActive(true);

        // Duplicate the bone hierarchy (transforms only, no renderers/colliders)
        DuplicateBoneHierarchy(sourceAnimator.transform, _refRoot.transform);

        // Add Animator with the same Avatar
        _refAnimator = _refRoot.AddComponent<Animator>();
        _refAnimator.avatar = sourceAnimator.avatar;
        _refAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // Match the reference skeleton's world position/scale to the source
        _refRoot.transform.position = sourceAnimator.transform.position;
        _refRoot.transform.rotation = sourceAnimator.transform.rotation;
        _refRoot.transform.localScale = sourceAnimator.transform.lossyScale;

        // Create a PlayableGraph for manual clip evaluation
        _playableGraph = PlayableGraph.Create("IKPlayback");
        _playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

        _clipPlayable = AnimationClipPlayable.Create(_playableGraph, Clip);
        var output = AnimationPlayableOutput.Create(_playableGraph, "output", _refAnimator);
        output.SetSourcePlayable(_clipPlayable);

        _playableGraph.Play();

        // Cache bone transforms from the reference animator
        CacheReferenceBones();

        return true;
    }

    private void DuplicateBoneHierarchy(Transform source, Transform destParent)
    {
        // Recreate the transform hierarchy without any components besides Transform
        for (int i = 0; i < source.childCount; i++)
        {
            Transform child = source.GetChild(i);
            GameObject clone = new GameObject(child.name);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetParent(destParent, false);
            clone.transform.localPosition = child.localPosition;
            clone.transform.localRotation = child.localRotation;
            clone.transform.localScale = child.localScale;

            // Recurse
            DuplicateBoneHierarchy(child, clone.transform);
        }
    }

    private void CacheReferenceBones()
    {
        _refBones.Clear();

        foreach (var (bone, role, group) in BoneRoleMap)
        {
            Transform boneTransform = _refAnimator.GetBoneTransform(bone);
            if (boneTransform != null)
            {
                _refBones[bone] = boneTransform;
            }
        }
    }

    private void CreateSimulatedDevices()
    {
        _targets.Clear();

        // Find or create the simulation manager
        BasisSimulateXR simulator = null;
        var Instance = BasisDeviceManagement.Instance;
        for (int Index = 0; Index < Instance.BaseTypes.Length; Index++)
        {
            BasisBaseTypeManagement bt = Instance.BaseTypes[Index];
            if (bt is BasisSimulateXR sim)
            {
                simulator = sim;
                break;
            }
        }

        if (simulator == null)
        {
            // Create one if it doesn't exist
            GameObject simGO = new GameObject("IKPlayback_SimulateXR");
            simGO.hideFlags = HideFlags.HideAndDontSave;
            simulator = simGO.AddComponent<BasisSimulateXR>();
            var oldArray = Instance.BaseTypes;
            Array.Resize(ref oldArray, oldArray.Length + 1);
            oldArray[^1] = simulator;
            Instance.BaseTypes = oldArray;
        }

        foreach (var (bone, role, group) in BoneRoleMap)
        {
            if (!IsGroupEnabled(group))
                continue;

            if (!_refBones.ContainsKey(bone))
                continue;

            // Check if this role is already taken by a real device
            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput existing, role))
            {
                BasisDebug.Log($"BasisIKPlaybackDriver: Skipping {role} — already has a real device.", BasisDebug.LogTag.IK);
                continue;
            }

            string deviceId = $"IKPlayback_{role}";
            BasisInputXRSimulate device = simulator.CreatePhysicalTrackedDevice(
                UniqueID: deviceId,
                UnUniqueID: "IKPlaybackDevice",
                Role: role,
                hasrole: true,
                subSystems: "BasisIKPlayback"
            );

            _targets.Add(new PlaybackTarget
            {
                Role = role,
                HumanBone = bone,
                RefBoneTransform = _refBones[bone],
                Device = device
            });

            BasisDebug.Log($"BasisIKPlaybackDriver: Created simulated device for {role}", BasisDebug.LogTag.IK);
        }

        // Run calibration so the IK system knows about these devices
        if (_targets.Count > 0)
        {
            Basis.Scripts.Avatar.BasisAvatarIKStageCalibration.FullBodyCalibration();
        }
    }

    private bool IsGroupEnabled(string group)
    {
        return group switch
        {
            "head" => DriveHead,
            "hips" => DriveHips,
            "hands" => DriveHands,
            "feet" => DriveFeet,
            "chest" => DriveChest,
            "elbows" => DriveElbows,
            "knees" => DriveKnees,
            _ => false
        };
    }

    private void SampleAnimation(float time)
    {
        if (!_playableGraph.IsValid())
            return;

        _clipPlayable.SetTime(time);
        _playableGraph.Evaluate();
    }

    private void UpdateTrackerPositions()
    {
        Transform playerTransform = BasisLocalPlayer.Instance.transform;

        for (int i = 0; i < _targets.Count; i++)
        {
            PlaybackTarget target = _targets[i];
            if (target.Device == null || target.RefBoneTransform == null)
                continue;

            // Get the bone's world position/rotation from the reference skeleton
            Vector3 worldPos = target.RefBoneTransform.position;
            Quaternion worldRot = target.RefBoneTransform.rotation;

            // FollowMovement is parented to the player transform,
            // and BasisInputXRSimulate reads local pos/rot.
            // Convert world → player-local space.
            target.Device.FollowMovement.localPosition = playerTransform.InverseTransformPoint(worldPos);
            target.Device.FollowMovement.localRotation = Quaternion.Inverse(playerTransform.rotation) * worldRot;
        }
    }

    private void Cleanup()
    {
        // Destroy simulated devices
        for (int i = 0; i < _targets.Count; i++)
        {
            PlaybackTarget target = _targets[i];
            if (target.Device != null)
            {
                // Unassign from bone control
                target.Device.UnAssignTracker();

                // Remove from device management
                BasisDeviceManagement.Instance.AllInputDevices.Remove(target.Device);

                // Destroy the device GameObject
                if (target.Device.gameObject != null)
                    Destroy(target.Device.gameObject);
            }
        }
        _targets.Clear();

        // Destroy playable graph
        if (_playableGraph.IsValid())
        {
            _playableGraph.Destroy();
        }

        // Destroy reference skeleton
        if (_refRoot != null)
        {
            Destroy(_refRoot);
            _refRoot = null;
        }

        _refAnimator = null;
        _refBones.Clear();
        _initialized = false;
    }

    private void OnDestroy()
    {
        Cleanup();
    }
}
