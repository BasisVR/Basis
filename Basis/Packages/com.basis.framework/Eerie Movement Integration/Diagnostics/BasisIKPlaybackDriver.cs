using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
[NoAutoStaticsCleanup]
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
    private GameObject refRoot;
    private Animator refAnimator;
    private PlayableGraph playbackGraph;
    private AnimationClipPlayable clipPlayable;
    private Dictionary<HumanBodyBones, Transform> refBones = new();
    private struct PlaybackTarget
    {
        public BasisBoneTrackedRole Role;
        public HumanBodyBones HumanBone;
        public Transform RefBoneTransform;
        public BasisInputXRSimulate Device;
    }
    private List<PlaybackTarget> targets = new();
    private bool initialized;
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
    public void Play()
    {
        if (Clip == null)
        {
            BasisDebug.LogError("BasisIKPlaybackDriver: No AnimationClip assigned.", BasisDebug.LogTag.IK);
            return;
        }

        if (!initialized)
        {
            Initialize();
        }

        PlaybackTime = 0f;
        IsPlaying = true;
    }
    public void Pause()
    {
        IsPlaying = false;
    }
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

        if (!initialized)
        {
            Initialize();
            if (!initialized) return;
        }

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

        SampleAnimation(PlaybackTime);

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

        if (!CreateReferenceSkeleton(localPlayer))
            return;

        CreateSimulatedDevices();

        initialized = true;
        BasisDebug.Log($"BasisIKPlaybackDriver: Initialized with {targets.Count} tracked roles, clip length = {Clip.length:F2}s", BasisDebug.LogTag.IK);
    }
    private bool CreateReferenceSkeleton(BasisLocalPlayer localPlayer)
    {
        Animator sourceAnimator = localPlayer.BasisAvatar != null ? localPlayer.BasisAvatar.Animator : null;
        if (sourceAnimator == null || sourceAnimator.avatar == null || !sourceAnimator.avatar.isHuman)
        {
            BasisDebug.LogError("BasisIKPlaybackDriver: Avatar must be humanoid.", BasisDebug.LogTag.IK);
            return false;
        }

        refRoot = new GameObject("IK_Playback_Reference");
        refRoot.hideFlags = HideFlags.HideAndDontSave;
        refRoot.SetActive(true);

        DuplicateBoneHierarchy(sourceAnimator.transform, refRoot.transform);

        refAnimator = refRoot.AddComponent<Animator>();
        refAnimator.avatar = sourceAnimator.avatar;
        refAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        refRoot.transform.position = sourceAnimator.transform.position;
        refRoot.transform.rotation = sourceAnimator.transform.rotation;
        refRoot.transform.localScale = sourceAnimator.transform.lossyScale;

        playbackGraph = PlayableGraph.Create("IKPlayback");
        playbackGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

        clipPlayable = AnimationClipPlayable.Create(playbackGraph, Clip);
        var output = AnimationPlayableOutput.Create(playbackGraph, "output", refAnimator);
        output.SetSourcePlayable(clipPlayable);

        playbackGraph.Play();

        CacheReferenceBones();

        return true;
    }
    private void DuplicateBoneHierarchy(Transform source, Transform destParent)
    {
        for (int i = 0; i < source.childCount; i++)
        {
            Transform child = source.GetChild(i);
            GameObject clone = new GameObject(child.name);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetParent(destParent, false);
            clone.transform.localPosition = child.localPosition;
            clone.transform.localRotation = child.localRotation;
            clone.transform.localScale = child.localScale;

            DuplicateBoneHierarchy(child, clone.transform);
        }
    }
    private void CacheReferenceBones()
    {
        refBones.Clear();

        foreach (var (bone, role, group) in BoneRoleMap)
        {
            Transform boneTransform = refAnimator.GetBoneTransform(bone);
            if (boneTransform != null)
            {
                refBones[bone] = boneTransform;
            }
        }
    }
    private void CreateSimulatedDevices()
    {
        targets.Clear();

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

            if (!refBones.ContainsKey(bone))
                continue;

            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput existing, role))
            {
                BasisDebug.Log($"BasisIKPlaybackDriver: Skipping {role} — already has a real device.", BasisDebug.LogTag.IK);
                continue;
            }

            string deviceId = $"IKPlayback_{role}";
            BasisInputXRSimulate device = simulator.CreatePhysicalTrackedDevice( UniqueID: deviceId, UnUniqueID: "IKPlaybackDevice", Role: role, hasrole: true, subSystems: "BasisIKPlayback" );

            targets.Add(new PlaybackTarget
            {
                Role = role,
                HumanBone = bone,
                RefBoneTransform = refBones[bone],
                Device = device
            });

            BasisDebug.Log($"BasisIKPlaybackDriver: Created simulated device for {role}", BasisDebug.LogTag.IK);
        }

        if (targets.Count > 0)
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
        if (!playbackGraph.IsValid())
            return;

        clipPlayable.SetTime(time);
        playbackGraph.Evaluate();
    }
    private void UpdateTrackerPositions()
    {
        Transform playerTransform = BasisLocalPlayer.Instance.transform;

        for (int i = 0; i < targets.Count; i++)
        {
            PlaybackTarget target = targets[i];
            if (target.Device == null || target.RefBoneTransform == null)
                continue;

            Vector3 worldPos = target.RefBoneTransform.position;
            Quaternion worldRot = target.RefBoneTransform.rotation;

            target.Device.FollowMovement.localPosition = playerTransform.InverseTransformPoint(worldPos);
            target.Device.FollowMovement.localRotation = Quaternion.Inverse(playerTransform.rotation) * worldRot;
        }
    }
    private void Cleanup()
    {
        for (int i = 0; i < targets.Count; i++)
        {
            PlaybackTarget target = targets[i];
            if (target.Device != null)
            {
                target.Device.UnAssignTracker();

                BasisDeviceManagement.Instance.AllInputDevices.Remove(target.Device);

                if (target.Device.gameObject != null)
                    Destroy(target.Device.gameObject);
            }
        }
        targets.Clear();

        if (playbackGraph.IsValid())
        {
            playbackGraph.Destroy();
        }

        if (refRoot != null)
        {
            Destroy(refRoot);
            refRoot = null;
        }

        refAnimator = null;
        refBones.Clear();
        initialized = false;
    }
    private void OnDestroy()
    {
        Cleanup();
    }
}
