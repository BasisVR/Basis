using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Shared authoring surface for the Basis constraint components — the drop-in replacement for
    /// Unity's built-in <c>UnityEngine.Animations</c> constraints (<c>PositionConstraint</c>,
    /// <c>RotationConstraint</c>, and friends). The public shape deliberately mirrors Unity's
    /// <c>IConstraint</c> (<see cref="weight"/>, <see cref="constraintActive"/>, <see cref="locked"/>,
    /// <c>AddSource</c> / <c>GetSource</c> / <c>SetSource</c> / <c>RemoveSource</c>) so porting a rig
    /// is a component swap rather than a rewrite.
    ///
    /// Data-only, exactly like <see cref="BasisAuthoredMotion"/>: there is no per-instance
    /// <c>Update</c>. Every enabled component is folded into one batched, Burst-compiled solve in
    /// the framework-side <c>BasisConstraintSystem</c>, which is why this type lives in the SDK
    /// assembly and speaks only plain Unity types — the SDK cannot see the framework.
    ///
    /// The framework subscribes to <see cref="ActiveStateChanged"/> and <see cref="StructureChanged"/>
    /// once at startup; components announce themselves across the assembly boundary through those
    /// statics rather than holding a reference to any system.
    ///
    /// Scalar state (weight, offsets, masks, at-rest pose) is re-read from the component every frame,
    /// so tweaking it live — in the inspector or from script — just works. Changing the *shape* of
    /// <see cref="sources"/> is a structural edit: go through the source API below, or call
    /// <see cref="SetDirty"/> if you mutate the serialized list directly.
    /// </summary>
    public abstract class BasisConstraintBase : MonoBehaviour
    {
        /// <summary>
        /// Raised when any constraint component is enabled (<c>true</c>) or disabled (<c>false</c>),
        /// including on destroy. Static because the batched solver lives in an assembly this one
        /// cannot reference; it subscribes once and never needs a per-frame poll.
        /// </summary>
        public static event Action<BasisConstraintBase, bool> ActiveStateChanged;

        /// <summary>
        /// Raised when a component's source list changes shape, so the solver rebuilds its flattened
        /// containers. Scalar edits do not need this — they are picked up every frame.
        /// </summary>
        public static event Action<BasisConstraintBase> StructureChanged;

        [Tooltip("Evaluate this constraint. Cleared, the transform is left entirely alone.")]
        public bool constraintActive = true;

        [Tooltip("Blend between the at-rest pose (0) and the fully constrained pose (1).")]
        [Range(0f, 1f)]
        public float weight = 1f;

        [Tooltip("Locked keeps the axes this constraint does not drive at their live pose. Unlocked " +
                 "returns those axes to the captured rest pose instead, which also lets you re-pose " +
                 "the transform in the editor to author offsets.")]
        public bool locked = true;

        /// <summary>
        /// Where this sits in the sequence the content was authored in. Baked at conversion and left
        /// alone afterwards; int.MaxValue means unset, and ordering falls back to hierarchy depth.
        /// </summary>
        [HideInInspector] public int authoredOrder = int.MaxValue;

        [SerializeField]
        [Tooltip("Transforms driving this constraint. Weights are relative, not normalized.")]
        private List<BasisConstraintSourceEntry> sources = new List<BasisConstraintSourceEntry>();

        /// <summary>Guards double-add / double-remove across enable cycles.</summary>
        [NonSerialized] private bool announced;

        /// <summary>Which solver path this component takes.</summary>
        public abstract BasisConstraintType constraintType { get; }

        /// <summary>Number of source transforms driving this constraint.</summary>
        public int sourceCount => sources.Count;

        /// <summary>
        /// Direct indexed read for the solver's flatten pass. Indexing avoids the enumerator
        /// allocation a <c>foreach</c> over the interface would cost on every rebuild.
        /// </summary>
        public IReadOnlyList<BasisConstraintSourceEntry> Sources => sources;

        protected virtual void OnEnable() => OnInitialize();
        protected virtual void OnDisable() => OnRemove();

        /// <summary>
        /// Announce this constraint to the solver. Factored out of <c>OnEnable</c> so a host that
        /// drives lifecycle manually (avatar calibration, pooling) can register without toggling
        /// <see cref="Behaviour.enabled"/>. Idempotent.
        /// </summary>
        public void OnInitialize()
        {
            if (announced)
            {
                return;
            }
            announced = true;
            ActiveStateChanged?.Invoke(this, true);
        }

        /// <summary>Withdraw this constraint from the solver. Idempotent; safe to call unregistered.</summary>
        public void OnRemove()
        {
            if (!announced)
            {
                return;
            }
            announced = false;
            ActiveStateChanged?.Invoke(this, false);
        }

        /// <summary>
        /// Flag the source list as structurally changed. Only needed when you mutate the serialized
        /// list behind the source API's back; every method below already calls it.
        /// </summary>
        public void SetDirty()
        {
            if (announced)
            {
                StructureChanged?.Invoke(this);
            }
        }

        public BasisConstraintSourceEntry GetSource(int index) => sources[index];

        /// <summary>
        /// Replace a source. Only a change of <c>sourceTransform</c> forces a rebuild — retargeting
        /// weight alone rides the per-frame scalar refresh.
        /// </summary>
        public void SetSource(int index, BasisConstraintSourceEntry source)
        {
            Transform previous = sources[index].sourceTransform;
            sources[index] = source;
            if (previous != source.sourceTransform)
            {
                SetDirty();
            }
        }

        /// <summary>Copy the sources into <paramref name="results"/>, clearing it first (Unity's shape).</summary>
        public void GetSources(List<BasisConstraintSourceEntry> results)
        {
            if (results == null)
            {
                return;
            }
            results.Clear();
            results.AddRange(sources);
        }

        /// <summary>
        /// Replace the source list. Handing back the same transforms is a weight edit, not a
        /// structural one — it rides the per-frame refresh like <see cref="SetSource"/>, so a script
        /// calling this every frame in the Unity style does not force a global rebuild each time.
        /// </summary>
        public void SetSources(List<BasisConstraintSourceEntry> newSources)
        {
            if (newSources != null && newSources.Count == sources.Count)
            {
                bool sameTransforms = true;
                for (int Index = 0; Index < sources.Count; Index++)
                {
                    if (sources[Index].sourceTransform != newSources[Index].sourceTransform)
                    {
                        sameTransforms = false;
                        break;
                    }
                }
                if (sameTransforms)
                {
                    for (int Index = 0; Index < sources.Count; Index++)
                    {
                        sources[Index] = newSources[Index];
                    }
                    return;
                }
            }

            sources.Clear();
            if (newSources != null)
            {
                sources.AddRange(newSources);
            }
            OnSourcesReshaped();
            SetDirty();
        }

        /// <summary>Append a source and return its index.</summary>
        public int AddSource(BasisConstraintSourceEntry source)
        {
            sources.Add(source);
            OnSourcesReshaped();
            SetDirty();
            return sources.Count - 1;
        }

        public void RemoveSource(int index)
        {
            sources.RemoveAt(index);
            OnSourcesReshaped();
            SetDirty();
        }

        public void ClearSources()
        {
            sources.Clear();
            OnSourcesReshaped();
            SetDirty();
        }

        /// <summary>
        /// Hook for kinds carrying per-source data alongside the list — <see cref="BasisParentConstraint"/>
        /// keeps its offset arrays the same length here.
        /// </summary>
        protected virtual void OnSourcesReshaped()
        {
        }

        /// <summary>
        /// Capture the transform's current local pose as the at-rest pose — the pose the constraint
        /// blends back to at weight 0.
        /// </summary>
        [ContextMenu("Capture Rest From Current Pose")]
        public abstract void CaptureRest();

        protected virtual void Reset()
        {
            CaptureRest();
        }

        protected virtual void OnValidate()
        {
            weight = Mathf.Clamp01(weight);
            for (int Index = 0; Index < sources.Count; Index++)
            {
                BasisConstraintSourceEntry source = sources[Index];
                if (source.weight < 0f)
                {
                    source.weight = 0f;
                    sources[Index] = source;
                }
            }
            OnSourcesReshaped();
        }
    }
}
