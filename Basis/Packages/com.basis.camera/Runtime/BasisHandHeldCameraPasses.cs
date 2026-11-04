using Basis;
using Basis.BasisUI;
using System;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera
{
    private const string MissingPassShader = "Render pass shaders could not be loaded, so the camera keeps showing the picture.";
    private static readonly int ComposeDepthId = Shader.PropertyToID("_BasisPassDepthTex"), ComposeNormalsId = Shader.PropertyToID("_BasisPassNormalsTex"), ComposeMaskId = Shader.PropertyToID("_BasisPassMaskTex");
    [NoAutoStaticsCleanup] private static readonly string[] PassTargetNames = Array.ConvertAll(BasisCameraPasses.Suffixes, suffix => "BasisCameraPass" + suffix);
    private readonly RenderTexture[] passTextures = new RenderTexture[BasisCameraPasses.Count], capturePassTextures = new RenderTexture[BasisCameraPasses.Count];
    private readonly IBasisVideoOutputSink[] passSinks = new IBasisVideoOutputSink[BasisCameraPasses.Count];
    private readonly BasisWebVideoOutputSink[] passWebSinks = new BasisWebVideoOutputSink[BasisCameraPasses.Count];
    private readonly RenderTexture[] passStreamTextures = new RenderTexture[BasisCameraPasses.Count], passWebTextures = new RenderTexture[BasisCameraPasses.Count];
    private readonly BasisStreamFramePacer[] passWebPacers = new BasisStreamFramePacer[BasisCameraPasses.Count];
    private readonly string[] passSenderNames = new string[BasisCameraPasses.Count], passOutputFailures = new string[BasisCameraPasses.Count];
    private readonly int[] passPushedFrames = new int[BasisCameraPasses.Count];
    private RenderTexture quadTexture, captureQuadTexture;
    private Material passMaterial, passMaskMaterial, passComposeMaterial;
    private bool passShaderMissing, passMaskShaderMissing, passComposeShaderMissing, quadReady, captureQuadReady;
    private bool passesEnqueued, passFrameRendered, passCaptureRender, passCaptureData;
    private int passFramesRendered;
    private BasisCameraPassSet passCaptureSet, passesEnqueuedSet, livePassesRendered, capturePassesRendered;
    private BasisCameraPassesPass renderPassesPass;
    public BasisCameraPassOutput PassOutput { get; private set; }
    public float PassDepthRange { get; private set; } = BasisCameraPasses.DefaultDepthRange;
    public bool SeparatePasses { get; private set; }
    public BasisCameraPassSet SeparatePassSet { get; private set; } = BasisCameraPasses.DefaultSeparateSet;
    public bool RenderPassesAvailable => LoadPassMaterials();
    public BasisCameraPassSet LivePassSet => BasisCameraPasses.For(PassOutput) | (SeparatePasses && IsAnyVideoOutputActive ? StreamingSeparatePassSet : BasisCameraPassSet.None);
    private BasisCameraPassSet StreamingSeparatePassSet
    {
        get
        {
            BasisCameraPassSet set = BasisCameraPassSet.None;
            for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
            {
                if (!string.IsNullOrEmpty(passOutputFailures[Index])) continue;
                IBasisVideoOutputSink sink = passSinks[Index];
                BasisWebVideoOutputSink web = passWebSinks[Index];
                bool platform = sink != null && sink.FailureMessage == null;
                bool watched = web != null && web.FailureMessage == null && web.HasClients;
                if (platform || watched) set |= BasisCameraPasses.Flag((BasisCameraPass)Index);
            }
            return set & SeparatePassSet;
        }
    }
    public BasisCameraPassSet CapturePassSet => BasisCameraPasses.For(PassOutput) | (SeparatePasses ? SeparatePassSet : BasisCameraPassSet.None);
    public RenderTexture OutputTexture
    {
        get
        {
            if (PassOutput == BasisCameraPassOutput.QuadView) return quadReady && quadTexture != null ? quadTexture : renderTexture;
            if (!BasisCameraPasses.TryGetPass(PassOutput, out BasisCameraPass pass) || !BasisCameraPasses.Contains(livePassesRendered, pass)) return renderTexture;
            RenderTexture texture = passTextures[(int)pass];
            return texture != null && (captureInFlight || BasisCameraPasses.SameSize(texture, renderTexture)) ? texture : renderTexture;
        }
    }
    private RenderTexture CaptureOutputTexture
    {
        get
        {
            if (PassOutput == BasisCameraPassOutput.QuadView) return captureQuadReady && captureQuadTexture != null ? captureQuadTexture : renderTexture;
            if (!BasisCameraPasses.TryGetPass(PassOutput, out BasisCameraPass pass) || !BasisCameraPasses.Contains(capturePassesRendered, pass)) return renderTexture;
            RenderTexture texture = capturePassTextures[(int)pass];
            return BasisCameraPasses.Matches(texture, renderTexture, passCaptureData) ? texture : renderTexture;
        }
    }
    public bool IsSeparatePass(BasisCameraPass pass) => BasisCameraPasses.Contains(SeparatePassSet, pass);
    public void SetPassOutput(BasisCameraPassOutput output)
    {
        output = BasisCameraPasses.SanitizeOutput((int)output);
        if (PassOutput == output) return;
        PassOutput = output;
        quadReady = false;
        BindViewfinderFeed();
    }
    public void SetPassDepthRange(float range) => PassDepthRange = BasisCameraPasses.SanitizeDepthRange(range);
    public void SetSeparatePasses(bool enabled)
    {
        if (SeparatePasses == enabled) return;
        SeparatePasses = enabled;
        ClearPassOutputFailures();
    }
    public void SetSeparatePass(BasisCameraPass pass, bool enabled) => SetSeparatePassSet(enabled ? SeparatePassSet | BasisCameraPasses.Flag(pass) : SeparatePassSet & ~BasisCameraPasses.Flag(pass));
    public void SetSeparatePassSet(BasisCameraPassSet set)
    {
        set &= BasisCameraPasses.AllSet;
        if (SeparatePassSet == set) return;
        SeparatePassSet = set;
        ClearPassOutputFailures();
    }
    public bool TryGetPassOutputName(BasisCameraPass pass, out string name)
    {
        int index = (int)pass;
        if (passSinks[index] != null && !string.IsNullOrEmpty(passSenderNames[index]))
        {
            name = passSenderNames[index];
            return true;
        }
        if (passWebSinks[index] != null)
        {
            name = passWebSinks[index].Url;
            return true;
        }
        name = null;
        return false;
    }
    public string DescribeSeparateOutputs()
    {
        if (!SeparatePasses) return string.Empty;
        if (SeparatePassSet == BasisCameraPassSet.None) return BasisLocalization.Get("camera.passes.status.none");
        if (!IsAnyVideoOutputActive) return BasisLocalization.Get("camera.passes.status.photos", BasisCameraPasses.Describe(SeparatePassSet));

        StringBuilder builder = new StringBuilder();
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            BasisCameraPass pass = (BasisCameraPass)Index;
            if (!IsSeparatePass(pass)) continue;
            if (builder.Length > 0) builder.Append('\n');
            string label = BasisLocalization.Get(BasisCameraPasses.PassKeys[Index]);
            if (!string.IsNullOrEmpty(passOutputFailures[Index])) builder.Append(BasisLocalization.Get("camera.passes.status.failed", label, "<noparse>" + passOutputFailures[Index] + "</noparse>"));
            else if (TryGetPassOutputName(pass, out string name)) builder.Append(BasisLocalization.Get("camera.passes.status.line", label, "<noparse>" + name + "</noparse>"));
            else builder.Append(BasisLocalization.Get("camera.passes.status.starting", label));
        }
        return builder.ToString();
    }
    private bool LoadPassMaterials()
    {
        BasisCameraRenderTargets.LoadOverlayMaterial(ref passMaterial, ref passShaderMissing, BasisCameraPasses.ShaderResource, MissingPassShader);
        BasisCameraRenderTargets.LoadOverlayMaterial(ref passMaskMaterial, ref passMaskShaderMissing, BasisCameraPasses.MaskShaderResource, MissingPassShader);
        BasisCameraRenderTargets.LoadOverlayMaterial(ref passComposeMaterial, ref passComposeShaderMissing, BasisCameraPasses.ComposeShaderResource, MissingPassShader);
        return passMaterial != null && passMaskMaterial != null && passComposeMaterial != null;
    }
    private void TickRenderPasses()
    {
        if (captureInFlight) return;

        BasisCameraPassSet rebuilt = EnsurePassTargets(passTextures, ref quadTexture, LivePassSet, false);
        livePassesRendered &= ~rebuilt;
        if ((rebuilt & BasisCameraPasses.QuadSet) != BasisCameraPassSet.None || quadTexture == null) quadReady = false;
        if (passFrameRendered)
        {
            passFrameRendered = false;
            if (PassOutput == BasisCameraPassOutput.QuadView) quadReady = ComposeQuad(passTextures, quadTexture, livePassesRendered, false);
        }
        BindViewfinderFeed();
    }
    private BasisCameraPassSet EnsurePassTargets(RenderTexture[] targets, ref RenderTexture quad, BasisCameraPassSet wanted, bool data)
    {
        if (wanted != BasisCameraPassSet.None && !LoadPassMaterials()) wanted = BasisCameraPassSet.None;

        BasisCameraPassSet rebuilt = BasisCameraPassSet.None;
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            BasisCameraPass pass = (BasisCameraPass)Index;
            if (!BasisCameraPasses.Contains(wanted, pass))
            {
                if (targets[Index] != null) rebuilt |= BasisCameraPasses.Flag(pass);
                BasisCameraRenderTargets.Release(ref targets[Index]);
            }
            else if (EnsurePassTarget(ref targets[Index], data, PassTargetNames[Index]))
            {
                rebuilt |= BasisCameraPasses.Flag(pass);
            }
        }

        if (PassOutput == BasisCameraPassOutput.QuadView && (wanted & BasisCameraPasses.QuadSet) == BasisCameraPasses.QuadSet)
        {
            if (EnsurePassTarget(ref quad, data, "BasisCameraQuadView")) rebuilt |= BasisCameraPasses.QuadSet;
        }
        else
        {
            BasisCameraRenderTargets.Release(ref quad);
        }
        return rebuilt;
    }
    private bool EnsurePassTarget(ref RenderTexture target, bool data, string name)
    {
        if (renderTexture == null)
        {
            bool released = target != null;
            BasisCameraRenderTargets.Release(ref target);
            return released;
        }
        if (BasisCameraPasses.Matches(target, renderTexture, data)) return false;

        BasisCameraRenderTargets.Release(ref target);
        target = BasisCameraRenderTargets.Create(renderTexture.width, renderTexture.height, BasisCameraPasses.TargetFormat(data), 0, 1, !data, name);
        return true;
    }
    private bool ComposeQuad(RenderTexture[] targets, RenderTexture quad, BasisCameraPassSet rendered, bool data)
    {
        if (quad == null || renderTexture == null || passComposeMaterial == null || (rendered & BasisCameraPasses.QuadSet) != BasisCameraPasses.QuadSet) return false;
        for (int Index = 0; Index < BasisCameraPasses.QuadPasses.Length; Index++)
        {
            if (!BasisCameraPasses.Matches(targets[(int)BasisCameraPasses.QuadPasses[Index]], renderTexture, data)) return false;
        }

        passComposeMaterial.SetTexture(ComposeDepthId, targets[(int)BasisCameraPass.Depth]);
        passComposeMaterial.SetTexture(ComposeNormalsId, targets[(int)BasisCameraPass.Normals]);
        passComposeMaterial.SetTexture(ComposeMaskId, targets[(int)BasisCameraPass.Mask]);
        Graphics.Blit(renderTexture, quad, passComposeMaterial);
        return true;
    }
    private void EnqueueRenderPasses()
    {
        passesEnqueued = false;
        BasisCameraPassSet wanted = passCaptureRender ? passCaptureSet : captureInFlight ? BasisCameraPassSet.None : LivePassSet;
        if (wanted == BasisCameraPassSet.None || CameraData == null || renderTexture == null || !ReferenceEquals(captureCamera.targetTexture, renderTexture)) return;
        if (!LoadPassMaterials()) return;

        ScriptableRenderer renderer = CameraData.scriptableRenderer;
        if (renderer == null) return;

        renderPassesPass ??= new BasisCameraPassesPass();
        bool data = passCaptureRender && passCaptureData;
        int subjects = BasisCameraCaptureLayers.SubjectMask(captureCamera.cullingMask) & captureCamera.cullingMask;
        BasisCameraPassSet ready = renderPassesPass.Setup(passMaterial, passMaskMaterial, renderTexture, passCaptureRender ? capturePassTextures : passTextures, wanted, subjects, PassDepthRange, data, captureCamera.transform.rotation);
        if (ready == BasisCameraPassSet.None) return;

        renderer.EnqueuePass(renderPassesPass);
        if (passCaptureRender)
        {
            capturePassesRendered = ready;
            return;
        }
        passesEnqueued = true;
        passesEnqueuedSet = ready;
    }
    private void MarkRenderPassesRendered()
    {
        if (!passesEnqueued) return;
        passesEnqueued = false;
        passFrameRendered = true;
        livePassesRendered |= passesEnqueuedSet;
        passFramesRendered++;
    }
    private void BeginPassCapture()
    {
        passCaptureSet = CapturePassSet;
        passCaptureData = BasisCameraCaptureFormats.IsExr(captureFormat);
        passCaptureRender = passCaptureSet != BasisCameraPassSet.None;
        captureQuadReady = false;
        capturePassesRendered = BasisCameraPassSet.None;
        if (passCaptureRender) EnsurePassTargets(capturePassTextures, ref captureQuadTexture, passCaptureSet, passCaptureData);
    }
    private void EndPassCapture()
    {
        passCaptureRender = false;
        captureQuadReady = PassOutput == BasisCameraPassOutput.QuadView && ComposeQuad(capturePassTextures, captureQuadTexture, capturePassesRendered, passCaptureData);
    }
    private void ReleaseCapturePasses()
    {
        for (int Index = 0; Index < capturePassTextures.Length; Index++) BasisCameraRenderTargets.Release(ref capturePassTextures[Index]);
        BasisCameraRenderTargets.Release(ref captureQuadTexture);
        captureQuadReady = false;
        passCaptureSet = BasisCameraPassSet.None;
        capturePassesRendered = BasisCameraPassSet.None;
    }
    private void SaveSeparatePasses(string stamp, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata)
    {
        if (!SeparatePasses || (capturePassesRendered & SeparatePassSet) == BasisCameraPassSet.None) return;

        bool exr = passCaptureData;
        string format = captureFormat, extension = BasisCameraCaptureFormats.Extension(captureFormat);
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            BasisCameraPass pass = (BasisCameraPass)Index;
            RenderTexture source = capturePassTextures[Index];
            if (!IsSeparatePass(pass) || !BasisCameraPasses.Contains(capturePassesRendered, pass) || !BasisCameraPasses.Matches(source, renderTexture, exr)) continue;

            RenderTexture copy = BasisCameraRenderTargets.Create(source.width, source.height, BasisCameraPasses.TargetFormat(exr), 0, 1, !exr, "BasisPassCapture");
            Graphics.Blit(source, copy);

            string suffix = BasisCameraPasses.Suffixes[Index];
            string path = BasisCameraPhotoFolder.PathFor(BasisCameraPasses.FileName($"Screenshot_{stamp}_{source.width}x{source.height}.{extension}", pass));
            int width = source.width, height = source.height;
            AsyncGPUReadback.Request(copy, 0, request =>
            {
                RenderTexture finished = copy;
                BasisCameraRenderTargets.Release(ref finished);
                if (request.hasError)
                {
                    BasisDebug.LogError($"GPU readback of the {suffix} pass failed.", BasisDebug.LogTag.Camera);
                    return;
                }
                SavePassAsync(request.GetData<byte>().ToArray(), exr, width, height, format, path, photoMetadata);
            });
        }
    }
    private static async void SavePassAsync(byte[] pixels, bool exr, int width, int height, string format, string path, BasisHandHeldCameraPhotoMetadata.PhotoMetadata photoMetadata)
    {
        try
        {
            GraphicsFormat pixelFormat = exr ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.R8G8B8A8_SRGB;
            byte[] imageData = await BasisTasks.Run(() =>
            {
                byte[] encoded = exr ? ImageConversion.EncodeArrayToEXR(pixels, pixelFormat, (uint)width, (uint)height, 0, Texture2D.EXRFlags.CompressZIP) : ImageConversion.EncodeArrayToPNG(pixels, pixelFormat, (uint)width, (uint)height, 0);
                return photoMetadata != null ? BasisHandHeldCameraPhotoMetadata.Embed(encoded, format, photoMetadata, width, height) : encoded;
            });
            await BasisFiles.WriteAllBytesAsync(path, imageData);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Could not save render pass {path}: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
        }
    }
    private void TickPassOutputs()
    {
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            BasisCameraPass pass = (BasisCameraPass)Index;
            bool wanted = SeparatePasses && IsSeparatePass(pass) && string.IsNullOrEmpty(passOutputFailures[Index]);
            bool platform = wanted && IsVideoOutputActive, web = wanted && IsWebStreamActive;

            if (platform && passSinks[Index] == null) StartPassSink(Index);
            else if (!platform && passSinks[Index] != null) StopPassSink(Index);

            if (web && passWebSinks[Index] == null) StartPassWebSink(Index);
            else if (!web && passWebSinks[Index] != null) StopPassWebSink(Index);

            if (passSinks[Index] != null && passSinks[Index].FailureMessage != null) FailPassOutput(Index, passSinks[Index].FailureMessage);
            if (passWebSinks[Index] != null) PushPassWebFrame(Index);
        }
    }
    private void StartPassSink(int index)
    {
        BasisCameraPass pass = (BasisCameraPass)index;
        IBasisVideoOutputSink sink = BasisCameraVideoPlatform.CreateSink();
        if (sink == null)
        {
            passOutputFailures[index] = $"{BasisCameraVideoPlatform.BackendName} is not available in this build.";
            return;
        }

        BasisVideoOutputSettings main = VideoOutputSettings;
        string name = BasisCameraVideoPlatform.ClaimSenderName(BasisCameraPasses.SenderName(string.IsNullOrEmpty(activeSenderName) ? main.SenderName : activeSenderName, pass));
        BasisVideoOutputSettings settings = new BasisVideoOutputSettings { Width = main.Width, Height = main.Height, FrameRate = main.FrameRate, SenderName = name, DevicePath = string.Empty };
        settings.ClampSize();
        RenderTexture texture = BasisCameraRenderTargets.Create(settings.Width, settings.Height, BasisCameraVideoPlatform.FrameFormat, 0, 1, true, "BasisVideoOutput" + BasisCameraPasses.Suffixes[index]);

        bool started;
        try
        {
            started = sink.Start(settings, captureCamera.gameObject);
        }
        catch (Exception e)
        {
            started = false;
            passOutputFailures[index] = $"{e.GetType().Name}: {e.Message}";
        }

        if (!started)
        {
            if (string.IsNullOrEmpty(passOutputFailures[index])) passOutputFailures[index] = sink.FailureMessage ?? $"{BasisCameraVideoPlatform.BackendName} would not start.";
            sink.Stop();
            BasisCameraRenderTargets.Release(ref texture);
            BasisCameraVideoPlatform.ReleaseSenderName(name);
            BasisDebug.LogError($"'{name}' could not start: {passOutputFailures[index]}", BasisDebug.LogTag.Camera);
            return;
        }

        passSinks[index] = sink;
        passStreamTextures[index] = texture;
        passSenderNames[index] = name;
        BasisDebug.Log($"{BasisCameraVideoPlatform.BackendName} output started as '{name}': {settings.Width}x{settings.Height}", BasisDebug.LogTag.Camera);
    }
    private void StopPassSink(int index)
    {
        if (passSinks[index] != null)
        {
            passSinks[index].Stop();
            passSinks[index] = null;
        }
        BasisCameraRenderTargets.Release(ref passStreamTextures[index]);
        BasisCameraVideoPlatform.ReleaseSenderName(passSenderNames[index]);
        passSenderNames[index] = null;
    }
    private void StartPassWebSink(int index)
    {
        int mainPort = webSink != null ? webSink.Port : VideoOutputSettings.WebPort;
        int port = BasisCameraVideoPlatform.FirstUnclaimedPort(Mathf.Clamp(mainPort + 1 + index, 1024, 65500));
        BasisWebVideoOutputSink sink = new BasisWebVideoOutputSink();
        if (!sink.Start(port))
        {
            passOutputFailures[index] = sink.FailureMessage ?? $"No free port from {port} upwards to serve the stream on.";
            sink.Stop();
            return;
        }

        BasisCameraVideoPlatform.ClaimPort(sink.Port);
        passWebSinks[index] = sink;
        passWebPacers[index].Reset();
        passPushedFrames[index] = passFramesRendered - 1;
        BasisDebug.Log($"{BasisCameraPasses.Suffixes[index]} web stream started at {sink.Url}", BasisDebug.LogTag.Camera);
    }
    private void StopPassWebSink(int index)
    {
        BasisWebVideoOutputSink sink = passWebSinks[index];
        if (sink != null)
        {
            BasisCameraVideoPlatform.ReleasePort(sink.Port);
            sink.Stop();
            passWebSinks[index] = null;
        }
        BasisCameraRenderTargets.Release(ref passWebTextures[index]);
    }
    private void FailPassOutput(int index, string failure)
    {
        passOutputFailures[index] = failure;
        BasisDebug.LogError($"{BasisCameraPasses.Suffixes[index]} output stopped: {failure}", BasisDebug.LogTag.Camera);
        StopPassSink(index);
        StopPassWebSink(index);
    }
    private void PushPassVideoFrames()
    {
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++)
        {
            IBasisVideoOutputSink sink = passSinks[Index];
            RenderTexture source = passTextures[Index], target = passStreamTextures[Index];
            if (sink == null || target == null || source == null || !BasisCameraPasses.Contains(livePassesRendered, (BasisCameraPass)Index)) continue;

            BasisCameraRenderTargets.GetBlitCrop(source, target, out Vector2 scale, out Vector2 offset);
            if (BasisCameraVideoPlatform.FlipsRows)
            {
                scale.y = -scale.y;
                offset.y += -scale.y;
            }
            Graphics.Blit(source, target, scale, offset);
            sink.PushFrame(target, false);
        }
    }
    private void PushPassWebFrame(int index)
    {
        BasisWebVideoOutputSink sink = passWebSinks[index];
        if (sink.FailureMessage != null)
        {
            FailPassOutput(index, sink.FailureMessage);
            return;
        }
        if (!sink.HasClients)
        {
            passWebPacers[index].Reset();
            return;
        }

        RenderTexture source = passTextures[index];
        bool ready = source != null && BasisCameraPasses.Contains(livePassesRendered, (BasisCameraPass)index) && sink.CanAcceptFrame;
        if (!passWebPacers[index].AllowThisFrame(Time.unscaledDeltaTime, VideoOutputSettings.FrameRate, passPushedFrames[index] != passFramesRendered, ready)) return;
        passPushedFrames[index] = passFramesRendered;

        BasisVideoOutputSettings settings = VideoOutputSettings;
        settings.ClampSize();
        RenderTexture target = passWebTextures[index];
        if (target == null || target.width != settings.Width || target.height != settings.Height)
        {
            BasisCameraRenderTargets.Release(ref passWebTextures[index]);
            passWebTextures[index] = target = BasisCameraRenderTargets.Create(settings.Width, settings.Height, RenderTextureFormat.ARGB32, 0, 1, true, "BasisWebVideoOutput" + BasisCameraPasses.Suffixes[index]);
        }

        BasisCameraRenderTargets.GetBlitCrop(source, target, out Vector2 scale, out Vector2 offset);
        Graphics.Blit(source, target, scale, offset);
        sink.PushFrame(target, settings.WebQuality);
    }
    private void StopPassVideoSinks()
    {
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++) StopPassSink(Index);
    }
    private void StopPassWebSinks()
    {
        for (int Index = 0; Index < BasisCameraPasses.Count; Index++) StopPassWebSink(Index);
    }
    private void ClearPassOutputFailures()
    {
        for (int Index = 0; Index < passOutputFailures.Length; Index++) passOutputFailures[Index] = null;
    }
    private void ReleaseRenderPasses()
    {
        StopPassVideoSinks();
        StopPassWebSinks();
        ReleaseCapturePasses();
        for (int Index = 0; Index < passTextures.Length; Index++) BasisCameraRenderTargets.Release(ref passTextures[Index]);
        BasisCameraRenderTargets.Release(ref quadTexture);
        quadReady = false;
        livePassesRendered = BasisCameraPassSet.None;
        renderPassesPass?.Release();
        renderPassesPass = null;
        BasisCameraRenderTargets.DestroyAndClear(ref passMaterial);
        BasisCameraRenderTargets.DestroyAndClear(ref passMaskMaterial);
        BasisCameraRenderTargets.DestroyAndClear(ref passComposeMaterial);
    }
}
