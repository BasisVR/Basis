using System;
using System.Threading;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis;
using UnityEngine;
using static Basis.Network.Core.BasisScreenShareProtocol;

public class BasisScreenShare : MonoBehaviour
{
    public const string SpawnUrl = "Screen Share";
    public enum ShareState { Idle, Starting, Waiting, Live, Error }
    public BasisMediaPlayer Player;
    public BasisMediaPlayerAudio Audio;
    public BasisPickupInteractable Pickup;
    public bool IsRemote { get; private set; }
    public ShareState State { get; private set; }
    public string StatusError { get; private set; }
    public string SourceId { get; private set; }
    public string SourceName { get; private set; }
    public int Viewers { get; private set; }
    public int DirectViewers => sender != null ? sender.DirectViewers : 0;
    public int TotalViewers => Viewers + DirectViewers;
    public int TargetKbps { get; private set; }
    public int CurrentKbps => sender != null ? sender.CurrentKbps : TargetKbps;
    public BasisScreenShareNative.Status CaptureStatus { get; private set; }
    public BasisScreenShareClient.Remote Remote { get; private set; }
    public event Action Changed;
    const float PoseInterval = 0.1f, StatusInterval = 0.5f, KeyRequestInterval = 1f;
    ulong capture, attachedPreview, attachedCapture;
    bool accepted, previewOpen, awaitingStart;
    volatile bool directChanged;
    BasisScreenShareSender sender;
    float nextPose, nextStatus, nextKeyRequest;
    Vector3 sentPosition;
    Quaternion sentRotation;
    float sentScale, baseScale = 1f;
    int infoVersion = -1, poseVersion = -1;
    bool controlling = true, held, following;
    ScreenPose followTarget;
    StreamInfo info;
    bool infoPending;
    BasisScreenShareNative.Source source;
    BasisScreenShareControls controls;
    Bounds screenBounds = new Bounds(Vector3.zero, new Vector3(14.46f, 8.14f, 0f));

    void Awake()
    {
        if (Player == null) TryGetComponent(out Player);
        if (Audio == null) TryGetComponent(out Audio);
        if (Pickup == null) TryGetComponent(out Pickup);
        if (Pickup != null)
        {
            Pickup.OnInteractStartEvent.AddListener(OnGrabbed);
            Pickup.OnInteractEndEvent.AddListener(OnReleased);
        }
        poseVersion = BasisScreenShareClient.LocalPoseVersion;
        if (Player != null)
        {
            Player.ListInMediaMenu = false;
            Player.playOnStart = false;
        }
        baseScale = transform.localScale.x;
        if (TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) screenBounds = filter.sharedMesh.bounds;
        BasisScreenShareBack.BuildBack(transform, screenBounds);
        controls = gameObject.AddComponent<BasisScreenShareControls>();
        controls.Bind(this, screenBounds);
    }

    void OnGrabbed(BasisInput input)
    {
        held = true;
        controlling = true;
        following = false;
        nextPose = 0f;
        sentScale = -1f;
    }

    void OnReleased(BasisInput input)
    {
        held = false;
        nextPose = 0f;
    }

    void FollowPlacement(BasisScreenShareClient.Remote remote, bool snap)
    {
        if (BasisScreenShareClient.TryGetPlacement(remote, ref poseVersion, out ScreenPose pose) && !held)
        {
            controlling = false;
            followTarget = pose;
            following = true;
        }
        if (!following && !snap) return;
        Vector3 target = new Vector3(followTarget.PX, followTarget.PY, followTarget.PZ);
        Quaternion rotation = new Quaternion(followTarget.RX, followTarget.RY, followTarget.RZ, followTarget.RW).normalized;
        float scale = baseScale * followTarget.Scale;
        Transform t = transform;
        float blend = snap ? 1f : 1f - Mathf.Exp(-15f * Time.deltaTime);
        t.SetPositionAndRotation(Vector3.Lerp(t.position, target, blend), Quaternion.Slerp(t.rotation, rotation, blend));
        t.localScale = Vector3.one * Mathf.Lerp(t.localScale.x, scale, blend);
        if (blend >= 1f || (t.position - target).sqrMagnitude < 0.000001f && Quaternion.Angle(t.rotation, rotation) < 0.01f && Mathf.Abs(t.localScale.x - scale) < 0.00001f)
        {
            t.SetPositionAndRotation(target, rotation);
            t.localScale = Vector3.one * scale;
            following = false;
        }
        sentPosition = t.position;
        sentRotation = t.rotation;
        sentScale = t.localScale.x;
    }

    bool PoseDue(float now)
    {
        if (!controlling || now < nextPose) return false;
        nextPose = now + PoseInterval;
        Transform t = transform;
        if ((t.position - sentPosition).sqrMagnitude <= 0.0001f && Quaternion.Angle(t.rotation, sentRotation) <= 0.25f && Mathf.Abs(t.localScale.x - sentScale) <= 0.001f) return false;
        sentPosition = t.position;
        sentRotation = t.rotation;
        sentScale = t.localScale.x;
        return true;
    }

    void UpdateBackPanel()
    {
        bool menuOpen = BasisMainMenu.Instance != null;
        if (controls != null && controls.Visible != menuOpen) controls.SetVisible(menuOpen);
    }

    public void OnHidePressed()
    {
        if (IsRemote)
        {
            if (Remote != null) BasisScreenShareClient.Hide(Remote);
            else Destroy(gameObject);
            return;
        }
        StopSharing();
        foreach (var pair in BasisRuntimeSpawnRegistry.SpawnedGameobjects)
        {
            if (pair.Value != gameObject) continue;
            _ = BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(pair.Key);
            return;
        }
        Destroy(gameObject);
    }

    void Start()
    {
        if (IsRemote) return;
        if (!BasisScreenShareClient.RegisterLocal(this))
        {
            Destroy(gameObject);
            return;
        }
        BasisScreenShareSettings.Resolution.OnChanged += OnTextSettingChanged;
        BasisScreenShareSettings.FrameRate.OnChanged += OnTextSettingChanged;
        BasisScreenShareSettings.Quality.OnChanged += OnTextSettingChanged;
        BasisScreenShareSettings.AudioSource.OnChanged += OnTextSettingChanged;
        BasisScreenShareSettings.ShareAudio.OnChanged += OnToggleSettingChanged;
        BasisScreenShareSettings.ShowCursor.OnChanged += OnToggleSettingChanged;
        BasisScreenSharePanelProvider.OpenPanel();
    }

    void OnTextSettingChanged(string _) => Recast(source);

    void OnToggleSettingChanged(bool _) => Recast(source);

    public bool IsSharing => State != ShareState.Idle && State != ShareState.Error;

    public void ConfigureRemote(BasisScreenShareClient.Remote remote)
    {
        IsRemote = true;
        Remote = remote;
        name = $"Screen Share ({BasisScreenShareClient.SharerName(remote.Sharer)})";
        controlling = false;
        poseVersion = -1;
        foreach (Rigidbody body in GetComponentsInChildren<Rigidbody>()) body.isKinematic = true;
        BasisScreenShareClient.TryGetPlacement(remote, ref poseVersion, out followTarget);
        FollowPlacement(remote, true);
    }

    void Raise() => Changed?.Invoke();

    public static List<BasisScreenShareNative.Source> Sources() => BasisScreenShareNative.Sources();

    public bool StartSharing(BasisScreenShareNative.Source source)
    {
        StopCapture(false);
        if (sender == null)
        {
            sender = new BasisScreenShareSender();
            sender.DirectViewersChanged += () => directChanged = true;
        }
        if (!OpenCapture(source, 0)) return false;
        State = ShareState.Starting;
        accepted = false;
        awaitingStart = true;
        Viewers = 0;
        sender.SetRouting(false, 0);
        Raise();
        return true;
    }

    public bool Recast(BasisScreenShareNative.Source next)
    {
        if (!IsSharing || next == null) return false;
        long ptsBase = sender != null && sender.LastPts >= 0 ? sender.LastPts + 20_000 : 0;
        CloseCapture();
        if (!OpenCapture(next, ptsBase))
        {
            StopCapture(true);
            return false;
        }
        infoPending = accepted;
        Raise();
        return true;
    }

    void CloseCapture()
    {
        if (capture == 0) return;
        sender?.SetCapture(0, TargetKbps);
        BasisScreenShareNative.bm_capture_close(capture);
        capture = attachedCapture = attachedPreview = 0;
    }

    bool OpenCapture(BasisScreenShareNative.Source source, long ptsBase)
    {
        if (BasisScreenShareNative.CanCapture && !BasisScreenShareNative.PluginCurrent) return Fail(BasisLocalization.Get(Application.isEditor ? "screenShare.error.restart" : "screenShare.error.pluginMissing"));
        if (source == null || !BasisScreenShareNative.CanCapture) return Fail(BasisLocalization.Get("screenShare.error.unsupported"));
        if (BasisScreenShareClient.LockedForMe) return Fail(BasisLocalization.Get("screenShare.error.locked"));
        Vector2Int max = BasisScreenShareSettings.MaxSize(BasisScreenShareSettings.Resolution.RawValue, new Vector2Int(source.width, source.height));
        int fps = BasisScreenShareSettings.Fps(BasisScreenShareSettings.FrameRate.RawValue);
        TargetKbps = BasisScreenShareSettings.Bitrate(BasisScreenShareSettings.Quality.RawValue, max, fps, BasisScreenShareClient.ServerMaxKbps);
        BasisScreenShareNative.Config config = new BasisScreenShareNative.Config { AudioSource = BasisScreenShareSettings.AudioSource.RawValue, PtsBaseUs = ptsBase, SourceId = source.id, MaxWidth = max.x, MaxHeight = max.y, Fps = fps, BitrateKbps = TargetKbps, KeyframeIntervalMs = 10000, AudioBitrateKbps = BasisScreenShareSettings.AudioBitrateKbps, Cursor = BasisScreenShareSettings.ShowCursor.RawValue, Audio = BasisScreenShareSettings.ShareAudio.RawValue };
        if (!BasisScreenShareNative.Open(config, out capture)) return Fail(BasisLocalization.Get("screenShare.error.open"));
        sender?.SetCapture(capture, TargetKbps);
        this.source = source;
        SourceId = source.id;
        SourceName = source.name;
        StatusError = null;
        CaptureStatus = null;
        nextStatus = 0f;
        return true;
    }

    bool Fail(string error)
    {
        StatusError = error;
        State = ShareState.Error;
        Raise();
        return false;
    }

    public void StopSharing()
    {
        StopCapture(true);
        State = ShareState.Idle;
        Raise();
    }

    void StopCapture(bool tellServer)
    {
        CloseCapture();
        if (tellServer && (accepted || awaitingStart)) BasisScreenShareClient.SendStop();
        accepted = awaitingStart = infoPending = false;
        sender?.SetRouting(false, 0);
        CaptureStatus = null;
        if (previewOpen && Player != null) Player.Stop();
        previewOpen = false;
    }

    public void OnAccepted(int serverMaxKbps, int viewers)
    {
        if (capture == 0) return;
        accepted = true;
        Viewers = viewers;
        State = ShareState.Live;
        sender?.SetRouting(true, viewers);
        if (TargetKbps > serverMaxKbps && serverMaxKbps > 0)
        {
            TargetKbps = serverMaxKbps;
            sender?.SetTarget(TargetKbps);
        }
        RequestKeyframe();
        Raise();
    }

    public void OnRefused(byte reason)
    {
        StopCapture(false);
        Fail(BasisLocalization.Get(reason switch
        {
            RefusedNoPermission => "screenShare.error.permission",
            RefusedTooMany => "screenShare.error.tooMany",
            RefusedLocked => "screenShare.error.locked",
            _ => "screenShare.error.refused",
        }));
    }

    public void OnReconnected()
    {
        if (capture == 0) return;
        accepted = false;
        awaitingStart = true;
        Viewers = 0;
        sender?.SetRouting(false, 0);
        State = ShareState.Starting;
        Raise();
    }

    public void RequestKeyframe() => sender?.RequestKeyframe();

    public void OnDirectWatch(ushort viewer, bool watching)
    {
        if (sender == null) return;
        bool changed = watching ? BasisScreenShareClient.HasDirectLink(viewer) && sender.AddDirectViewer(viewer) : sender.RemoveDirectViewer(viewer);
        if (changed) Raise();
    }

    public void OnDirectKeyframe(ushort viewer) => sender?.MarkNeedsKey(viewer);

    public void SetViewers(int count)
    {
        bool gained = count > Viewers;
        Viewers = count;
        sender?.SetRouting(accepted, count);
        if (gained) RequestKeyframe();
        Raise();
    }

    ScreenPose CurrentPose()
    {
        Transform t = transform;
        Quaternion r = t.rotation;
        Vector3 p = t.position;
        return new ScreenPose { PX = p.x, PY = p.y, PZ = p.z, RX = r.x, RY = r.y, RZ = r.z, RW = r.w, Scale = Mathf.Clamp(t.localScale.x / Mathf.Max(baseScale, 0.0001f), 0.05f, 20f) };
    }

    void Update()
    {
        UpdateBackPanel();
        if (IsRemote) UpdateRemote();
        else UpdateLocal();
    }

    void UpdateLocal()
    {
        if (capture == 0) return;
        float now = Time.unscaledTime;
        if (now >= nextStatus)
        {
            nextStatus = now + StatusInterval;
            CaptureStatus = BasisScreenShareNative.ReadStatus(capture);
            if (CaptureStatus == null || CaptureStatus.Failed)
            {
                string error = CaptureStatus != null && !string.IsNullOrEmpty(CaptureStatus.error) ? CaptureStatus.error : BasisLocalization.Get("screenShare.error.ended");
                StopCapture(true);
                Fail(error);
                return;
            }
            if (CaptureStatus.Running && infoPending)
            {
                infoPending = false;
                StreamInfo next = CurrentInfo();
                if (next.Width != info.Width || next.Height != info.Height)
                {
                    if (previewOpen && Player != null) Player.Stop();
                    previewOpen = false;
                }
                bool changed = next.Width != info.Width || next.Height != info.Height || next.Fps != info.Fps || next.HasAudio != info.HasAudio || next.Title != info.Title;
                info = next;
                if (changed) BasisScreenShareClient.SendInfo(info);
                RequestKeyframe();
            }
            if (CaptureStatus.Running && awaitingStart && BasisScreenShareClient.Available)
            {
                info = CurrentInfo();
                if (BasisScreenShareClient.SendStart(info, CurrentPose()))
                {
                    awaitingStart = false;
                    State = ShareState.Waiting;
                    sentPosition = transform.position;
                    sentRotation = transform.rotation;
                    sentScale = transform.localScale.x;
                }
            }
            Raise();
        }
        UpdatePreview();
        if (directChanged)
        {
            directChanged = false;
            Raise();
        }
        if (!accepted) return;
        FollowPlacement(null, false);
        if (PoseDue(now)) BasisScreenShareClient.SendPose(CurrentPose());
    }

    StreamInfo CurrentInfo() => new StreamInfo { Width = (ushort)CaptureStatus.width, Height = (ushort)CaptureStatus.height, Fps = (byte)BasisScreenShareSettings.Fps(BasisScreenShareSettings.FrameRate.RawValue), HasAudio = CaptureStatus.audio, Title = SourceName ?? string.Empty };

    void UpdatePreview()
    {
        bool want = CaptureStatus != null && CaptureStatus.Running && Player != null && !BasisScreenShareNative.IsLinux && Near();
        if (want && !previewOpen) previewOpen = Player.LoadPushStream("local");
        else if (!want && previewOpen)
        {
            Player.Stop();
            previewOpen = false;
        }
        ulong handle = previewOpen ? Player.PushHandle : 0;
        if (handle == attachedPreview && capture == attachedCapture) return;
        if (capture == 0 || !BasisScreenShareNative.PluginCurrent) return;
        BasisScreenShareNative.bm_capture_set_preview(capture, handle);
        attachedPreview = handle;
        attachedCapture = capture;
        if (handle != 0) RequestKeyframe();
    }

    bool Near() => (BasisLocalCameraDriver.Position - transform.position).sqrMagnitude <= Sq(BasisScreenShareSettings.ViewDistance.RawValue);

    void SetPushHandle(BasisScreenShareClient.Remote remote, ulong handle)
    {
        long value = (long)handle;
        if (Interlocked.Read(ref remote.PushHandle) == value) return;
        Interlocked.Exchange(ref remote.PushHandle, value);
        if (value != 0) remote.NeedKeyframe = true;
    }

    void UpdateRemote()
    {
        BasisScreenShareClient.Remote remote = Remote;
        if (remote == null || remote.Ended) return;
        FollowPlacement(remote, false);
        if (PoseDue(Time.unscaledTime)) BasisScreenShareClient.SendMove(remote.Sharer, CurrentPose());
        bool want = BasisScreenShareSettings.Receive.RawValue && Near();
        int route = !want ? BasisScreenShareClient.RouteNone : BasisScreenShareClient.HasDirectLink(remote.Sharer) ? BasisScreenShareClient.RouteDirect : BasisScreenShareClient.RouteServer;
        if (route != remote.Route)
        {
            BasisScreenShareClient.SetRoute(remote, route);
            if (!want)
            {
                SetPushHandle(remote, 0);
                if (Player != null) Player.Stop();
                previewOpen = false;
            }
            nextKeyRequest = 0f;
            Raise();
        }
        if (remote.InfoVersion != infoVersion)
        {
            infoVersion = remote.InfoVersion;
            StreamInfo next = remote.Info;
            if (next.Width != info.Width || next.Height != info.Height || next.HasAudio != info.HasAudio)
            {
                SetPushHandle(remote, 0);
                if (previewOpen && Player != null) Player.Stop();
                previewOpen = false;
            }
            info = next;
            remote.NeedKeyframe = true;
        }
        if (remote.ReopenRequested)
        {
            remote.ReopenRequested = false;
            SetPushHandle(remote, 0);
            if (previewOpen && Player != null) Player.Stop();
            previewOpen = false;
        }
        if (!want || Player == null) return;
        if (Audio != null)
        {
            Audio.VolumeGain = controls != null && controls.Volume >= 0f ? controls.Volume : Mathf.Clamp(BasisScreenShareSettings.Volume.RawValue, 0f, 1f);
            Audio.Mute = controls != null && controls.Muted;
        }
        if (!previewOpen) previewOpen = Player.LoadPushStream("share/" + remote.Sharer);
        SetPushHandle(remote, previewOpen ? Player.PushHandle : 0);
        if (remote.NeedKeyframe && Time.unscaledTime >= nextKeyRequest)
        {
            nextKeyRequest = Time.unscaledTime + KeyRequestInterval;
            BasisScreenShareClient.RequestKeyframe(remote);
        }
    }

    static float Sq(float v) => v * v;

    void OnDestroy()
    {
        if (IsRemote)
        {
            if (Remote != null) SetPushHandle(Remote, 0);
            if (Remote != null && !Remote.Ended) BasisScreenShareClient.SetRoute(Remote, BasisScreenShareClient.RouteNone);
            if (Remote != null && Remote.Screen == this) Remote.Screen = null;
            return;
        }
        BasisScreenShareSettings.Resolution.OnChanged -= OnTextSettingChanged;
        BasisScreenShareSettings.FrameRate.OnChanged -= OnTextSettingChanged;
        BasisScreenShareSettings.Quality.OnChanged -= OnTextSettingChanged;
        BasisScreenShareSettings.AudioSource.OnChanged -= OnTextSettingChanged;
        BasisScreenShareSettings.ShareAudio.OnChanged -= OnToggleSettingChanged;
        BasisScreenShareSettings.ShowCursor.OnChanged -= OnToggleSettingChanged;
        StopCapture(true);
        sender?.Dispose();
        sender = null;
        BasisScreenShareClient.UnregisterLocal(this);
    }
}
