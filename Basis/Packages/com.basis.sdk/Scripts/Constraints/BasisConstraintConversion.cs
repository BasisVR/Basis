using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using Object = UnityEngine.Object;

namespace Basis.Scripts.BasisSdk.Constraints
{
    /// <summary>
    /// Rewrites Unity's built-in constraints and the Animation Rigging package's constraints onto the
    /// Basis equivalents, then removes the originals along with the rig that drove them.
    ///
    /// This is what lets constrained content run at all on a remote avatar. Animation Rigging needs an
    /// Animator and a PlayableGraph per rig, and remotes have neither — their bones come off the
    /// network. Unity's built-in constraints do run without an Animator, but each one costs its own
    /// component update and its own scattered transform reads, every frame, on every avatar in the
    /// instance. Everything converted here ends up in one batched Burst job instead.
    ///
    /// Conversion runs while the content is still parked inactive, so the originals never evaluate
    /// even once and there is no first-frame pop. Nothing here logs per component: a busy instance
    /// converts hundreds of these at a time, and the exception notifier fans errors out to disk and
    /// the network.
    /// </summary>
    public static class BasisConstraintConversion
    {
        /// <summary>What a single conversion pass did, for the caller to log once in summary.</summary>
        public struct Report
        {
            public int Converted;
            public int Removed;
            public int Unsupported;
            /// <summary>Bindings elsewhere that named a converted constraint and were repointed.</summary>
            public int Rebound;

            public bool DidAnything =>
                Converted > 0 || Removed > 0 || Unsupported > 0 || Rebound > 0;

            public override string ToString() =>
                $"converted {Converted}, removed {Removed}, unsupported {Unsupported}, rebound {Rebound}";
        }

        static readonly List<Component> ComponentScratch = new List<Component>();
        static readonly List<Transform> ChainScratch = new List<Transform>();
        static readonly List<ConstraintSource> SourceScratch = new List<ConstraintSource>();

        /// <summary>
        /// Converts one component if it is something we replace, reporting whether the caller should
        /// now destroy it. Built to be called from a walk the caller is already doing — Content Police
        /// visits every component on load anyway, and walking the hierarchy a second time to find
        /// constraints would cost more than the conversion does.
        ///
        /// Returning true always means "destroy this", including for the rig scaffolding, which has
        /// nothing to convert but must not outlive the constraints it drove.
        /// </summary>
        public static bool TryConvert(Component component, ref Report report)
        {
            switch (component)
            {
                // Rig scaffolding: no equivalent and nothing to carry over, it just goes. Order
                // against the constraints does not matter here because the content is still inactive,
                // so RigBuilder never enabled and never built a PlayableGraph to tear down.
                case RigBuilder:
                case Rig:
                case RigTransform:
                case BoneRenderer:
                    report.Removed++;
                    return true;

                case ParentConstraint parent: return Finish(ConvertParent(parent), ref report);
                case PositionConstraint position: return Finish(ConvertPosition(position), ref report);
                case RotationConstraint rotation: return Finish(ConvertRotation(rotation), ref report);
                case ScaleConstraint scale: return Finish(ConvertScale(scale), ref report);
                case AimConstraint aim: return Finish(ConvertAim(aim), ref report);
                case LookAtConstraint lookAt: return Finish(ConvertLookAt(lookAt), ref report);

                case MultiParentConstraint p: return Finish(ConvertMultiParent(p), ref report);
                case MultiPositionConstraint p: return Finish(ConvertMultiPosition(p), ref report);
                case MultiRotationConstraint r: return Finish(ConvertMultiRotation(r), ref report);
                case MultiAimConstraint a: return Finish(ConvertMultiAim(a), ref report);
                case MultiReferentialConstraint r: return Finish(ConvertMultiReferential(r), ref report);
                case TwoBoneIKConstraint t: return Finish(ConvertTwoBoneIK(t), ref report);
                case ChainIKConstraint c: return Finish(ConvertChainIK(c), ref report);
                case DampedTransform d: return Finish(ConvertDamped(d), ref report);
                case TwistCorrection t: return Finish(ConvertTwistCorrection(t), ref report);
                case BlendConstraint b: return Finish(ConvertBlend(b), ref report);
                case OverrideTransform o: return Finish(ConvertOverride(o), ref report);
                case TwistChainConstraint t: return Finish(ConvertTwistChain(t), ref report);

                default:
                    // Anything that drives a constraint by type name has to be repointed at the
                    // replacement. Vixxy is the one that does this today: it stores the component
                    // type as a string and resolves it at runtime, so once the original is gone its
                    // binding finds nothing and the toggle silently stops working.
                    RemapExternalBindings(component, ref report);
                    return false;
            }
        }

        /// <summary>Old constraint type name to new, for bindings that name one as a string.</summary>
        static readonly Dictionary<string, string> BindingRemap = new Dictionary<string, string>
        {
            { "UnityEngine.Animations.ParentConstraint", Prefix + "BasisParentConstraint" },
            { "UnityEngine.Animations.PositionConstraint", Prefix + "BasisPositionConstraint" },
            { "UnityEngine.Animations.RotationConstraint", Prefix + "BasisRotationConstraint" },
            { "UnityEngine.Animations.ScaleConstraint", Prefix + "BasisScaleConstraint" },
            { "UnityEngine.Animations.AimConstraint", Prefix + "BasisAimConstraint" },
            { "UnityEngine.Animations.LookAtConstraint", Prefix + "BasisLookAtConstraint" },
            { "UnityEngine.Animations.Rigging.MultiParentConstraint", Prefix + "BasisParentConstraint" },
            { "UnityEngine.Animations.Rigging.MultiPositionConstraint", Prefix + "BasisPositionConstraint" },
            { "UnityEngine.Animations.Rigging.MultiRotationConstraint", Prefix + "BasisRotationConstraint" },
            { "UnityEngine.Animations.Rigging.MultiAimConstraint", Prefix + "BasisAimConstraint" },
            { "UnityEngine.Animations.Rigging.TwoBoneIKConstraint", Prefix + "BasisTwoBoneIK" },
            { "UnityEngine.Animations.Rigging.ChainIKConstraint", Prefix + "BasisChainIK" },
            { "UnityEngine.Animations.Rigging.DampedTransform", Prefix + "BasisDampedTransform" },
            { "UnityEngine.Animations.Rigging.TwistCorrection", Prefix + "BasisTwistCorrection" },
            { "UnityEngine.Animations.Rigging.TwistChainConstraint", Prefix + "BasisTwistChain" },
            { "UnityEngine.Animations.Rigging.BlendConstraint", Prefix + "BasisBlendConstraint" },
            { "UnityEngine.Animations.Rigging.OverrideTransform", Prefix + "BasisOverrideTransform" },
        };

        const string Prefix = "Basis.Scripts.BasisSdk.Constraints.";

        /// <summary>Components created by the conversion currently in flight, awaiting their order stamp.</summary>
        static readonly List<BasisConstraintBase> PendingOrderScratch = new List<BasisConstraintBase>();
        static int sAuthoredSequence;

        /// <summary>
        /// Hands every component the conversion just produced its place in the authored sequence.
        /// One source constraint can produce several (a twist chain becomes one per bone), and they
        /// all belong at the same point in the order, so they share the number.
        /// </summary>
        static void StampAuthoredOrder()
        {
            int order = sAuthoredSequence++;
            for (int Index = 0; Index < PendingOrderScratch.Count; Index++)
            {
                PendingOrderScratch[Index].authoredOrder = order;
            }
            PendingOrderScratch.Clear();
        }

        /// <summary>Every constraint the conversion adds goes through here so none miss the stamp.</summary>
        static T Add<T>(GameObject host) where T : BasisConstraintBase
        {
            T created = host.AddComponent<T>();
            PendingOrderScratch.Add(created);
            return created;
        }

        const BindingFlags SerializedFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly HashSet<object> VisitedScratch = new HashSet<object>();

        /// <summary>
        /// Repoints any serialized type name that named a constraint we replaced. Driven by
        /// reflection rather than against Vixxy's own types, so this needs no assembly reference on a
        /// third-party package and becomes a no-op if that package is not installed.
        ///
        /// Only the type name moves. The Basis components deliberately carry Unity's field names, so
        /// whatever property the binding was driving still resolves under the same name.
        /// </summary>
        static void RemapExternalBindings(Component component, ref Report report)
        {
            // Cheap gate first: this runs for every component that is not a constraint.
            Type type = component.GetType();
            if (type.FullName == null || !type.FullName.StartsWith("HVR.", StringComparison.Ordinal))
            {
                return;
            }

            VisitedScratch.Clear();
            report.Rebound += RemapIn(component, 0);
            VisitedScratch.Clear();
        }

        /// <summary>
        /// Walks serialized data for a string naming a constraint we replaced. Depth is capped and
        /// visits are tracked because this is authored data, not a graph worth recursing forever over.
        /// </summary>
        static int RemapIn(object instance, int depth)
        {
            if (instance == null || depth > 6 || !VisitedScratch.Add(instance))
            {
                return 0;
            }

            int rewritten = 0;
            FieldInfo[] fields = instance.GetType().GetFields(SerializedFields);
            for (int Index = 0; Index < fields.Length; Index++)
            {
                FieldInfo field = fields[Index];
                Type fieldType = field.FieldType;

                if (fieldType == typeof(string))
                {
                    if (field.GetValue(instance) is string name
                        && BindingRemap.TryGetValue(name, out string replacement))
                    {
                        field.SetValue(instance, replacement);
                        rewritten++;
                    }
                    continue;
                }

                // Never follow a reference to another object in the scene: the binding data lives in
                // plain serialized structs and lists hanging off this component.
                if (fieldType.IsPrimitive || typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
                {
                    continue;
                }

                object nested = field.GetValue(instance);
                if (nested == null)
                {
                    continue;
                }

                if (nested is IEnumerable sequence)
                {
                    foreach (object element in sequence)
                    {
                        rewritten += RemapIn(element, depth + 1);
                    }
                    continue;
                }

                rewritten += RemapIn(nested, depth + 1);
            }
            return rewritten;
        }

        /// <summary>
        /// Counts the outcome and asks for the original to go either way. A constraint we could not
        /// convert still cannot be left behind: it would drive the same transform the replacement
        /// does, or keep costing a per-frame update for a rig that is no longer there.
        /// </summary>
        static bool Finish(bool converted, ref Report report)
        {
            // Bake the sequence as we meet it. The police walk visits components in the same
            // depth-first order Animation Rigging builds a rig in, so counting them off here carries
            // the authored evaluation order across — computed once, then never derived again.
            StampAuthoredOrder();
            if (converted)
            {
                report.Converted++;
            }
            else
            {
                report.Unsupported++;
            }
            report.Removed++;
            return true;
        }

        /// <summary>
        /// Converts everything under <paramref name="root"/> in a single walk. For callers that are
        /// not already walking the hierarchy; Content Police should use <see cref="TryConvert"/>
        /// inside its own pass instead.
        /// </summary>
        public static Report ConvertHierarchy(GameObject root)
        {
            Report report = default;
            if (root == null)
            {
                return report;
            }

            root.GetComponentsInChildren(true, ComponentScratch);
            for (int Index = 0; Index < ComponentScratch.Count; Index++)
            {
                Component component = ComponentScratch[Index];
                if (component != null && TryConvert(component, ref report))
                {
                    Object.DestroyImmediate(component);
                }
            }
            ComponentScratch.Clear();
            return report;
        }

        // ── Unity's built-in constraints ──────────────────────────────────────────────────────────

        static bool ConvertParent(ParentConstraint source)
        {
            BasisParentConstraint target = Add<BasisParentConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.translationAtRest = source.translationAtRest;
            target.rotationAtRest = source.rotationAtRest;
            target.translationAxis = ToAxes(source.translationAxis);
            target.rotationAxis = ToAxes(source.rotationAxis);
            CopySources(source, target);

            // Unity keeps per-source offsets in parallel arrays indexed by source order.
            Vector3[] translationOffsets = source.translationOffsets;
            Vector3[] rotationOffsets = source.rotationOffsets;
            target.translationOffsets = translationOffsets;
            target.rotationOffsets = rotationOffsets;
            return true;
        }

        static bool ConvertPosition(PositionConstraint source)
        {
            BasisPositionConstraint target = Add<BasisPositionConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.translationAtRest = source.translationAtRest;
            target.translationOffset = source.translationOffset;
            target.translationAxis = ToAxes(source.translationAxis);
            CopySources(source, target);
            return true;
        }

        static bool ConvertRotation(RotationConstraint source)
        {
            BasisRotationConstraint target = Add<BasisRotationConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.rotationAtRest = source.rotationAtRest;
            target.rotationOffset = source.rotationOffset;
            target.rotationAxis = ToAxes(source.rotationAxis);
            CopySources(source, target);
            return true;
        }

        static bool ConvertScale(ScaleConstraint source)
        {
            BasisScaleConstraint target = Add<BasisScaleConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.scaleAtRest = source.scaleAtRest;
            target.scaleOffset = source.scaleOffset;
            target.scalingAxis = ToAxes(source.scalingAxis);
            CopySources(source, target);
            return true;
        }

        static bool ConvertAim(AimConstraint source)
        {
            BasisAimConstraint target = Add<BasisAimConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.rotationAtRest = source.rotationAtRest;
            target.rotationOffset = source.rotationOffset;
            target.rotationAxis = ToAxes(source.rotationAxis);
            target.aimVector = source.aimVector;
            target.upVector = source.upVector;
            target.worldUpVector = source.worldUpVector;
            target.worldUpObject = source.worldUpObject;
            target.worldUpType = (BasisConstraintWorldUp)(int)source.worldUpType;
            CopySources(source, target);
            return true;
        }

        static bool ConvertLookAt(LookAtConstraint source)
        {
            BasisLookAtConstraint target = Add<BasisLookAtConstraint>(source.gameObject);
            CopyCommon(source, target);
            target.rotationAtRest = source.rotationAtRest;
            target.roll = source.roll;
            target.worldUpObject = source.worldUpObject;
            target.useUpObject = source.useUpObject;
            CopySources(source, target);
            return true;
        }

        // ── Animation Rigging ─────────────────────────────────────────────────────────────────────

        static bool ConvertMultiParent(MultiParentConstraint source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null)
            {
                return false;
            }
            BasisParentConstraint target = Add<BasisParentConstraint>(constrained.gameObject);
            target.weight = source.weight;
            target.translationAxis = ToAxes(source.data.constrainedPositionXAxis,
                source.data.constrainedPositionYAxis, source.data.constrainedPositionZAxis);
            target.rotationAxis = ToAxes(source.data.constrainedRotationXAxis,
                source.data.constrainedRotationYAxis, source.data.constrainedRotationZAxis);
            CopyWeighted(source.data.sourceObjects, target);
            target.CaptureRest();
            if (source.data.maintainPositionOffset || source.data.maintainRotationOffset)
            {
                target.CaptureOffsets();
            }
            return true;
        }

        static bool ConvertMultiPosition(MultiPositionConstraint source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null)
            {
                return false;
            }
            BasisPositionConstraint target = Add<BasisPositionConstraint>(constrained.gameObject);
            target.weight = source.weight;
            target.translationOffset = source.data.offset;
            target.translationAxis = ToAxes(source.data.constrainedXAxis,
                source.data.constrainedYAxis, source.data.constrainedZAxis);
            CopyWeighted(source.data.sourceObjects, target);
            target.CaptureRest();
            return true;
        }

        static bool ConvertMultiRotation(MultiRotationConstraint source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null)
            {
                return false;
            }
            BasisRotationConstraint target = Add<BasisRotationConstraint>(constrained.gameObject);
            target.weight = source.weight;
            target.rotationOffset = source.data.offset;
            target.rotationAxis = ToAxes(source.data.constrainedXAxis,
                source.data.constrainedYAxis, source.data.constrainedZAxis);
            CopyWeighted(source.data.sourceObjects, target);
            target.CaptureRest();
            return true;
        }

        static bool ConvertMultiAim(MultiAimConstraint source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null)
            {
                return false;
            }
            BasisAimConstraint target = Add<BasisAimConstraint>(constrained.gameObject);
            target.weight = source.weight;
            // Axes are an enum on the concrete data and vectors only through the interface.
            IMultiAimConstraintData axes = source.data;
            target.aimVector = axes.aimAxis;
            target.upVector = axes.upAxis;
            target.worldUpVector = axes.worldUpAxis;
            target.worldUpObject = source.data.worldUpObject;
            target.worldUpType = (BasisConstraintWorldUp)(int)source.data.worldUpType;
            target.rotationOffset = source.data.offset;
            target.rotationAxis = ToAxes(source.data.constrainedXAxis,
                source.data.constrainedYAxis, source.data.constrainedZAxis);
            CopyWeighted(source.data.sourceObjects, target);
            target.CaptureRest();
            return true;
        }

        /// <summary>
        /// Every non-driver keeps its pose relative to the driver, which is a parent constraint on
        /// each of them with the offset baked. The driver itself is left alone, as Unity leaves it.
        /// The driver index is frozen at conversion; Animation Rigging lets it change at runtime.
        /// </summary>
        static bool ConvertMultiReferential(MultiReferentialConstraint source)
        {
            List<Transform> sources = source.data.sourceObjects;
            if (sources == null || sources.Count < 2)
            {
                return false;
            }
            for (int Index = 0; Index < sources.Count; Index++)
            {
                if (sources[Index] == null)
                {
                    return false;
                }
            }

            // One constraint holding the whole set, rather than a follow-the-leader constraint on
            // each non-driver: the driver index stays a live value that can change at runtime, the
            // way Animation Rigging allows, instead of being baked in at conversion.
            Transform host = sources[0];
            BasisMultiReferential target = Add<BasisMultiReferential>(host.gameObject);
            target.weight = source.weight;
            target.members = new List<Transform>(sources);
            target.driver = Mathf.Clamp(source.data.driver, 0, sources.Count - 1);
            target.CaptureRest();
            return true;
        }

        static bool ConvertTwoBoneIK(TwoBoneIKConstraint source)
        {
            Transform tip = source.data.tip;
            if (tip == null || source.data.mid == null || source.data.root == null)
            {
                return false;
            }
            BasisTwoBoneIK target = Add<BasisTwoBoneIK>(tip.gameObject);
            target.weight = source.weight;
            target.mid = source.data.mid;
            target.root = source.data.root;
            target.targetPositionWeight = source.data.targetPositionWeight;
            target.targetRotationWeight = source.data.targetRotationWeight;
            target.hintWeight = source.data.hintWeight;
            target.maintainTargetOffset =
                source.data.maintainTargetPositionOffset || source.data.maintainTargetRotationOffset;

            if (source.data.target != null)
            {
                target.AddSource(new BasisConstraintSourceEntry(source.data.target, 1f));
            }
            if (source.data.hint != null)
            {
                target.AddSource(new BasisConstraintSourceEntry(source.data.hint, 1f));
            }
            target.CaptureRest();
            return source.data.target != null;
        }

        static bool ConvertChainIK(ChainIKConstraint source)
        {
            Transform tip = source.data.tip;
            if (tip == null || source.data.root == null || source.data.target == null)
            {
                return false;
            }
            BasisChainIK target = Add<BasisChainIK>(tip.gameObject);
            target.weight = source.weight;
            target.root = source.data.root;
            target.chainRotationWeight = source.data.chainRotationWeight;
            target.tipRotationWeight = source.data.tipRotationWeight;
            target.tolerance = source.data.tolerance;
            target.maxIterations = source.data.maxIterations;
            target.AddSource(new BasisConstraintSourceEntry(source.data.target, 1f));
            return true;
        }

        static bool ConvertDamped(DampedTransform source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null || source.data.sourceObject == null)
            {
                return false;
            }
            BasisDampedTransform target = Add<BasisDampedTransform>(constrained.gameObject);
            target.weight = source.weight;
            target.dampPosition = source.data.dampPosition;
            target.dampRotation = source.data.dampRotation;
            target.maintainAim = source.data.maintainAim;
            target.AddSource(new BasisConstraintSourceEntry(source.data.sourceObject, 1f));
            target.CaptureRest();
            return true;
        }

        /// <summary>
        /// Animation Rigging drives a whole list of twist nodes from one component; Basis drives one
        /// transform per constraint, so this becomes one constraint per node carrying that node's own
        /// share of the twist.
        /// </summary>
        static bool ConvertTwistCorrection(TwistCorrection source)
        {
            Transform twistSource = source.data.sourceObject;
            WeightedTransformArray nodes = source.data.twistNodes;
            if (twistSource == null || nodes.Count == 0)
            {
                return false;
            }

            BasisTwistCorrection.TwistAxis axis = source.data.twistAxis switch
            {
                TwistCorrectionData.Axis.Y => BasisTwistCorrection.TwistAxis.Y,
                TwistCorrectionData.Axis.Z => BasisTwistCorrection.TwistAxis.Z,
                _ => BasisTwistCorrection.TwistAxis.X,
            };

            bool any = false;
            for (int Index = 0; Index < nodes.Count; Index++)
            {
                WeightedTransform node = nodes[Index];
                if (node.transform == null)
                {
                    continue;
                }
                BasisTwistCorrection target = Add<BasisTwistCorrection>(node.transform.gameObject);
                target.weight = source.weight;
                target.twistAxis = axis;
                target.twistWeight = node.weight;
                target.AddSource(new BasisConstraintSourceEntry(twistSource, 1f));
                target.CaptureRest();
                any = true;
            }
            return any;
        }

        static bool ConvertBlend(BlendConstraint source)
        {
            Transform constrained = source.data.constrainedObject;
            Transform a = source.data.sourceObjectA;
            Transform b = source.data.sourceObjectB;
            if (constrained == null || a == null || b == null)
            {
                return false;
            }
            BasisBlendConstraint target = Add<BasisBlendConstraint>(constrained.gameObject);
            target.weight = source.weight;
            target.blendPosition = source.data.blendPosition;
            target.blendRotation = source.data.blendRotation;
            target.positionWeight = source.data.positionWeight;
            target.rotationWeight = source.data.rotationWeight;
            target.AddSource(new BasisConstraintSourceEntry(a, 1f));
            target.AddSource(new BasisConstraintSourceEntry(b, 1f));
            target.CaptureRest();
            return true;
        }

        static bool ConvertOverride(OverrideTransform source)
        {
            Transform constrained = source.data.constrainedObject;
            if (constrained == null)
            {
                return false;
            }
            BasisOverrideTransform target = Add<BasisOverrideTransform>(constrained.gameObject);
            target.weight = source.weight;
            target.position = source.data.position;
            target.rotation = source.data.rotation;
            target.positionWeight = source.data.positionWeight;
            target.rotationWeight = source.data.rotationWeight;
            target.space = (BasisOverrideTransform.Space)source.data.space;

            if (source.data.sourceObject != null)
            {
                target.useSource = true;
                target.AddSource(new BasisConstraintSourceEntry(source.data.sourceObject, 1f));
                target.CaptureSourceBind();
            }
            return true;
        }

        /// <summary>
        /// Walks the chain root-to-tip and gives each bone its own constraint, sampling the curve at
        /// that bone's distance along the chain — the same thing Animation Rigging does once at bind,
        /// which is what keeps a managed AnimationCurve out of the solve entirely.
        /// </summary>
        static bool ConvertTwistChain(TwistChainConstraint source)
        {
            Transform root = source.data.root;
            Transform tip = source.data.tip;
            Transform rootTarget = source.data.rootTarget;
            Transform tipTarget = source.data.tipTarget;
            if (root == null || tip == null || rootTarget == null || tipTarget == null)
            {
                return false;
            }

            // Tip upward to root, then reverse: the chain is only well defined if root is an ancestor.
            ChainScratch.Clear();
            Transform cursor = tip;
            while (cursor != null)
            {
                ChainScratch.Add(cursor);
                if (cursor == root)
                {
                    break;
                }
                cursor = cursor.parent;
            }
            if (ChainScratch.Count < 2 || ChainScratch[ChainScratch.Count - 1] != root)
            {
                ChainScratch.Clear();
                return false;
            }
            ChainScratch.Reverse();

            // Distance along the chain, normalised, so the curve is read in the same place Animation
            // Rigging reads it rather than by bone index.
            float total = 0f;
            for (int Index = 1; Index < ChainScratch.Count; Index++)
            {
                total += Vector3.Distance(
                    ChainScratch[Index].position, ChainScratch[Index - 1].position);
            }

            AnimationCurve curve = source.data.curve;
            float travelled = 0f;
            for (int Index = 0; Index < ChainScratch.Count; Index++)
            {
                if (Index > 0)
                {
                    travelled += Vector3.Distance(
                        ChainScratch[Index].position, ChainScratch[Index - 1].position);
                }
                float step = total > 0f ? travelled / total : 0f;

                BasisTwistChain target = Add<BasisTwistChain>(ChainScratch[Index].gameObject);
                target.weight = source.weight;
                // The ends are pinned to their own targets; only the bones between them read the curve.
                target.blend = Index == 0 ? 0f
                    : Index == ChainScratch.Count - 1 ? 1f
                    : Mathf.Clamp01(curve?.Evaluate(step) ?? step);
                target.AddSource(new BasisConstraintSourceEntry(rootTarget, 1f));
                target.AddSource(new BasisConstraintSourceEntry(tipTarget, 1f));
                target.CaptureRest();
            }
            ChainScratch.Clear();
            return true;
        }

        // ── Strip what drove them ─────────────────────────────────────────────────────────────────

        // ── Shared ────────────────────────────────────────────────────────────────────────────────

        static void CopyCommon(IConstraint source, BasisConstraintBase target)
        {
            target.weight = source.weight;
            target.constraintActive = source.constraintActive;
            target.locked = source.locked;
        }

        static void CopySources(IConstraint source, BasisConstraintBase target)
        {
            SourceScratch.Clear();
            source.GetSources(SourceScratch);
            for (int Index = 0; Index < SourceScratch.Count; Index++)
            {
                ConstraintSource entry = SourceScratch[Index];
                if (entry.sourceTransform != null)
                {
                    target.AddSource(new BasisConstraintSourceEntry(entry.sourceTransform, entry.weight));
                }
            }
            SourceScratch.Clear();
        }

        static void CopyWeighted(WeightedTransformArray sources, BasisConstraintBase target)
        {
            for (int Index = 0; Index < sources.Count; Index++)
            {
                WeightedTransform entry = sources[Index];
                if (entry.transform != null)
                {
                    target.AddSource(new BasisConstraintSourceEntry(entry.transform, entry.weight));
                }
            }
        }

        static BasisConstraintAxes ToAxes(Axis axis)
        {
            BasisConstraintAxes result = BasisConstraintAxes.None;
            if ((axis & Axis.X) != 0) result |= BasisConstraintAxes.X;
            if ((axis & Axis.Y) != 0) result |= BasisConstraintAxes.Y;
            if ((axis & Axis.Z) != 0) result |= BasisConstraintAxes.Z;
            return result;
        }

        static BasisConstraintAxes ToAxes(bool x, bool y, bool z)
        {
            BasisConstraintAxes result = BasisConstraintAxes.None;
            if (x) result |= BasisConstraintAxes.X;
            if (y) result |= BasisConstraintAxes.Y;
            if (z) result |= BasisConstraintAxes.Z;
            return result;
        }
    }
}
