using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Text;
using Basis.Developer.Recorder;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using GatorDragonGames.JigglePhysics;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisAvatarRecorderDriver
{
    public enum RecorderState
    {
        Idle,
        CountingDown,
        Recording
    }

    private const int TickPriority = 150;

    public static RecorderState State { get; private set; } = RecorderState.Idle;
    public static float ElapsedSeconds { get; private set; }
    public static int FrameCount { get; private set; }
    public static float CountdownRemaining { get; private set; }

    public static event Action OnChanged;

    private static bool _tickRegistered;
    private static bool _autoStop;
    private static float _maxDurationSeconds;

    [NoAutoStaticsCleanup] private static HumanPoseHandler _poseHandler;
    private static Animator _boundAnimator;
    private static HumanPose _pose;
    private static Transform[] _bones = Array.Empty<Transform>();

    public static bool IsBusy => State != RecorderState.Idle;

    public static void RequestStart(float countdownSeconds, bool autoStop, float maxDurationSeconds)
    {
        if (State != RecorderState.Idle)
            return;

        if (!TryGetLocalAnimator(out Animator animator) || animator.avatar == null || !animator.avatar.isHuman)
        {
            BasisDebug.LogError("BasisAvatarRecorderDriver: local avatar is missing or not humanoid; cannot record.");
            return;
        }

        _autoStop = autoStop;
        _maxDurationSeconds = Mathf.Max(0f, maxDurationSeconds);

        EnsureTickRegistered();
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;

        if (countdownSeconds > 0f)
        {
            State = RecorderState.CountingDown;
            CountdownRemaining = countdownSeconds;
        }
        else
        {
            BeginRecording();
        }

        OnChanged?.Invoke();
    }

    private static void OnQuitting()
    {
        Application.quitting -= OnQuitting;
        try
        {
            RequestStop();
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"BasisAvatarRecorderDriver: stop on quit failed: {e.Message}");
        }
    }

    public static void RequestStop()
    {
        if (State == RecorderState.Idle)
            return;

        if (State == RecorderState.Recording)
            BasisAvatarRecorder.StopRecording();

        State = RecorderState.Idle;
        CountdownRemaining = 0f;
        _bones = Array.Empty<Transform>();
        ReleasePoseHandler();
        OnChanged?.Invoke();
    }

    private static void BeginRecording()
    {
        if (!EnsurePoseHandler())
        {
            State = RecorderState.Idle;
            return;
        }

        string[] bonePaths = Array.Empty<string>();
        _bones = BasisRecorderSettings.AdditionalBones.RawValue ? CollectAdditionalBones(_boundAnimator, out bonePaths) : Array.Empty<Transform>();
        BasisAvatarRecorder.StartRecording(_boundAnimator.humanScale, BasisRecorderSettings.RootMotion.RawValue, bonePaths);
        State = RecorderState.Recording;
        ElapsedSeconds = 0f;
        FrameCount = 0;
        CountdownRemaining = 0f;
    }

    private static void Tick()
    {
        switch (State)
        {
            case RecorderState.CountingDown:
                CountdownRemaining -= Time.unscaledDeltaTime;
                if (CountdownRemaining <= 0f)
                    BeginRecording();
                NotifyChangedThrottled();
                break;

            case RecorderState.Recording:
                CaptureFrame();
                if (_autoStop && _maxDurationSeconds > 0f && ElapsedSeconds >= _maxDurationSeconds)
                {
                    RequestStop();
                    break;
                }
                NotifyChangedThrottled();
                break;
        }
    }

    // The per-frame invoke drove UI refresh at capture rate; 10 Hz is plenty for a
    // timer readout and keeps subscriber work off the capture path.
    private const float ChangedNotifyInterval = 0.1f;
    private static float _nextChangedNotify;

    private static void NotifyChangedThrottled()
    {
        float now = Time.unscaledTime;
        if (now < _nextChangedNotify) return;
        _nextChangedNotify = now + ChangedNotifyInterval;
        OnChanged?.Invoke();
    }

    private static void CaptureFrame()
    {
        if (_poseHandler == null || !TryGetLocalAnimator(out Animator animator) || animator != _boundAnimator)
        {
            RequestStop();
            return;
        }

        float dt = Time.unscaledDeltaTime;
        _poseHandler.GetHumanPose(ref _pose);
        if (_bones.Length > 0)
            BasisLateJiggleCompletion.CompleteIfPending();

        Transform root = _boundAnimator.transform;
        BasisAvatarRecorder.StoreData(dt, _pose.bodyRotation, _pose.bodyPosition, _pose.muscles, root.localScale.y, root, _bones);

        ElapsedSeconds += dt;
        FrameCount++;
    }

    private static bool EnsurePoseHandler()
    {
        if (!TryGetLocalAnimator(out Animator animator) || animator.avatar == null || !animator.avatar.isHuman)
            return false;

        if (_poseHandler != null && _boundAnimator == animator)
            return true;

        ReleasePoseHandler();
        _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        _boundAnimator = animator;
        return true;
    }

    private static void ReleasePoseHandler()
    {
        _poseHandler?.Dispose();
        _poseHandler = null;
        _boundAnimator = null;
    }

    public static Transform[] CollectAdditionalBones(Animator animator, out string[] paths)
    {
        Transform root = animator.transform;
        HashSet<Transform> skip = new HashSet<Transform>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            Transform t = animator.GetBoneTransform((HumanBodyBones)i);
            if (t != null) skip.Add(t);
        }
        for (Transform t = animator.GetBoneTransform(HumanBodyBones.Hips); t != null && t != root; t = t.parent) skip.Add(t);

        HashSet<Transform> wanted = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform[] smrBones = smr.bones;
            for (int i = 0; i < smrBones.Length; i++) AddWithParents(smrBones[i], root, wanted);
        }
        JiggleRig[] rigs = BasisLocalAvatarDriver.JiggleRigs;
        for (int i = 0; rigs != null && i < rigs.Length; i++)
        {
            Transform rigRoot = rigs[i] != null ? rigs[i].GetJiggleRigData().rootBone : null;
            if (rigRoot == null || !rigRoot.IsChildOf(root)) continue;
            foreach (Transform t in rigRoot.GetComponentsInChildren<Transform>(true)) AddWithParents(t, root, wanted);
        }

        List<Transform> bones = new List<Transform>(wanted.Count);
        List<string> names = new List<string>(wanted.Count);
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!wanted.Contains(t) || skip.Contains(t)) continue;
            bones.Add(t);
            names.Add(GetRelativePath(root, t));
        }
        paths = names.ToArray();
        return bones.ToArray();
    }

    private static void AddWithParents(Transform t, Transform root, HashSet<Transform> into)
    {
        if (t == null || !t.IsChildOf(root)) return;
        for (; t != null && t != root && into.Add(t); t = t.parent) { }
    }

    public static string GetRelativePath(Transform root, Transform t)
    {
        StringBuilder sb = new StringBuilder(t.name);
        for (Transform p = t.parent; p != null && p != root; p = p.parent) sb.Insert(0, '/').Insert(0, p.name);
        return sb.ToString();
    }

    private static bool TryGetLocalAnimator(out Animator animator)
    {
        animator = null;
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null || local.BasisAvatar == null)
            return false;
        animator = local.BasisAvatar.Animator;
        return animator != null;
    }

    private static void EnsureTickRegistered()
    {
        if (_tickRegistered)
            return;
        BasisLocalPlayer.AfterSimulateOnRender.AddAction(TickPriority, Tick);
        _tickRegistered = true;
    }
}
