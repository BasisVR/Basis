using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.BasisSdk.Highlight
{
    /// <summary>
    /// State for the BasisHighlight render feature.
    ///
    /// Two independent collections:
    ///  - <see cref="Overrides"/> records *capability* (renderer => (mode, mask material)).
    ///    Authored by <see cref="BasisHighlightOverride"/>.
    ///  - <see cref="Active"/> records *state* (renderer is currently highlighted).
    ///    Driven by hover/interaction code via <see cref="Highlight"/> / <see cref="Unhighlight"/>.
    ///
    /// The static methods are thin facades that delegate to <see cref="Instance"/>. An
    /// implementing application can replace <see cref="Instance"/> with a subclass during
    /// bootstrap to override highlight behavior without touching the static call sites.
    /// </summary>
    public class BasisHighlightManager
    {
        protected readonly struct OverrideEntry
        {
            public readonly BasisHighlightOverrideType Type;
            public readonly Material Material;

            public OverrideEntry(BasisHighlightOverrideType type, Material material)
            {
                Type = type;
                Material = material;
            }
        }

        /// <summary>
        /// The active manager. Assign a subclass during bootstrap to override behavior.
        /// </summary>
        public static BasisHighlightManager Instance = new BasisHighlightManager();

        protected readonly Dictionary<Renderer, OverrideEntry> Overrides = new();

        // Renderer => cached submesh count, resolved once at Highlight() registration
        protected readonly Dictionary<Renderer, int> Active = new();

        // Scratch list reused across DrawAll calls to drop fake-null entries
        // without allocating each frame.
        private readonly List<Renderer> _pruneScratch = new();

        // -------- Static facade (call-site API) --------

        public static int ActiveCount => Instance.ActiveCountImpl;

        public static void RegisterOverride(Renderer r, BasisHighlightOverrideType type, Material maskMaterial)
            => Instance.RegisterOverrideImpl(r, type, maskMaterial);

        public static void UnregisterOverride(Renderer r) => Instance.UnregisterOverrideImpl(r);

        public static void Highlight(Renderer r) => Instance.HighlightImpl(r);

        public static void Unhighlight(Renderer r) => Instance.UnhighlightImpl(r);

        public static void SetHighlight(Renderer r, bool active) => Instance.SetHighlightImpl(r, active);

        internal static void DrawAll(RasterCommandBuffer cmd, Material fallback)
            => Instance.DrawAllImpl(cmd, fallback);

        // -------- Overridable implementations --------

        protected virtual int ActiveCountImpl => Active.Count;

        // -------- Authoring API (BasisHighlightOverride) --------

        protected virtual void RegisterOverrideImpl(Renderer r, BasisHighlightOverrideType type, Material maskMaterial)
        {
            if (r == null)
            {
                return;
            }

            Overrides[r] = new OverrideEntry(type, maskMaterial);
        }

        protected virtual void UnregisterOverrideImpl(Renderer r)
        {
            if (r == null)
            {
                return;
            }

            Overrides.Remove(r);
            // If the renderer was being highlighted at the moment of teardown,
            // drop it from Active too. The pass would lazy-prune it next frame
            // anyway, but this keeps state consistent for ActiveCount.
            Active.Remove(r);
        }

        // -------- Runtime API (hover / interaction) --------

        protected virtual void HighlightImpl(Renderer r)
        {
            if (r == null)
            {
                return;
            }

            Active[r] = ResolveSubmeshCount(r);
        }

        // Resolve the draw-index bound (submesh count) once at registration
        private static int ResolveSubmeshCount(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh != null ? Mathf.Max(1, smr.sharedMesh.subMeshCount) : 0;
            }

            if (!r.TryGetComponent(out MeshFilter mf))
            {
                return 0;
            }

            return mf.sharedMesh != null ? Mathf.Max(1, mf.sharedMesh.subMeshCount) : 0;
        }

        protected virtual void UnhighlightImpl(Renderer r)
        {
            if (r == null)
            {
                return;
            }

            Active.Remove(r);
        }

        protected virtual void SetHighlightImpl(Renderer r, bool active)
        {
            if (r == null)
            {
                return;
            }

            if (active)
            {
                HighlightImpl(r);
            }
            else
            {
                UnhighlightImpl(r);
            }
        }

        // -------- Used by the render pass --------

        /// <summary>
        /// Iterate the active set and draw each renderer using its override
        /// material (or <paramref name="fallback"/> if none registered). Lazy-prunes
        /// destroyed renderers caught via Unity's fake-null check.
        /// </summary>
        protected virtual void DrawAllImpl(RasterCommandBuffer cmd, Material fallback)
        {
            if (Active.Count == 0)
            {
                return;
            }

            _pruneScratch.Clear();
            foreach (KeyValuePair<Renderer, int> kv in Active)
            {
                Renderer r = kv.Key;
                if (r == null)
                {
                    _pruneScratch.Add(r);
                    continue;
                }

                if (!r.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Material mat = fallback;
                if (Overrides.TryGetValue(r, out OverrideEntry ov))
                {
                    switch (ov.Type)
                    {
                        case BasisHighlightOverrideType.Exclude: continue; // skip entirely
                        case BasisHighlightOverrideType.Material: mat = ov.Material != null ? ov.Material : fallback; break;
                        case BasisHighlightOverrideType.None:
                        default: mat = fallback; break; // None
                    }
                }

                if (mat == null)
                {
                    continue;
                }

                DrawSubmeshes(cmd, r, mat, kv.Value, 0);
            }

            if (_pruneScratch.Count > 0)
            {
                foreach (Renderer r in _pruneScratch)
                {
                    Active.Remove(r);
                }

                _pruneScratch.Clear();
            }
        }

        protected static void DrawSubmeshes(RasterCommandBuffer cmd, Renderer r, Material mat, int submeshCount, int shaderPass)
        {
            for (int i = 0; i < submeshCount; i++)
            {
                cmd.DrawRenderer(r, mat, i, shaderPass);
            }
        }
    }
}
