using System.Collections.Generic;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Debug representations a handheld camera can draw into the world.
/// </summary>
[System.Flags]
public enum BasisCameraGizmoLayers
{
    None = 0,
    Frustum = 1 << 0,
    Follow = 1 << 1,
    DepthOfField = 1 << 2,
    PinState = 1 << 3,
    Readouts = 1 << 4,
}

/// <summary>
/// World-space debug visualisation of one handheld camera: the capture frustum and its optics,
/// the auto-follow rig's solve, the depth-of-field slab, and which pin space / modes are live.
/// <para>
/// Drawn through <see cref="BasisGizmoManager"/>, so it costs a batched instanced draw rather
/// than GameObjects. These are world geometry on <see cref="BasisGizmoManager.RenderLayer"/>
/// (Default), so every camera that renders that layer sees them — including this camera's own
/// capture. Turn the layers off before shooting.
/// </para>
/// <para>
/// Everything drawn is read back from the camera's own state on the frame it was computed; the
/// follow rig hands over its actual intermediates (see
/// <c>BasisHandHeldCameraInteractable.TryGetFollowGizmoSample</c>) rather than being re-derived
/// here, so the picture cannot drift away from the solve it is documenting.
/// </para>
/// </summary>
public sealed class BasisHandHeldCameraGizmos
{
    private const float FrustumDrawDepth = 3.5f;
    private const float MaxPlaneDrawDistance = 40f;
    private const float LineWidth = 0.006f;
    private const float ThinLineWidth = 0.003f;
    private const float NodeSize = 0.035f;
    private const float SmallNodeSize = 0.022f;
    private const float LabelBaseScale = 0.014f;
    private const int RingSegments = 40;

    private static readonly Color FrustumColor = new Color(0.35f, 0.85f, 1f, 1f);
    private static readonly Color FrustumFarColor = new Color(0.2f, 0.5f, 0.7f, 1f);
    private static readonly Color LevelGuideColor = new Color(1f, 1f, 1f, 0.6f);
    private static readonly Color FollowColor = new Color(0.4f, 1f, 0.55f, 1f);
    private static readonly Color FollowPreviewColor = new Color(0.4f, 1f, 0.55f, 0.35f);
    private static readonly Color FocusColor = new Color(1f, 0.78f, 0.28f, 1f);
    private static readonly Color FocusLimitColor = new Color(0.75f, 0.5f, 0.15f, 1f);
    private static readonly Color AxisXColor = new Color(1f, 0.35f, 0.35f, 1f);
    private static readonly Color AxisYColor = new Color(0.45f, 1f, 0.45f, 1f);
    private static readonly Color AxisZColor = new Color(0.45f, 0.6f, 1f, 1f);
    private static readonly Color GhostColor = new Color(1f, 1f, 1f, 0.25f);

    private static readonly Color HandHeldColor = new Color(0.6f, 0.9f, 0.6f, 1f);
    private static readonly Color PlaySpaceColor = new Color(1f, 0.85f, 0.4f, 1f);
    private static readonly Color WorldSpaceColor = new Color(0.9f, 0.5f, 1f, 1f);

    private static readonly Rect FullViewport = new Rect(0f, 0f, 1f, 1f);

    private readonly GizmoSet _frustum = new GizmoSet("CameraFrustum");
    private readonly GizmoSet _follow = new GizmoSet("CameraFollow");
    private readonly GizmoSet _depth = new GizmoSet("CameraDepth");
    private readonly GizmoSet _pin = new GizmoSet("CameraPin");

    private readonly Vector3[] _cornerScratch = new Vector3[4];
    private readonly Vector3[] _nearQuad = new Vector3[4];
    private readonly Vector3[] _farQuad = new Vector3[4];
    private readonly Vector3[] _planeQuad = new Vector3[4];
    private readonly Vector3[] _ring = new Vector3[RingSegments];
    private readonly StringBuilder _builder = new StringBuilder(220);

    private string _opticsText;
    private int _opticsKey;
    private string _followText;
    private int _followKey;
    private string _depthText;
    private int _depthKey;
    private string _pinText;
    private int _pinKey;

    private bool _hooked;

    /// <summary>Which representations are drawn. Set from the camera settings panel.</summary>
    public BasisCameraGizmoLayers Layers { get; private set; } = BasisCameraGizmoLayers.None;

    public bool AnyLayerActive => Layers != BasisCameraGizmoLayers.None;

    public bool IsLayerEnabled(BasisCameraGizmoLayers layer) => (Layers & layer) != 0;

    public void SetLayerEnabled(BasisCameraGizmoLayers layer, bool enabled)
    {
        BasisCameraGizmoLayers next = enabled ? Layers | layer : Layers & ~layer;
        if (next == Layers)
        {
            return;
        }
        Layers = next;

        if (!enabled)
        {
            ClearLayer(layer);
        }
    }

    public void Tick(BasisHandHeldCamera camera)
    {
        if (camera == null)
        {
            return;
        }

        camera.CaptureFollowGizmoSample = IsLayerEnabled(BasisCameraGizmoLayers.Follow);

        if (Layers == BasisCameraGizmoLayers.None || camera.captureCamera == null)
        {
            return;
        }

        EnsureMasterHook();

        float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        if (scale <= 0f)
        {
            scale = 1f;
        }
        Vector3 viewer = BasisLocalCameraDriver.Position;
        bool readouts = IsLayerEnabled(BasisCameraGizmoLayers.Readouts);

        if (IsLayerEnabled(BasisCameraGizmoLayers.Frustum))
        {
            DrawFrustum(camera, scale, viewer, readouts);
        }
        if (IsLayerEnabled(BasisCameraGizmoLayers.DepthOfField))
        {
            DrawDepthOfField(camera, scale, viewer, readouts);
        }
        if (IsLayerEnabled(BasisCameraGizmoLayers.Follow))
        {
            DrawFollow(camera, scale, viewer, readouts);
        }
        if (IsLayerEnabled(BasisCameraGizmoLayers.PinState))
        {
            DrawPinState(camera, scale, viewer, readouts);
        }
    }

    public void Shutdown()
    {
        _frustum.Clear();
        _follow.Clear();
        _depth.Clear();
        _pin.Clear();
        _opticsText = null;
        _followText = null;
        _depthText = null;
        _pinText = null;

        if (_hooked)
        {
            BasisGizmoManager.OnUseGizmosChanged -= OnMasterGizmoToggle;
            _hooked = false;
        }
    }

    private void ClearLayer(BasisCameraGizmoLayers layer)
    {
        switch (layer)
        {
            case BasisCameraGizmoLayers.Frustum:
                _frustum.Clear();
                _opticsText = null;
                break;
            case BasisCameraGizmoLayers.Follow:
                _follow.Clear();
                _followText = null;
                break;
            case BasisCameraGizmoLayers.DepthOfField:
                _depth.Clear();
                _depthText = null;
                break;
            case BasisCameraGizmoLayers.PinState:
                _pin.Clear();
                _pinText = null;
                break;
            case BasisCameraGizmoLayers.Readouts:
                _frustum.ClearLabels();
                _follow.ClearLabels();
                _depth.ClearLabels();
                _pin.ClearLabels();
                break;
        }
    }

    private void EnsureMasterHook()
    {
        if (_hooked)
        {
            return;
        }
        BasisGizmoManager.OnUseGizmosChanged += OnMasterGizmoToggle;
        _hooked = true;
    }

    private void OnMasterGizmoToggle(bool state)
    {
        if (state)
        {
            return;
        }
        _frustum.Forget();
        _follow.Forget();
        _depth.Forget();
        _pin.Forget();
    }

    private void DrawFrustum(BasisHandHeldCamera camera, float scale, Vector3 viewer, bool readouts)
    {
        Camera cam = camera.captureCamera;
        Transform t = cam.transform;
        _frustum.Begin();

        float depth = Mathf.Clamp(FrustumDrawDepth * scale, cam.nearClipPlane * 2f, cam.farClipPlane);
        BuildQuad(cam, t, cam.nearClipPlane, _nearQuad);
        BuildQuad(cam, t, depth, _farQuad);

        _frustum.Poly(_nearQuad, FrustumColor, true, LineWidth);
        _frustum.Poly(_farQuad, FrustumFarColor, true, LineWidth);
        for (int Index = 0; Index < 4; Index++)
        {
            _frustum.Line(_nearQuad[Index], _farQuad[Index], FrustumColor, ThinLineWidth);
        }

        t.GetPositionAndRotation(out Vector3 origin, out Quaternion rotation);
        Vector3 farCentre = Centre(_farQuad);
        _frustum.Line(origin, farCentre, FrustumColor, ThinLineWidth);

        Vector3 flatForward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        if (flatForward.sqrMagnitude > 1e-6f)
        {
            Vector3 levelRight = Vector3.Cross(Vector3.up, flatForward.normalized);
            float halfWidth = Vector3.Distance(_farQuad[1], _farQuad[2]) * 0.5f;
            Vector3 levelCentre = new Vector3(farCentre.x, origin.y, farCentre.z);
            _frustum.Line(levelCentre - levelRight * halfWidth, levelCentre + levelRight * halfWidth, LevelGuideColor, ThinLineWidth);
        }

        if (readouts)
        {
            float vertical = cam.fieldOfView;
            float horizontal = 2f * Mathf.Atan(Mathf.Tan(vertical * 0.5f * Mathf.Deg2Rad) * cam.aspect) * Mathf.Rad2Deg;
            float derivedFocal = cam.sensorSize.y * 0.5f / Mathf.Tan(vertical * 0.5f * Mathf.Deg2Rad);

            int key = Quantize(vertical, 10f) ^ (Quantize(horizontal, 10f) << 8) ^ (Quantize(cam.focalLength, 10f) << 15) ^
                      (Quantize(cam.aspect, 100f) << 22);
            if (_opticsText == null || key != _opticsKey)
            {
                _opticsKey = key;
                _opticsText = BuildOpticsText(cam, vertical, horizontal, derivedFocal);
            }
            _frustum.Label(farCentre + Vector3.up * (0.18f * scale), _opticsText, FrustumColor, viewer, scale);
        }

        _frustum.End();
    }

    private string BuildOpticsText(Camera cam, float vertical, float horizontal, float derivedFocal)
    {
        _builder.Clear();
        _builder.Append("FOV  ").Append(vertical.ToString("0.0")).Append("° v   ")
            .Append(horizontal.ToString("0.0")).Append("° h\n");
        _builder.Append("aspect ").Append(cam.aspect.ToString("0.000"));
        if (cam.targetTexture != null)
        {
            _builder.Append("  (").Append(cam.targetTexture.width).Append('x').Append(cam.targetTexture.height).Append(')');
        }
        _builder.Append('\n');
        _builder.Append("sensor ").Append(cam.sensorSize.x.ToString("0.#")).Append('x')
            .Append(cam.sensorSize.y.ToString("0.#")).Append("mm  gate ").Append(cam.gateFit).Append('\n');
        _builder.Append("focal ").Append(cam.focalLength.ToString("0.0")).Append("mm");
        if (Mathf.Abs(cam.focalLength - derivedFocal) > 0.5f)
        {
            _builder.Append("  ≠ FOV-derived ").Append(derivedFocal.ToString("0.0")).Append("mm");
        }
        _builder.Append('\n');
        _builder.Append("clip ").Append(cam.nearClipPlane.ToString("0.###")).Append(" – ")
            .Append(cam.farClipPlane.ToString("0")).Append('m');
        return _builder.ToString();
    }

    private void DrawDepthOfField(BasisHandHeldCamera camera, float scale, Vector3 viewer, bool readouts)
    {
        DepthOfField dof = camera.MetaData.depthOfField;
        Camera cam = camera.captureCamera;
        _depth.Begin();

        if (dof == null || !dof.active || dof.mode.value == DepthOfFieldMode.Off)
        {
            _depth.End();
            return;
        }

        Transform t = cam.transform;
        t.GetPositionAndRotation(out Vector3 origin, out Quaternion rotation);
        Vector3 forward = rotation * Vector3.forward;

        float focus = Mathf.Max(dof.focusDistance.value, cam.nearClipPlane);
        BuildQuad(cam, t, Mathf.Min(focus, cam.farClipPlane), _planeQuad);
        Vector3 focusCentre = Centre(_planeQuad);
        _depth.Poly(_planeQuad, FocusColor, true, LineWidth);
        _depth.Sphere(origin + forward * focus, SmallNodeSize * scale, FocusColor);

        bool bokeh = dof.mode.value == DepthOfFieldMode.Bokeh;
        float near = 0f;
        float far = 0f;
        float hyperfocal = 0f;
        float circleOfConfusion = 0f;

        if (bokeh)
        {
            circleOfConfusion = cam.sensorSize.magnitude / 1500f;
            ComputeDepthOfField(dof.focalLength.value, dof.aperture.value, focus, circleOfConfusion,
                out near, out far, out hyperfocal);

            float nearDraw = Mathf.Clamp(near, cam.nearClipPlane, cam.farClipPlane);
            BuildQuad(cam, t, nearDraw, _planeQuad);
            _depth.Poly(_planeQuad, FocusLimitColor, true, ThinLineWidth);

            float farDraw = Mathf.Clamp(float.IsInfinity(far) ? MaxPlaneDrawDistance : far,
                cam.nearClipPlane, Mathf.Min(cam.farClipPlane, MaxPlaneDrawDistance));
            BuildQuad(cam, t, farDraw, _planeQuad);
            _depth.Poly(_planeQuad, FocusLimitColor, true, ThinLineWidth);

            _depth.Line(origin + forward * nearDraw, origin + forward * farDraw, FocusColor, LineWidth);
        }

        if (camera.autoFocusFollowSubject && camera.TryGetFollowFocusPoint(out Vector3 subject))
        {
            _depth.Line(origin, subject, FocusColor, ThinLineWidth);
            _depth.Sphere(subject, SmallNodeSize * scale, FocusColor);
        }

        if (readouts)
        {
            int key = Quantize(focus, 20f) ^ (Quantize(dof.aperture.value, 10f) << 9) ^
                      (Quantize(dof.focalLength.value, 1f) << 17) ^ ((int)dof.mode.value << 25);
            if (_depthText == null || key != _depthKey)
            {
                _depthKey = key;
                _depthText = BuildDepthText(camera, dof, cam, focus, bokeh, near, far, hyperfocal, circleOfConfusion);
            }
            _depth.Label(focusCentre + Vector3.up * (0.14f * scale), _depthText, FocusColor, viewer, scale);
        }

        _depth.End();
    }

    private string BuildDepthText(BasisHandHeldCamera camera, DepthOfField dof, Camera cam, float focus,
        bool bokeh, float near, float far, float hyperfocal, float circleOfConfusion)
    {
        _builder.Clear();
        _builder.Append("DoF ").Append(dof.mode.value).Append(camera.autoFocusFollowSubject ? "  auto\n" : "\n");
        _builder.Append("focus ").Append(focus.ToString("0.00")).Append('m');
        if (!bokeh)
        {
            return _builder.ToString();
        }

        _builder.Append("   f/").Append(dof.aperture.value.ToString("0.0")).Append('\n');
        _builder.Append("blur focal ").Append(dof.focalLength.value.ToString("0")).Append("mm");
        if (Mathf.Abs(dof.focalLength.value - cam.focalLength) > 1f)
        {
            _builder.Append("  (lens ").Append(cam.focalLength.ToString("0")).Append("mm)");
        }
        _builder.Append('\n');
        _builder.Append("sharp ").Append(near.ToString("0.00")).Append(" – ")
            .Append(float.IsInfinity(far) ? "∞" : far.ToString("0.00")).Append('m');
        if (!float.IsInfinity(far))
        {
            _builder.Append("   depth ").Append((far - near).ToString("0.00")).Append('m');
        }
        _builder.Append('\n');
        _builder.Append("hyperfocal ").Append(hyperfocal.ToString("0.00")).Append("m   CoC ")
            .Append(circleOfConfusion.ToString("0.000")).Append("mm");
        return _builder.ToString();
    }

    /// <summary>
    /// Thin-lens depth of field. <paramref name="focalLength"/> and
    /// <paramref name="circleOfConfusion"/> are millimetres, <paramref name="focusDistance"/> is
    /// metres, and the limits come back in metres. Focus at or past the hyperfocal distance puts
    /// the far limit at infinity, which is reported as <see cref="float.PositiveInfinity"/>.
    /// </summary>
    public static void ComputeDepthOfField(float focalLength, float aperture, float focusDistance,
        float circleOfConfusion, out float near, out float far, out float hyperfocal)
    {
        near = 0f;
        far = float.PositiveInfinity;
        hyperfocal = float.PositiveInfinity;

        if (focalLength <= 0f || aperture <= 0f || circleOfConfusion <= 0f || focusDistance <= 0f)
        {
            return;
        }

        float hyperfocalMm = focalLength * focalLength / (aperture * circleOfConfusion) + focalLength;
        hyperfocal = hyperfocalMm / 1000f;

        float focusMm = focusDistance * 1000f;
        float numerator = focusMm * (hyperfocalMm - focalLength);

        near = numerator / (hyperfocalMm + focusMm - 2f * focalLength) / 1000f;
        far = focusMm >= hyperfocalMm ? float.PositiveInfinity : numerator / (hyperfocalMm - focusMm) / 1000f;
    }

    private void DrawFollow(BasisHandHeldCamera camera, float scale, Vector3 viewer, bool readouts)
    {
        _follow.Begin();

        if (!camera.TryGetFollowGizmoSample(out BasisHandHeldCameraInteractable.FollowGizmoSample sample))
        {
            _follow.End();
            return;
        }

        Color tint = sample.Live ? FollowColor : FollowPreviewColor;
        float nodeSize = NodeSize * sample.Scale;

        _follow.Sphere(sample.GroundPos, SmallNodeSize * sample.Scale, tint);
        _follow.Sphere(sample.AnchorPos, nodeSize, tint);
        _follow.Line(sample.GroundPos, sample.AnchorPos, tint, ThinLineWidth);
        _follow.Sphere(sample.LookPoint, nodeSize, tint);

        Vector3 applied = sample.AppliedOffset * sample.Scale;
        Vector3 legX = sample.AnchorPos + sample.Yaw * new Vector3(applied.x, 0f, 0f);
        Vector3 legY = legX + sample.Yaw * new Vector3(0f, applied.y, 0f);
        _follow.Line(sample.AnchorPos, legX, AxisXColor, LineWidth);
        _follow.Line(legX, legY, AxisYColor, LineWidth);
        _follow.Line(legY, sample.TargetPosition, AxisZColor, LineWidth);

        if (sample.CloseIn > 0.001f)
        {
            Vector3 authoredX = sample.AnchorPos + sample.Yaw * new Vector3(sample.AuthoredOffset.x * sample.Scale, 0f, 0f);
            _follow.Line(sample.AnchorPos, authoredX, GhostColor, ThinLineWidth);
            _follow.Sphere(authoredX, SmallNodeSize * sample.Scale, GhostColor);
        }

        _follow.Sphere(sample.TargetPosition, nodeSize, tint);
        _follow.Line(sample.TargetPosition, sample.LookPoint, tint, LineWidth);

        BuildRing(sample.TargetPosition, camera.autoFollowTeleportDistance * sample.Scale);
        _follow.Poly(_ring, sample.Snapped ? AxisXColor : GhostColor, true, ThinLineWidth);

        Vector3 live = camera.captureCamera.transform.position;
        if (Vector3.Distance(live, sample.TargetPosition) > 0.01f)
        {
            _follow.Line(live, sample.TargetPosition, GhostColor, ThinLineWidth);
        }

        if (readouts)
        {
            float aimDistance = Vector3.Distance(sample.TargetPosition, sample.LookPoint);
            float lag = Vector3.Distance(live, sample.TargetPosition);
            int key = Quantize(aimDistance, 20f) ^ (Quantize(lag, 50f) << 9) ^ (Quantize(sample.CloseIn, 20f) << 17) ^
                      (Quantize(sample.LateralSpeed, 10f) << 22) ^ (sample.Live ? 1 << 30 : 0);
            if (_followText == null || key != _followKey)
            {
                _followKey = key;
                _followText = BuildFollowText(camera, sample, aimDistance, lag);
            }
            _follow.Label(sample.TargetPosition + Vector3.up * (0.2f * sample.Scale), _followText, tint, viewer, scale);
        }

        _follow.End();
    }

    private string BuildFollowText(BasisHandHeldCamera camera, BasisHandHeldCameraInteractable.FollowGizmoSample sample,
        float aimDistance, float lag)
    {
        _builder.Clear();
        _builder.Append(sample.Live ? "Follow" : "Follow (preview)");
        _builder.Append(camera.IsFollowingRemotePlayer ? "  remote #" : "  local");
        if (camera.IsFollowingRemotePlayer)
        {
            _builder.Append(camera.followTargetPlayerId);
        }
        _builder.Append('\n');
        _builder.Append("offset ").Append(sample.AppliedOffset.x.ToString("0.00")).Append(", ")
            .Append(sample.AppliedOffset.y.ToString("0.00")).Append(", ")
            .Append(sample.AppliedOffset.z.ToString("0.00")).Append("  x").Append(sample.Scale.ToString("0.00")).Append('\n');
        if (sample.CloseIn > 0.001f)
        {
            _builder.Append("lateral ").Append(sample.LateralSpeed.ToString("0.00")).Append("m/s  close-in ")
                .Append((sample.CloseIn * 100f).ToString("0")).Append("%\n");
        }
        _builder.Append("aim ").Append(aimDistance.ToString("0.00")).Append("m   lag ")
            .Append(lag.ToString("0.00")).Append("m\n");
        _builder.Append("half-life ").Append(HalfLife(camera.autoFollowPositionSmoothing).ToString("0.00")).Append("s pos  ")
            .Append(HalfLife(camera.autoFollowRotationSmoothing).ToString("0.00")).Append("s rot");
        if (sample.Snapped)
        {
            _builder.Append("\nSNAP — past ").Append((camera.autoFollowTeleportDistance * sample.Scale).ToString("0.0")).Append('m');
        }
        return _builder.ToString();
    }

    private static float HalfLife(float rate) => rate > 1e-4f ? Mathf.Log(2f) / rate : 0f;

    private void DrawPinState(BasisHandHeldCamera camera, float scale, Vector3 viewer, bool readouts)
    {
        _pin.Begin();

        camera.captureCamera.transform.GetPositionAndRotation(out Vector3 camPos, out Quaternion camRot);
        Color tint = PinColor(camera.PinSpace);
        _pin.Sphere(camPos, NodeSize * scale, tint);

        bool hasSource = TryGetPinSource(camera, out Vector3 sourcePos, out Quaternion sourceRot);
        if (hasSource)
        {
            _pin.Sphere(sourcePos, SmallNodeSize * scale, tint);
            _pin.Line(sourcePos, camPos, tint, ThinLineWidth);

            float axis = 0.25f * scale;
            _pin.Line(sourcePos, sourcePos + sourceRot * Vector3.right * axis, AxisXColor, ThinLineWidth);
            _pin.Line(sourcePos, sourcePos + sourceRot * Vector3.up * axis, AxisYColor, ThinLineWidth);
            _pin.Line(sourcePos, sourcePos + sourceRot * Vector3.forward * axis, AxisZColor, ThinLineWidth);
        }

        if (BasisLocalPlayer.Instance != null)
        {
            float floor = BasisLocalPlayer.Instance.transform.position.y;
            Vector3 ground = new Vector3(camPos.x, floor, camPos.z);
            _pin.Line(camPos, ground, tint, ThinLineWidth);
            BuildRing(ground, 0.15f * scale);
            _pin.Poly(_ring, tint, true, ThinLineWidth);
        }

        if (readouts)
        {
            camera.GetPinnedTargetPose(out Vector3 pinned, out Quaternion _);
            float height = BasisLocalPlayer.Instance != null
                ? camPos.y - BasisLocalPlayer.Instance.transform.position.y
                : camPos.y;

            int key = (int)camera.PinSpace ^ (camera.IsFlying ? 1 << 4 : 0) ^ (camera.IsAutoFollowing ? 1 << 5 : 0) ^
                      (camera.HandHeld.IsSelfieMode ? 1 << 6 : 0) ^ (camera.enableRecordingView ? 1 << 7 : 0) ^
                      (camera.IsVideoOutputActive ? 1 << 8 : 0) ^ (camera.autoFocusFollowSubject ? 1 << 9 : 0) ^
                      (camera.useAutoLeveling ? 1 << 10 : 0) ^ (camera.useVRHandheldSmoothing ? 1 << 11 : 0) ^
                      (Quantize(height, 20f) << 12);
            if (_pinText == null || key != _pinKey)
            {
                _pinKey = key;
                _pinText = BuildPinText(camera, height, pinned);
            }
            _pin.Label(camPos + Vector3.up * (0.22f * scale), _pinText, tint, viewer, scale);
        }

        _pin.End();
    }

    private string BuildPinText(BasisHandHeldCamera camera, float height, Vector3 pinned)
    {
        _builder.Clear();
        _builder.Append("Pin ").Append(camera.PinSpace).Append('\n');
        _builder.Append("height ").Append(height.ToString("0.00")).Append("m over floor\n");
        if (camera.PinSpace != BasisHandHeldCameraInteractable.CameraPinSpace.HandHeld)
        {
            _builder.Append("target ").Append(pinned.x.ToString("0.0")).Append(", ")
                .Append(pinned.y.ToString("0.0")).Append(", ").Append(pinned.z.ToString("0.0")).Append('\n');
        }

        int written = 0;
        AppendMode(ref written, camera.IsFlying, "fly");
        AppendMode(ref written, camera.IsAutoFollowing, "follow");
        AppendMode(ref written, camera.autoFocusFollowSubject, "autofocus");
        AppendMode(ref written, camera.HandHeld.IsSelfieMode, "selfie");
        AppendMode(ref written, camera.enableRecordingView, "recording");
        AppendMode(ref written, camera.IsVideoOutputActive, "streaming");
        AppendMode(ref written, camera.useAutoLeveling, "auto-level");
        AppendMode(ref written, camera.useVRHandheldSmoothing, "vr-stab");
        if (written == 0)
        {
            _builder.Append("no modes active");
        }
        return _builder.ToString();
    }

    private void AppendMode(ref int written, bool active, string label)
    {
        if (!active)
        {
            return;
        }
        if (written > 0)
        {
            _builder.Append("  ");
        }
        _builder.Append(label);
        written++;
    }

    private static bool TryGetPinSource(BasisHandHeldCamera camera, out Vector3 position, out Quaternion rotation)
    {
        switch (camera.PinSpace)
        {
            case BasisHandHeldCameraInteractable.CameraPinSpace.HandHeld:
                camera.transform.GetPositionAndRotation(out position, out rotation);
                return true;

            case BasisHandHeldCameraInteractable.CameraPinSpace.PlaySpace:
                if (BasisLocalPlayer.Instance == null)
                {
                    break;
                }
                BasisLocalPlayer.Instance.transform.GetPositionAndRotation(out position, out rotation);
                return true;
        }

        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }

    private static Color PinColor(BasisHandHeldCameraInteractable.CameraPinSpace space)
    {
        switch (space)
        {
            case BasisHandHeldCameraInteractable.CameraPinSpace.PlaySpace: return PlaySpaceColor;
            case BasisHandHeldCameraInteractable.CameraPinSpace.WorldSpace: return WorldSpaceColor;
            default: return HandHeldColor;
        }
    }

    private void BuildQuad(Camera cam, Transform t, float distance, Vector3[] into)
    {
        cam.CalculateFrustumCorners(FullViewport, distance, Camera.MonoOrStereoscopicEye.Mono, _cornerScratch);
        for (int Index = 0; Index < 4; Index++)
        {
            into[Index] = t.TransformPoint(_cornerScratch[Index]);
        }
    }

    private void BuildRing(Vector3 centre, float radius)
    {
        for (int Index = 0; Index < RingSegments; Index++)
        {
            float angle = Index / (float)RingSegments * Mathf.PI * 2f;
            _ring[Index] = new Vector3(centre.x + Mathf.Cos(angle) * radius, centre.y, centre.z + Mathf.Sin(angle) * radius);
        }
    }

    private static Vector3 Centre(Vector3[] quad) => (quad[0] + quad[1] + quad[2] + quad[3]) * 0.25f;

    private static int Quantize(float value, float steps) => Mathf.RoundToInt(value * steps);

    /// <summary>
    /// One layer's gizmo handles. Slots are handed out by draw order each frame and reused, so a
    /// layer whose element count changes between frames parks the tail rather than leaking it.
    /// </summary>
    private sealed class GizmoSet
    {
        private readonly string _name;
        private readonly List<int> _spheres = new List<int>();
        private readonly List<int> _lines = new List<int>();
        private readonly List<int> _labels = new List<int>();
        private int _sphereCursor;
        private int _lineCursor;
        private int _labelCursor;

        public GizmoSet(string name)
        {
            _name = name;
        }

        public void Begin()
        {
            _sphereCursor = 0;
            _lineCursor = 0;
            _labelCursor = 0;
        }

        public void Sphere(Vector3 position, float size, Color color)
        {
            if (_sphereCursor == _spheres.Count)
            {
                BasisGizmoManager.CreateSphereGizmo(_name, out int created, position, size, color);
                _spheres.Add(created);
                _sphereCursor++;
                return;
            }

            int id = _spheres[_sphereCursor++];
            BasisGizmoManager.SetGizmoActive(id, true);
            BasisGizmoManager.UpdateSphereGizmo(id, position, Vector3.one * size);
            BasisGizmoManager.UpdateGizmoColor(id, color);
        }

        public void Line(Vector3 start, Vector3 end, Color color, float width)
        {
            if (_lineCursor == _lines.Count)
            {
                BasisGizmoManager.CreateLineGizmo(_name, out int created, start, end, width, color);
                _lines.Add(created);
                _lineCursor++;
                return;
            }

            int id = _lines[_lineCursor++];
            BasisGizmoManager.SetGizmoActive(id, true);
            BasisGizmoManager.UpdateLineGizmo(id, start, end);
            BasisGizmoManager.UpdateGizmoColor(id, color);
        }

        public void Poly(Vector3[] points, Color color, bool loop, float width)
        {
            if (_lineCursor == _lines.Count)
            {
                BasisGizmoManager.CreateLineGizmo(_name, out int created, points, width, color, loop);
                _lines.Add(created);
                _lineCursor++;
                return;
            }

            int id = _lines[_lineCursor++];
            BasisGizmoManager.SetGizmoActive(id, true);
            BasisGizmoManager.UpdateLineGizmo(id, points);
            BasisGizmoManager.UpdateGizmoColor(id, color);
        }

        public void Label(Vector3 position, string text, Color color, Vector3 viewer, float scale)
        {
            int id;
            if (_labelCursor == _labels.Count)
            {
                BasisGizmoManager.CreateTextGizmo(_name, out id, position, text, color);
                _labels.Add(id);
                _labelCursor++;
            }
            else
            {
                id = _labels[_labelCursor++];
                BasisGizmoManager.SetGizmoActive(id, true);
            }
            BasisGizmoManager.UpdateTextGizmo(id, position, BasisGizmoManager.BillboardRotation(position, viewer),
                LabelBaseScale * scale, text, color);
        }

        public void End()
        {
            for (int Index = _sphereCursor; Index < _spheres.Count; Index++)
            {
                BasisGizmoManager.SetGizmoActive(_spheres[Index], false);
            }
            for (int Index = _lineCursor; Index < _lines.Count; Index++)
            {
                BasisGizmoManager.SetGizmoActive(_lines[Index], false);
            }
            for (int Index = _labelCursor; Index < _labels.Count; Index++)
            {
                BasisGizmoManager.SetGizmoActive(_labels[Index], false);
            }
        }

        public void ClearLabels()
        {
            for (int Index = 0; Index < _labels.Count; Index++)
            {
                BasisGizmoManager.DestroyGizmo(_labels[Index]);
            }
            _labels.Clear();
            _labelCursor = 0;
        }

        public void Clear()
        {
            for (int Index = 0; Index < _spheres.Count; Index++)
            {
                BasisGizmoManager.DestroyGizmo(_spheres[Index]);
            }
            for (int Index = 0; Index < _lines.Count; Index++)
            {
                BasisGizmoManager.DestroyGizmo(_lines[Index]);
            }
            for (int Index = 0; Index < _labels.Count; Index++)
            {
                BasisGizmoManager.DestroyGizmo(_labels[Index]);
            }
            Forget();
        }

        /// <summary>
        /// Drops the cached ids without destroying them — the master gizmo gate already wiped
        /// every slot, so destroying again would only log warnings for ids that no longer exist.
        /// </summary>
        public void Forget()
        {
            _spheres.Clear();
            _lines.Clear();
            _labels.Clear();
            Begin();
        }
    }
}
