using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Optional caption presenter for BasisMediaPlayer. Parent this under your video
// surface and it draws the active CEA-608 cue on a TextMeshPro element, composited
// as a separate layer. The video texture is never touched, so the overlay survives
// the external-texture rebind on size change or XR loader swap, and consumers keep
// full control of styling and placement.
//
// It only mirrors text and visibility: the engine parses the captions and the
// player releases each cue when playback reaches it. Visibility and styling follow
// the viewer's own preferences (per player where they have set one, the stored
// defaults otherwise), never anything the world authored.
//
// Readability is the TMP element's job. White text with a black outline, or a
// background graphic assigned below, is the usual treatment over arbitrary video.
[AddComponentMenu("Basis/Basis Media Caption Overlay")]
public sealed class BasisMediaCaptionOverlay : MonoBehaviour
{
    [Tooltip("Player to show captions for. Defaults to a BasisMediaPlayer found on this object or its parents.")]
    [SerializeField] private BasisMediaPlayer player;

    [Tooltip("Text element the cue is written into. Defaults to a TMP_Text on this object or its children.")]
    [SerializeField] private TMP_Text label;

    [Tooltip("Optional background graphic (e.g. an Image box) shown only while a caption is visible. Its alpha follows the viewer's caption background opacity.")]
    [SerializeField] private Graphic background;

    private BasisMediaPlayer bound;
    private string currentText;

    public BasisMediaPlayer Player
    {
        get => player;
        set { player = value; if (isActiveAndEnabled) Bind(value); }
    }

    private void Awake()
    {
        if (label == null) label = GetComponentInChildren<TMP_Text>(true);
        if (player == null) player = GetComponentInParent<BasisMediaPlayer>();
        if (label == null)
            BasisDebug.LogWarning("[BasisMedia] BasisMediaCaptionOverlay has no TMP_Text assigned or in children; captions won't be drawn.", BasisDebug.LogTag.Video);
        // Caption text is stream-provided: keep TMP rich-text off so literal '<...>'
        // shows verbatim instead of being parsed as markup. Captions are a passive
        // overlay, so neither graphic should swallow pointer/UI raycasts.
        if (label != null) { label.richText = false; label.raycastTarget = false; }
        if (background != null) background.raycastTarget = false;
    }

    private void OnEnable()
    {
        // The stored preferences are the defaults, followed for as long as this
        // overlay is alive; a player the viewer has decided individually raises
        // its own event, hooked in Bind.
        BasisMediaSettings.CaptionsEnabled.OnChanged += HandleEnabled;
        BasisMediaSettings.CaptionTextOpacity.OnChanged += HandleStyle;
        BasisMediaSettings.CaptionBackgroundOpacity.OnChanged += HandleStyle;
        Bind(player);
    }

    private void OnDisable()
    {
        BasisMediaSettings.CaptionsEnabled.OnChanged -= HandleEnabled;
        BasisMediaSettings.CaptionTextOpacity.OnChanged -= HandleStyle;
        BasisMediaSettings.CaptionBackgroundOpacity.OnChanged -= HandleStyle;
        Unbind();
    }

    private void Bind(BasisMediaPlayer p)
    {
        Unbind();
        bound = p;
        if (bound == null) { Apply(); return; }
        bound.OnCaptionChanged += HandleCaption;
        bound.OnCaptionPreferencesChanged += Apply;
        // Bind mid-playback without waiting for the next cue.
        currentText = bound.CurrentCaption;
        Apply();
    }

    private void Unbind()
    {
        if (bound != null)
        {
            bound.OnCaptionChanged -= HandleCaption;
            bound.OnCaptionPreferencesChanged -= Apply;
            bound = null;
        }
        // Drop any caption text so it can't linger across a rebind or after disable.
        currentText = null;
        Apply();
    }

    private void HandleCaption(string text)
    {
        currentText = text;
        Apply();
    }

    private void HandleEnabled(bool _) => Apply();
    private void HandleStyle(float _) => Apply();

    private void Apply()
    {
        bool show = bound != null && bound.CaptionsEnabledEffective && !string.IsNullOrEmpty(currentText);
        if (label != null)
        {
            if (show) label.text = currentText;
            label.enabled = show;
            label.alpha = Mathf.Clamp01(bound != null
                ? bound.CaptionTextOpacityEffective
                : BasisMediaSettings.CaptionTextOpacity.RawValue);
        }
        if (background != null)
        {
            background.enabled = show;
            Color c = background.color;
            c.a = Mathf.Clamp01(bound != null
                ? bound.CaptionBackgroundOpacityEffective
                : BasisMediaSettings.CaptionBackgroundOpacity.RawValue);
            background.color = c;
        }
    }
}
