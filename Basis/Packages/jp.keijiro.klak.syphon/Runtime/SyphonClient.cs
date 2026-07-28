using UnityEngine;
using Plugin = Klak.Syphon.Interop.PluginClient;

namespace Klak.Syphon {

[ExecuteInEditMode]
public sealed class SyphonClient : MonoBehaviour
{
    #region Public properties

    public string ServerName
      { get => _serverName;
        set { TeardownPlugin(); _serverName = value; } }

    [field:SerializeField] public RenderTexture TargetTexture { get; set; }
    [field:SerializeField] public Renderer TargetRenderer { get; set; }
    [field:SerializeField] public string TargetMaterialProperty { get; set; }

    public Texture2D Texture => _plugin.texture;

    #endregion

    #region Property backing fields

    [SerializeField] string _serverName = "Syphon Server";

    #endregion

    #region Private members

    (Plugin instance, Texture2D texture) _plugin;

    void SetupPlugin()
    {
        if (_plugin.instance != null) return;

        // Server name validity check
        if (string.IsNullOrEmpty(_serverName)) return;

        // Plugin instantiation
        _plugin.instance = Plugin.Create(_serverName);
    }

    void TeardownPlugin()
    {
        // Plugin instance/texture disposal
        _plugin.instance?.Dispose();
        Utility.Destroy(_plugin.texture);
        _plugin = (null, null);

        // Renderer reset
        Utility.SetTexture(TargetRenderer, TargetMaterialProperty, null);
    }

    void UpdatePlugin()
    {
        // Plugin-side invalidation
        if (_plugin.instance == null || !_plugin.instance.IsValid)
        {
            TeardownPlugin();
            return;
        }

        // Plugin update
        _plugin.instance.Update();

        // Plugin texture invalidation
        if (!_plugin.instance.HasSameTexture(_plugin.texture))
        {
            Utility.Destroy(_plugin.texture);
            _plugin.texture = null;
        }

        // Plugin texture lazy initialization
        if (_plugin.texture == null)
            _plugin.texture = _plugin.instance.CreateTexture();
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
    {
        SetupPlugin();
        UpdatePlugin();

        // Plugin texture readiness check
        if (_plugin.texture == null) return;

        // Target texture update
        if (TargetTexture != null)
            Graphics.Blit(_plugin.texture, TargetTexture);

        // Target renderer override
        Utility.SetTexture(TargetRenderer, TargetMaterialProperty, _plugin.texture);
    }

    #endregion
}

} // namespace Klak.Syphon
