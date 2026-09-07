using UnityEngine;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Formerly staggered a freshly-installed avatar's Renderers back on a few per frame (skinned
    /// "body" renderers first, plain "accessory" renderers after) to spread the explicit-PSO
    /// backends' (D3D12/Vulkan/Metal) synchronous first-draw Pipeline State Object creation cost
    /// across several frames instead of one hitch.
    ///
    /// <b>DISABLED 2026-08-30 — safety regression, not a perf tuning knob.</b> The reveal order was
    /// a naive proxy ("skinned = body") with no way to know which renderer is clothing vs. skin for
    /// arbitrary user-uploaded avatars, so for the several frames the queue took to drain, an
    /// avatar's real body could sit fully visible and fully lit before its clothing renderers caught
    /// up — reported by players as avatars reading as nude while loading. There is no hide-then-
    /// reveal ORDERING that is safe for arbitrary UGC (flipping the order just moves the exposure
    /// onto whichever content puts its covering geometry on the renderer type that goes last), so
    /// <see cref="BeginStagedReveal"/> is now a no-op and the old queue/tick machinery was removed
    /// with it. Do not re-enable this by hiding renderers again; a safe redesign would need to keep
    /// 100% of the avatar's geometry visible at all times (e.g. warm PSOs by swapping each renderer
    /// to a pre-warmed placeholder MATERIAL rather than toggling Renderer.enabled) before this comes
    /// back.
    /// </summary>
    public static class BasisAvatarPsoReveal
    {
        public static bool Enabled = false;

        public static void Apply(bool enabled)
        {
            Enabled = enabled;
        }

        /// <summary>
        /// Formerly queued a freshly-installed avatar's renderers for a staggered reveal.
        /// Intentionally inert now — see the safety note on <see cref="BasisAvatarPsoReveal"/>.
        /// Left as a no-op call site (rather than removed) so <see cref="BasisAvatarFactory"/>
        /// doesn't need to change; nothing is hidden and nothing is queued.
        /// </summary>
        public static void BeginStagedReveal(Renderer[] renders)
        {
        }
    }
}
