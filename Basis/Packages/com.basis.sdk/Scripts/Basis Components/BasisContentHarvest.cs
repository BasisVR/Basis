using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
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
    /// Holds the raw <see cref="Component"/> list plus a parallel <see cref="BasisComponentKind"/>
    /// tag, letting downstream load steps classify by byte switch instead of re-running
    /// GetComponent / is-chains. Lives on <see cref="BasisContentBase"/> and is transient — the
    /// load path nulls it once consumed.
    /// </summary>
    public sealed partial class BasisContentHarvest
    {
        public List<Component> Components;
        public List<BasisComponentKind> Kinds;
        public List<Renderer> Renderers;
        public List<SkinnedMeshRenderer> SkinnedMeshRenderers;
        public List<BasisAuthoredMotion> AuthoredMotions;

        // Per-type free lists backing Rent()/ReturnToPool(). Main-thread only — the
        // content walk instantiates GameObjects and so can never run off the main
        // thread, which is why these stacks need no locking.
        [AutoStaticsCleanup]
        private static partial class Pool<T>
        {
            private static readonly Stack<List<T>> Free = new Stack<List<T>>();
            public static List<T> Rent() => Free.Count > 0 ? Free.Pop() : new List<T>(64);
            public static void Return(List<T> list)
            {
                if (list == null) return;
                list.Clear();
                Free.Push(list);
            }
        }

        /// <summary>
        /// Builds a harvest with all five collections rented and empty — the shape a full
        /// content-removal walk (or <see cref="BuildFrom"/>) populates. Pair with <see cref="ReturnToPool"/>.
        /// </summary>
        public static BasisContentHarvest Rent()
        {
            BasisContentHarvest harvest = new BasisContentHarvest();
            harvest.RentBuffers();
            return harvest;
        }

        /// <summary>Rents any of the five buffers still null; the content-removal walk fills them all.</summary>
        public void RentBuffers()
        {
            Components ??= Pool<Component>.Rent();
            Kinds ??= Pool<BasisComponentKind>.Rent();
            Renderers ??= Pool<Renderer>.Rent();
            SkinnedMeshRenderers ??= Pool<SkinnedMeshRenderer>.Rent();
            AuthoredMotions ??= Pool<BasisAuthoredMotion>.Rent();
        }

        /// <summary>
        /// Rents only the renderer bucket. The no-content-removal path fills just this and leaves
        /// the other buckets null, so a later <c>EnsureHarvest</c> still rebuilds a full snapshot.
        /// </summary>
        public void RentRenderers()
        {
            Renderers ??= Pool<Renderer>.Rent();
        }

        /// <summary>
        /// Returns the five collections to their pools and nulls them. Call only once every
        /// consumer is done with the snapshot (the load path copies the buckets it keeps via
        /// ToArray first). Safe to skip — the lists then fall back to ordinary GC.
        /// </summary>
        public void ReturnToPool()
        {
            Pool<Component>.Return(Components);
            Pool<BasisComponentKind>.Return(Kinds);
            Pool<Renderer>.Return(Renderers);
            Pool<SkinnedMeshRenderer>.Return(SkinnedMeshRenderers);
            Pool<BasisAuthoredMotion>.Return(AuthoredMotions);
            Components = null;
            Kinds = null;
            Renderers = null;
            SkinnedMeshRenderers = null;
            AuthoredMotions = null;
        }

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
            BasisContentHarvest harvest = Rent();
            List<Component> components = harvest.Components;
            root.GetComponentsInChildren(includeInactive, components);
            int count = components.Count;
            List<BasisComponentKind> kinds = harvest.Kinds;
            for (int i = 0; i < count; i++)
            {
                Component component = components[i];
                kinds.Add(Classify(component));
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
            Component component = Components[i];
            return UnsafeUtility.As<Component, T>(ref component);
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
            value = UnsafeUtility.As<Component, T>(ref component);
            return true;
        }
    }
}
