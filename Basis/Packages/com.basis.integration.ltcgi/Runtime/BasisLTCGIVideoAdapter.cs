using UnityEngine;

namespace Basis.Integration.LTCGI
{
    /// <summary>
    /// Bridges a <see cref="BasisMediaPlayer"/> into LTCGI's dynamic video path so a
    /// screen lit by the player emits matching realtime area light.
    /// </summary>
    /// <remarks>
    /// The player surfaces the current frame as <see cref="BasisMediaPlayer.OutputTexture"/>
    /// (a zero-copy native GPU texture on the OS-codec path, a CPU Texture2D on the
    /// synthetic path) and swaps that texture's identity on start, resize, and reconnect.
    /// LTCGI samples a single shared video texture, so this adapter owns a RenderTexture
    /// with a stable handle, blits each frame into it, and hands it to
    /// <c>LTCGI_UdonAdapter._SetVideoTexture</c> once per resize. The blit flips both
    /// axes by default, which orients the feed correctly for the standard Basis
    /// media-player quad (vertical handles native D3D top-left origin; horizontal
    /// matches the quad's UVs). Requires a baked LTCGI controller with at least one
    /// dynamic screen.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)] // after LTCGI_UdonAdapter has initialised its globals
    public sealed class BasisLTCGIVideoAdapter : MonoBehaviour
    {
        [Tooltip("Player to read frames from. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
        public BasisMediaPlayer Player;

        [Tooltip("LTCGI runtime adapter to feed. If unassigned, the first one in the scene is used.")]
        public LTCGI_UdonAdapter Target;

        [Tooltip("Flip the source vertically when blitting. On by default: native D3D textures are top-left origin and arrive upside-down.")]
        public bool FlipVertically = true;

        [Tooltip("Mirror the source horizontally when blitting. On by default: together with FlipVertically this matches the standard Basis media-player quad's UVs. Turn off for a differently-mapped mesh.")]
        public bool FlipHorizontally = true;

        private RenderTexture rt;
        private Texture source;
        private int sourceWidth;
        private int sourceHeight;

        private static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int[] LodGlobalIds =
        {
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD1"),
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD2"),
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD3"),
        };

        private void Reset()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        }

        private void OnEnable()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
            if (Player == null)
            {
                Debug.LogWarning("BasisLTCGIVideoAdapter: no BasisMediaPlayer found; nothing to feed LTCGI.", this);
                enabled = false;
                return;
            }
            if (Target == null) Target = FindFirstObjectByType<LTCGI_UdonAdapter>();
            if (Target == null)
            {
                Debug.LogWarning("BasisLTCGIVideoAdapter: no LTCGI_UdonAdapter in the scene. Bake an LTCGI controller with at least one dynamic screen.", this);
                enabled = false;
                return;
            }
            if (Target.BlurCRTInput == null)
            {
                Debug.LogWarning("BasisLTCGIVideoAdapter: LTCGI_UdonAdapter has no BlurCRTInput. The controller needs a dynamic screen baked before video can be fed.", this);
                enabled = false;
                return;
            }

            Player.OnOutputTextureChanged += HandleTextureChanged;
            HandleTextureChanged(Player.OutputTexture);
        }

        private void OnDisable()
        {
            if (Player != null) Player.OnOutputTextureChanged -= HandleTextureChanged;
            // Drop LTCGI's pointer before releasing the RT so the blur chain never
            // samples a destroyed texture.
            if (Target != null) Target._SetVideoTexture(null);
            source = null;
            ReleaseRT();
        }

        private void HandleTextureChanged(Texture texture)
        {
            source = texture;
            if (texture == null) return;
            EnsureRT(texture.width, texture.height);
            if (rt == null) return;
            Blit();
            Target._SetVideoTexture(rt);
        }

        private void LateUpdate()
        {
            if (source == null || rt == null) return;
            Blit();
            EnsureBound();
        }

        // LTCGI sets its blur-chain globals once at init and the video feed is pushed
        // once per resize; an external reset (asset unload, render-pipeline reinit, a
        // Desktop/VR mode switch) can clear them with nothing to restore them. Detect a
        // dropped binding and re-assert both the video feed and the LOD CRT globals so
        // the reflection self-heals within a frame.
        private void EnsureBound()
        {
            if (Target == null || Target.BlurCRTInput == null) return;
            var mat = Target.BlurCRTInput.material;
            if (mat == null || mat.GetTexture(IdMainTex) == rt) return;

            Target._SetVideoTexture(rt);
            var lods = Target._LTCGI_LODs;
            if (lods == null) return;
            for (int j = 1; j < lods.Length && j - 1 < LodGlobalIds.Length; j++)
                if (lods[j] != null) Shader.SetGlobalTexture(LodGlobalIds[j - 1], lods[j]);
        }

        private void Blit()
        {
            var scale = new Vector2(FlipHorizontally ? -1f : 1f, FlipVertically ? -1f : 1f);
            var offset = new Vector2(FlipHorizontally ? 1f : 0f, FlipVertically ? 1f : 0f);
            Graphics.Blit(source, rt, scale, offset);
        }

        private void EnsureRT(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            if (rt != null && sourceWidth == w && sourceHeight == h) return;
            ReleaseRT();
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "BasisLTCGIVideo",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
            };
            rt.Create();
            sourceWidth = w;
            sourceHeight = h;
        }

        private void ReleaseRT()
        {
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
            rt = null;
            sourceWidth = 0;
            sourceHeight = 0;
        }
    }
}
