using UnityEngine;

/// <summary>
/// Holds references to bundled content (avatars, systems, props, scenes) and their selectors.
/// Provides accessors for content validation and enforces singleton access via <see cref="Instance"/>.
/// </summary>
public partial class BundledContentHolder : MonoBehaviour
{
    /// <summary>
    /// Selector used to validate or control avatar scripts.
    /// </summary>
    public ContentPoliceSelector AvatarScriptSelector;

    /// <summary>
    /// Selector used to validate or control system scripts.
    /// </summary>
    public ContentPoliceSelector SystemScriptSelector;

    /// <summary>
    /// Selector used to validate or control prop scripts.
    /// </summary>
    public ContentPoliceSelector PropScriptSelector;

    /// <summary>
    /// Selector used to validate or control World scripts.
    /// </summary>
    public ContentPoliceSelector WorldScriptSelector;


    /// <summary>
    /// Default loadable bundle representing the starting scene.
    /// </summary>
    public BasisLoadableBundle DefaultScene;

    /// <summary>
    /// Default loadable bundle representing the default avatar.
    /// </summary>
    public BasisLoadableBundle DefaultAvatar;

    /// <summary>
    /// Singleton-style instance of this holder.
    /// </summary>
    public static BundledContentHolder Instance;

    /// <summary>
    /// If true, scene loading will use Unity Addressables instead of direct references.
    /// </summary>
    public bool UseAddressablesToLoadScene = false;

    /// <summary>
    /// If true, uses the scene reference provided here instead of externally specified ones.
    /// </summary>
    public bool UseSceneProvidedHere = false;

    public Shader UrpShader;
    /// <summary>
    /// Retrieves the appropriate <see cref="ContentPoliceSelector"/> for the specified selector type.
    /// </summary>
    /// <param name="Selector">The type of content to retrieve (Avatar, System, Prop).</param>
    /// <param name="PoliceCheck">Outputs the matching content police selector.</param>
    /// <returns><c>true</c> if a selector was found; otherwise <c>false</c>.</returns>
    public bool GetSelector(Selector Selector, out ContentPoliceSelector PoliceCheck)
    {
        switch (Selector)
        {
            case Selector.Avatar:
                PoliceCheck = AvatarScriptSelector;
                return true;
            case Selector.System:
                PoliceCheck = SystemScriptSelector;
                return true;
            case Selector.Prop:
                PoliceCheck = PropScriptSelector;
                return true;
            case Selector.World:
                PoliceCheck = WorldScriptSelector;
                return true;
            default:
                PoliceCheck = null;
                BasisDebug.LogError("Missing Selector");
                return false;
        }
    }

    /// <summary>
    /// Unity Awake hook. Sets the static <see cref="Instance"/> reference.
    /// </summary>
    public void Awake()
    {
        Instance = this;
    }
}
