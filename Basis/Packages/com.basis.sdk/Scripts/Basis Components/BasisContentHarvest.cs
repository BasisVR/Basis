using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    /// <summary>
    /// Kind tag stored parallel to <see cref="BasisContentHarvest.Components"/> so consumers can
    /// dispatch on a byte (jump table) instead of an is-chain, then reinterpret the reference with
    /// <see cref="BasisContentHarvest.UnsafeAs{T}"/>. <see cref="Removed"/> marks a slot whose
    /// component was destroyed during the content walk; never reinterpret those.
    /// </summary>
    public enum BasisComponentKind : byte
    {
        Other = 0,
        Removed,

        Transform,
        RectTransform,
        Animator,
        Animation,

        MeshFilter,
        Collider,
        Rigidbody,
        Rigidbody2D,
        Joint,
        Cloth,

        AudioSource,
        AudioListener,

        Camera,
        Light,
        ReflectionProbe,
        LODGroup,
        ParticleSystem,
        VisualEffect,

        Canvas,
        CanvasRenderer,
        CanvasGroup,
        TextMeshProText,

        VideoPlayer,
        NavMeshAgent,
        NavMeshObstacle,
        Terrain,
        Tilemap,

        MeshRenderer,
        SkinnedMeshRenderer,
        ParticleSystemRenderer,
        LineRenderer,
        TrailRenderer,
        BillboardRenderer,
        SpriteRenderer,
        TilemapRenderer,
        VFXRenderer,
    }

    /// <summary>
    /// Snapshot of the single component walk ContentPoliceControl already performs at load.
    /// Holds the raw <see cref="Component"/> array plus a parallel <see cref="BasisComponentKind"/>
    /// tag, letting downstream load steps classify by byte switch instead of re-running
    /// GetComponent / is-chains. Lives on <see cref="BasisContentBase"/> and is transient — the
    /// load path nulls it once consumed.
    /// </summary>
    public sealed class BasisContentHarvest
    {
        public Component[] Components;
        public BasisComponentKind[] Kinds;
        public List<Renderer> Renderers;
        public List<SkinnedMeshRenderer> SkinnedMeshRenderers;
        public List<BasisAuthoredMotion> AuthoredMotions;

        /// <summary>
        /// Maps a component to its <see cref="BasisComponentKind"/>. Single source of truth for the
        /// tag: <see cref="UnsafeAs{T}"/> reinterprets based on this, so the mapping here and the
        /// type a caller passes must agree. Null/destroyed components return <see cref="BasisComponentKind.Other"/>.
        /// </summary>
        public static BasisComponentKind Classify(Component component)
        {
            if (component == null)
            {
                return BasisComponentKind.Other;
            }
            switch (component)
            {
                case SkinnedMeshRenderer _: return BasisComponentKind.SkinnedMeshRenderer;
                case MeshRenderer _: return BasisComponentKind.MeshRenderer;
                case ParticleSystemRenderer _: return BasisComponentKind.ParticleSystemRenderer;
                case LineRenderer _: return BasisComponentKind.LineRenderer;
                case TrailRenderer _: return BasisComponentKind.TrailRenderer;
                case BillboardRenderer _: return BasisComponentKind.BillboardRenderer;
                case SpriteRenderer _: return BasisComponentKind.SpriteRenderer;
                case UnityEngine.Tilemaps.TilemapRenderer _: return BasisComponentKind.TilemapRenderer;
                case UnityEngine.VFX.VFXRenderer _: return BasisComponentKind.VFXRenderer;

                case MeshFilter _: return BasisComponentKind.MeshFilter;
                case Collider _: return BasisComponentKind.Collider;
                case Rigidbody _: return BasisComponentKind.Rigidbody;
                case Rigidbody2D _: return BasisComponentKind.Rigidbody2D;
                case Joint _: return BasisComponentKind.Joint;
                case Cloth _: return BasisComponentKind.Cloth;

                case AudioSource _: return BasisComponentKind.AudioSource;
                case AudioListener _: return BasisComponentKind.AudioListener;

                case Camera _: return BasisComponentKind.Camera;
                case Light _: return BasisComponentKind.Light;
                case ReflectionProbe _: return BasisComponentKind.ReflectionProbe;
                case LODGroup _: return BasisComponentKind.LODGroup;
                case ParticleSystem _: return BasisComponentKind.ParticleSystem;
                case UnityEngine.VFX.VisualEffect _: return BasisComponentKind.VisualEffect;

                case CanvasRenderer _: return BasisComponentKind.CanvasRenderer;
                case CanvasGroup _: return BasisComponentKind.CanvasGroup;
                case Canvas _: return BasisComponentKind.Canvas;
                case TMPro.TMP_Text _: return BasisComponentKind.TextMeshProText;

                case UnityEngine.Video.VideoPlayer _: return BasisComponentKind.VideoPlayer;
                case UnityEngine.AI.NavMeshAgent _: return BasisComponentKind.NavMeshAgent;
                case UnityEngine.AI.NavMeshObstacle _: return BasisComponentKind.NavMeshObstacle;
                case Terrain _: return BasisComponentKind.Terrain;
                case UnityEngine.Tilemaps.Tilemap _: return BasisComponentKind.Tilemap;

                case Animator _: return BasisComponentKind.Animator;
                case Animation _: return BasisComponentKind.Animation;

                case RectTransform _: return BasisComponentKind.RectTransform;
                case Transform _: return BasisComponentKind.Transform;
                default: return BasisComponentKind.Other;
            }
        }

        /// <summary>
        /// Builds a full harvest from a live hierarchy walk. Backwards-compat path for content that
        /// reaches a consumer without one (older bundles, spawns that bypass the content police):
        /// populates Components, Kinds, and the renderer buckets so callers get the same shape
        /// ContentPoliceControl produces.
        /// </summary>
        public static BasisContentHarvest BuildFrom(GameObject root, bool includeInactive = true)
        {
            Component[] components = root.GetComponentsInChildren<Component>(includeInactive);
            int count = components.Length;
            BasisContentHarvest harvest = new BasisContentHarvest
            {
                Components = components,
                Kinds = new BasisComponentKind[count],
                Renderers = new List<Renderer>(),
                SkinnedMeshRenderers = new List<SkinnedMeshRenderer>(),
                AuthoredMotions = new List<BasisAuthoredMotion>(),
            };
            for (int i = 0; i < count; i++)
            {
                Component component = components[i];
                harvest.Kinds[i] = Classify(component);
                if (component is Renderer renderer)
                {
                    harvest.Renderers.Add(renderer);
                    if (renderer is SkinnedMeshRenderer skinned)
                    {
                        harvest.SkinnedMeshRenderers.Add(skinned);
                    }
                }
                if (component is BasisAuthoredMotion authored)
                {
                    harvest.AuthoredMotions.Add(authored);
                }
            }
            return harvest;
        }

        /// <summary>
        /// Reinterprets <c>Components[i]</c> as <typeparamref name="T"/> with no runtime type check.
        /// The caller MUST have matched <c>Kinds[i]</c> to <typeparamref name="T"/> first and must
        /// never call this on an <see cref="BasisComponentKind.Other"/> or
        /// <see cref="BasisComponentKind.Removed"/> slot. Unchecked — a wrong tag is memory-unsafe —
        /// and does not test for a destroyed component, so only use it while the snapshot is unmutated.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeAs<T>(int i) where T : Component
        {
            return Unsafe.As<T>(Components[i]);
        }

        /// <summary>
        /// Null-checked typed access: returns false for <see cref="BasisComponentKind.Removed"/> slots
        /// and for components destroyed after the walk (deferred Destroy, perf trim); otherwise
        /// reinterprets via the tag without an is-chain. Use this when the snapshot may have developed
        /// holes between harvest and consumption.
        /// </summary>
        public bool TryGet<T>(int i, out T value) where T : Component
        {
            Component component = Components[i];
            if (Kinds[i] == BasisComponentKind.Removed || component == null)
            {
                value = null;
                return false;
            }
            value = Unsafe.As<T>(component);
            return true;
        }
    }
}
