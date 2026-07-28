using UnityEngine;
using UnityEngine.Rendering;
using Plugin = Klak.Syphon.Interop.PluginServer;

namespace Klak.Syphon {

[ExecuteInEditMode]
public sealed class SyphonServer : MonoBehaviour
{
    #region Public properties

    public string ServerName
      { get => _serverName;
        set { TeardownPlugin(); _serverName = value; } }

    public CaptureMethod CaptureMethod
      { get => _captureMethod;
        set { TeardownPlugin(); _captureMethod = value; } }

    public Camera SourceCamera
      { get => _sourceCamera;
        set { TeardownPlugin(); _sourceCamera = value; } }

    public Texture SourceTexture
      { get => _sourceTexture;
        set { TeardownPlugin(); _sourceTexture = value; } }

    [field:SerializeField] public bool KeepAlpha { get; set; }

    [field:SerializeField] public SyphonResources Resources { get; set; }

    #endregion

    #region Property backing fields

    [SerializeField] string _serverName = "Syphon Server";
    [SerializeField] CaptureMethod _captureMethod;
    [SerializeField] Camera _sourceCamera;
    [SerializeField] Texture _sourceTexture;

    #endregion

    #region SRP callback

    Camera _attachedCamera;

    #if KLAK_SYPHON_HAS_SRP

    void AttachCameraCallback(Camera target)
    {
        CameraCaptureBridge.AddCaptureAction(target, OnCameraCapture);
        _attachedCamera = target;
    }

    void ResetCameraCallback()
    {
        if (_attachedCamera == null) return;
        CameraCaptureBridge.RemoveCaptureAction(_attachedCamera, OnCameraCapture);
        _attachedCamera = null;
    }

    void OnCameraCapture(RenderTargetIdentifier source, CommandBuffer cb)
    {
        if (_attachedCamera == null || _plugin.texture == null) return;
        Blitter.Blit(cb, source, _plugin.texture, KeepAlpha);
    }

    #else

    void AttachCameraCallback(Camera target) {}
    void ResetCameraCallback() {}

    #endif

    #endregion

    #region Syphon server plugin
 
    (Plugin instance, Texture2D texture) _plugin;

    void SetupPlugin()
    {
        if (_plugin.instance != null) return;

        // Server name validity
        if (string.IsNullOrEmpty(_serverName)) return;

        // Texture capture mode
        if (_captureMethod == CaptureMethod.Texture)
        {
            if (_sourceTexture == null) return;
            var (w, h) = (_sourceTexture.width, _sourceTexture.height);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
        }

        // Camera capture mode
        if (_captureMethod == CaptureMethod.Camera)
        {
            if (_sourceCamera == null) return;
            var (w, h) = (_sourceCamera.pixelWidth, _sourceCamera.pixelHeight);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
            AttachCameraCallback( _sourceCamera);
        }

        // Game View capture mode
        if (_captureMethod == CaptureMethod.GameView)
        {
            var (w, h) = (Screen.width, Screen.height);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
        }

        // Blitter lazy initialization
        Blitter.Prepare(Resources);

        // Coroutine start
        StartCoroutine(CaptureCoroutine());
    }

    void TeardownPlugin()
    {
        // Plugin instance/texture disposal
        _plugin.instance?.Dispose();
        Utility.Destroy(_plugin.texture);
        _plugin = (null, null);

        ResetCameraCallback();
        StopAllCoroutines();
    }

    #endregion

    #region Capture coroutine

    System.Collections.IEnumerator CaptureCoroutine()
    {
        for (var eof = new WaitForEndOfFrame(); true;)
        {
            // End of the frame
            yield return eof;

            if (_plugin.instance == null) continue;

            // Texture capture mode
            if (_captureMethod == CaptureMethod.Texture)
                Blitter.Blit(_sourceTexture, _plugin.texture, KeepAlpha);

            // Game View capture mode
            if (_captureMethod == CaptureMethod.GameView)
            {
                var rt = Utility.CaptureScreenAsTempRT();
                Blitter.Blit(rt, _plugin.texture, KeepAlpha, vflip: true);
                RenderTexture.ReleaseTemporary(rt);
            }

            // Frame update notification
            _plugin.instance.PublishTexture();
        }
    }

    #endregion

    #region MonoBehaviour implementation

    void Start()
      => InternalCommon.ApplyCurrentColorSpace();

    void OnValidate()
      => TeardownPlugin();

    void OnDisable()
      => TeardownPlugin();

    void Update()
      => SetupPlugin();

    #endregion
}

} // namespace Klak.Syphon
