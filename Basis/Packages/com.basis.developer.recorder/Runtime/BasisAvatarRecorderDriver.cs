using System;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

public static class BasisAvatarRecorderDriver
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

    private static HumanPoseHandler _poseHandler;
    private static Animator _boundAnimator;
    private static HumanPose _pose;

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

    public static void RequestStop()
    {
        if (State == RecorderState.Idle)
            return;

        if (State == RecorderState.Recording)
            BasisAvatarRecorder.StopRecording();

        State = RecorderState.Idle;
        CountdownRemaining = 0f;
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

        BasisAvatarRecorder.StartRecording();
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
        if (!EnsurePoseHandler())
        {
            RequestStop();
            return;
        }

        float dt = Time.unscaledDeltaTime;
        _poseHandler.GetHumanPose(ref _pose);

        float scale = _boundAnimator != null ? _boundAnimator.transform.localScale.y : 1f;
        BasisAvatarRecorder.StoreData(dt, _pose.bodyRotation, _pose.bodyPosition, _pose.muscles, scale);

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
