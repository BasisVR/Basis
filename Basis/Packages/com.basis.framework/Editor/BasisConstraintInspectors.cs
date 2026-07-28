using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Constraints;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Editor.Constraints
{
    /// <summary>
    /// Shared chrome for every Basis constraint inspector, in the same card-and-accent style as the
    /// sync components so the SDK reads as one thing.
    ///
    /// The layout deliberately follows Unity's own constraint inspectors, because that is what people
    /// porting a rig already know: Activate and Zero at the top, then weight, then the source list,
    /// then the per-axis toggles. Activate does what Unity's does — freeze the current pose as the
    /// neutral one and switch the constraint on — which matters more here than it does in Unity,
    /// since several Basis kinds capture a bind rather than only a rest pose.
    /// </summary>
    public abstract class BasisConstraintInspectorBase : UnityEditor.Editor
    {
        protected BasisConstraintBase Constraint => (BasisConstraintBase)target;

        /// <summary>Title shown in the header. Defaults to the type name split on capitals.</summary>
        protected abstract string Title { get; }

        /// <summary>One line under the title saying what the constraint is for.</summary>
        protected abstract string Subtitle { get; }

        /// <summary>Per-kind fields, added under the shared ones.</summary>
        protected virtual void BuildSpecific(VisualElement root, SerializedObject so) { }

        /// <summary>Per-kind authoring problems, surfaced the same way the sync components do.</summary>
        protected virtual void Validate(List<BasisSyncIssue> issues) { }

        public override VisualElement CreateInspectorGUI()
        {
            SerializedObject so = serializedObject;
            VisualElement root = new VisualElement();

            root.Add(BasisSyncInspectorUI.Header(Title, Subtitle));
            root.Add(BasisSyncInspectorUI.ValidationContainer(() =>
            {
                List<BasisSyncIssue> issues = new List<BasisSyncIssue>();
                if (Constraint.sourceCount == 0 && RequiresSources)
                {
                    issues.Add(BasisSyncIssue.Warning(
                        "No sources assigned — this constraint will not drive anything."));
                }
                Validate(issues);
                return issues;
            }));

            root.Add(BuildActions());
            root.Add(BuildCommon(so));
            BuildSpecific(root, so);
            return root;
        }

        /// <summary>False for the kinds that drive from something other than a source list.</summary>
        protected virtual bool RequiresSources => true;

        /// <summary>
        /// Unity's Activate / Zero pair. Activate freezes the pose the constraint should treat as
        /// neutral and switches it on; Zero clears the captured neutral back to identity, which is
        /// what you want when re-authoring from scratch rather than from the current pose.
        /// </summary>
        VisualElement BuildActions()
        {
            VisualElement row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            row.style.marginBottom = 8;

            Button activate = new Button(() =>
            {
                Undo.RecordObject(Constraint, "Activate Constraint");
                Constraint.CaptureRest();
                if (Constraint is BasisParentConstraint parent)
                {
                    parent.CaptureOffsets();
                }
                if (Constraint is BasisOverrideTransform over)
                {
                    over.CaptureSourceBind();
                }
                Constraint.constraintActive = true;
                EditorUtility.SetDirty(Constraint);
            })
            { text = "Activate" };
            activate.style.flexGrow = 1;
            BasisSyncInspectorUI.Described(activate,
                "Freeze the current pose as the neutral one and switch the constraint on. Do this " +
                "with the rig arranged the way it should sit at rest.");

            Button zero = new Button(() =>
            {
                Undo.RecordObject(Constraint, "Zero Constraint");
                ZeroNeutral();
                EditorUtility.SetDirty(Constraint);
            })
            { text = "Zero" };
            zero.style.flexGrow = 1;
            BasisSyncInspectorUI.Described(zero,
                "Clear the captured neutral pose back to identity, for authoring offsets from " +
                "scratch instead of from where things currently sit.");

            row.Add(activate);
            row.Add(zero);
            return row;
        }

        /// <summary>Clears whatever this kind treats as its neutral pose.</summary>
        protected virtual void ZeroNeutral() { }

        VisualElement BuildCommon(SerializedObject so)
        {
            VisualElement card = BasisSyncInspectorUI.Card("Constraint");
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("constraintActive"), "Active"),
                "Cleared, the transform is left entirely alone."));
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("weight"), "Weight"),
                "Blend between the at-rest pose (0) and the fully constrained pose (1)."));
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("locked"), "Locked"),
                "Locked keeps the axes this constraint does not drive at their live pose. Unlocked " +
                "returns them to the captured rest pose."));

            if (RequiresSources)
            {
                VisualElement sources = BasisSyncInspectorUI.Card("Sources");
                sources.Add(BasisSyncInspectorUI.Described(
                    new PropertyField(so.FindProperty("sources"), "Sources"),
                    "Transforms driving this constraint. Weights are relative, not normalised — two " +
                    "sources at 1 each blend 50/50, exactly as Unity's constraints do."));
                card.Add(sources);
            }
            return card;
        }

        /// <summary>A titled card of plain property fields, the common per-kind shape.</summary>
        protected static VisualElement Fields(
            SerializedObject so, string title, params (string property, string label, string tooltip)[] entries)
        {
            VisualElement card = BasisSyncInspectorUI.Card(title);
            for (int Index = 0; Index < entries.Length; Index++)
            {
                (string property, string label, string tooltip) = entries[Index];
                SerializedProperty found = so.FindProperty(property);
                if (found == null)
                {
                    continue;
                }
                card.Add(BasisSyncInspectorUI.Described(new PropertyField(found, label), tooltip));
            }
            return card;
        }
    }

    [CustomEditor(typeof(BasisPositionConstraint))]
    public class BasisPositionConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Position Constraint";
        protected override string Subtitle => "Follows the blended position of its sources.";

        protected override void ZeroNeutral()
        {
            BasisPositionConstraint constraint = (BasisPositionConstraint)target;
            constraint.translationAtRest = Vector3.zero;
            constraint.translationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Position",
                ("translationAtRest", "At Rest", "Local position used when Weight is 0."),
                ("translationOffset", "Offset", "Local-space offset added to the blended source position."),
                ("translationAxis", "Freeze Axes", "Axes this constraint may drive. Excluded axes keep their current value.")));
        }
    }

    [CustomEditor(typeof(BasisRotationConstraint))]
    public class BasisRotationConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Rotation Constraint";
        protected override string Subtitle => "Follows the blended rotation of its sources.";

        protected override void ZeroNeutral()
        {
            BasisRotationConstraint constraint = (BasisRotationConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Rotation",
                ("rotationAtRest", "At Rest", "Local rotation in degrees used when Weight is 0."),
                ("rotationOffset", "Offset", "Local-space rotation offset added after blending."),
                ("rotationAxis", "Freeze Axes", "Axes this constraint may drive.")));
        }
    }

    [CustomEditor(typeof(BasisScaleConstraint))]
    public class BasisScaleConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Scale Constraint";
        protected override string Subtitle => "Follows the blended scale of its sources.";

        protected override void ZeroNeutral()
        {
            BasisScaleConstraint constraint = (BasisScaleConstraint)target;
            constraint.scaleAtRest = Vector3.one;
            constraint.scaleOffset = Vector3.one;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Scale",
                ("scaleAtRest", "At Rest", "Local scale used when Weight is 0."),
                ("scaleOffset", "Offset", "Multiplied onto the blended source scale."),
                ("scalingAxis", "Freeze Axes", "Axes this constraint may drive.")));
        }
    }

    [CustomEditor(typeof(BasisParentConstraint))]
    public class BasisParentConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Parent Constraint";
        protected override string Subtitle => "Follows its sources in position and rotation together.";

        protected override void ZeroNeutral()
        {
            BasisParentConstraint constraint = (BasisParentConstraint)target;
            constraint.translationAtRest = Vector3.zero;
            constraint.rotationAtRest = Vector3.zero;
            for (int Index = 0; Index < constraint.translationOffsets.Length; Index++)
            {
                constraint.translationOffsets[Index] = Vector3.zero;
            }
            for (int Index = 0; Index < constraint.rotationOffsets.Length; Index++)
            {
                constraint.rotationOffsets[Index] = Vector3.zero;
            }
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Parent",
                ("translationAtRest", "Position At Rest", "Local position used when Weight is 0."),
                ("rotationAtRest", "Rotation At Rest", "Local rotation in degrees used when Weight is 0."),
                ("translationAxis", "Freeze Position Axes", "Position axes this constraint may drive."),
                ("rotationAxis", "Freeze Rotation Axes", "Rotation axes this constraint may drive.")));

            VisualElement offsets = BasisSyncInspectorUI.Card("Per-Source Offsets", false);
            offsets.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("translationOffsets"), "Position Offsets"),
                "One entry per source, in source order. Activate bakes these from the current pose."));
            offsets.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("rotationOffsets"), "Rotation Offsets"),
                "One entry per source, in source order."));
            root.Add(offsets);
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisParentConstraint constraint = (BasisParentConstraint)target;
            if (constraint.sourceCount > 0 && constraint.translationOffsets.Length != constraint.sourceCount)
            {
                issues.Add(BasisSyncIssue.Warning(
                    "Offset count does not match the source count. Press Activate to rebake them, " +
                    "or offsets will be read against the wrong sources."));
            }
        }
    }

    [CustomEditor(typeof(BasisAimConstraint))]
    public class BasisAimConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Aim Constraint";
        protected override string Subtitle => "Points a chosen axis at the blended source position.";

        protected override void ZeroNeutral()
        {
            BasisAimConstraint constraint = (BasisAimConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Aim",
                ("aimVector", "Aim Vector", "The local axis pointed at the sources."),
                ("upVector", "Up Vector", "The local axis rolled toward world up."),
                ("rotationAtRest", "At Rest", "Local rotation in degrees used when Weight is 0."),
                ("rotationOffset", "Offset", "Rotation offset applied after aiming."),
                ("rotationAxis", "Freeze Axes", "Axes this constraint may drive.")));

            root.Add(Fields(so, "World Up",
                ("worldUpType", "Mode", "How the up reference is resolved."),
                ("worldUpObject", "Up Object", "Used by the Object Up and Object Rotation Up modes."),
                ("worldUpVector", "Up Vector", "Used by the Vector and Object Rotation Up modes.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisAimConstraint constraint = (BasisAimConstraint)target;
            if (constraint.aimVector == Vector3.zero)
            {
                issues.Add(BasisSyncIssue.Error("Aim Vector is zero — there is no axis to point."));
            }
            bool needsObject = constraint.worldUpType == BasisConstraintWorldUp.ObjectUp
                || constraint.worldUpType == BasisConstraintWorldUp.ObjectRotationUp;
            if (needsObject && constraint.worldUpObject == null)
            {
                issues.Add(BasisSyncIssue.Warning(
                    "This World Up mode needs an Up Object; without one it falls back to scene up."));
            }
        }
    }

    [CustomEditor(typeof(BasisLookAtConstraint))]
    public class BasisLookAtConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Look At Constraint";
        protected override string Subtitle => "Points local forward at the blended source position.";

        protected override void ZeroNeutral()
        {
            BasisLookAtConstraint constraint = (BasisLookAtConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Look At",
                ("roll", "Roll", "Spin about the aim axis, in degrees."),
                ("rotationAtRest", "At Rest", "Local rotation in degrees used when Weight is 0."),
                ("rotationOffset", "Offset", "Rotation offset applied after aiming."),
                ("useUpObject", "Use Up Object", "Roll against an object instead of scene up."),
                ("worldUpObject", "Up Object", "The object rolled against when Use Up Object is set.")));
        }
    }

    [CustomEditor(typeof(BasisBlendConstraint))]
    public class BasisBlendConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Blend Constraint";
        protected override string Subtitle => "Blends between exactly two sources.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Blend",
                ("blendPosition", "Blend Position", "Blend the position between the two sources."),
                ("positionWeight", "Position", "0 is the first source, 1 is the second."),
                ("blendRotation", "Blend Rotation", "Blend the rotation between the two sources."),
                ("rotationWeight", "Rotation", "0 is the first source, 1 is the second."),
                ("translationAxis", "Freeze Position Axes", "Position axes this constraint may drive."),
                ("rotationAxis", "Freeze Rotation Axes", "Rotation axes this constraint may drive.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            if (((BasisBlendConstraint)target).sourceCount < 2)
            {
                issues.Add(BasisSyncIssue.Error(
                    "A blend needs two sources — the first is the 0 end, the second is the 1 end."));
            }
        }
    }

    [CustomEditor(typeof(BasisOverrideTransform))]
    public class BasisOverrideTransformInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Override Transform";
        protected override string Subtitle => "Replaces the pose, from explicit values or a source's movement.";
        protected override bool RequiresSources => false;

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Override",
                ("space", "Space", "How the override pose is interpreted."),
                ("useSource", "Use Source", "Drive from the first source's movement since bind instead of the values below."),
                ("position", "Position", "Override position, used when Use Source is off."),
                ("rotation", "Rotation", "Override rotation in degrees, used when Use Source is off."),
                ("positionWeight", "Position Weight", "How far the position travels toward the override."),
                ("rotationWeight", "Rotation Weight", "How far the rotation travels toward the override."),
                ("translationAxis", "Freeze Position Axes", "Position axes this constraint may drive."),
                ("rotationAxis", "Freeze Rotation Axes", "Rotation axes this constraint may drive.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisOverrideTransform constraint = (BasisOverrideTransform)target;
            if (constraint.useSource && constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error(
                    "Use Source is set but no source is assigned, so there is no movement to apply."));
            }
        }
    }

    [CustomEditor(typeof(BasisDampedTransform))]
    public class BasisDampedTransformInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Damped Transform";
        protected override string Subtitle => "Lags behind its source, for floppy secondary motion.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Damping",
                ("dampPosition", "Damp Position", "Resistance, not weight: 0 snaps to the source, 1 never moves."),
                ("dampRotation", "Damp Rotation", "Resistance, not weight: 0 snaps to the source, 1 never moves."),
                ("maintainAim", "Maintain Aim", "Keep pointing at the source while lagging behind it.")));
        }
    }

    [CustomEditor(typeof(BasisTwistCorrection))]
    public class BasisTwistCorrectionInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Twist Correction";
        protected override string Subtitle => "Takes a share of the source's roll about one axis.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Twist",
                ("twistAxis", "Twist Axis", "The axis of the source the twist is read from."),
                ("twistWeight", "Share", "How much of the source's twist this transform takes. Negative counters it.")));
        }
    }

    [CustomEditor(typeof(BasisTwistChain))]
    public class BasisTwistChainInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Twist Chain";
        protected override string Subtitle => "Holds one bone's share of the twist between two ends.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Twist Chain",
                ("blend", "Blend", "Where between the root end (0) and the tip end (1) this bone sits."),
                ("rotationAtRest", "At Rest", "Local rotation in degrees used when Weight is 0.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            if (((BasisTwistChain)target).sourceCount < 2)
            {
                issues.Add(BasisSyncIssue.Error(
                    "Needs two sources: the root end first, then the tip end."));
            }
        }
    }

    [CustomEditor(typeof(BasisTwoBoneIK))]
    public class BasisTwoBoneIKInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Two Bone IK";
        protected override string Subtitle => "Analytic arm and leg IK. Lives on the tip bone.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Chain",
                ("mid", "Mid Bone", "Elbow or knee. Defaults to this transform's parent."),
                ("root", "Root Bone", "Shoulder or hip. Defaults to the mid bone's parent.")));

            root.Add(Fields(so, "Target",
                ("targetPositionWeight", "Position Weight", "How strongly the tip is pulled onto the target."),
                ("targetRotationWeight", "Rotation Weight", "How strongly the tip takes the target's rotation."),
                ("hintWeight", "Hint Weight", "How strongly the hint steers which way the limb bends."),
                ("maintainTargetOffset", "Maintain Offset", "Hold the offset the tip had from the target when captured.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisTwoBoneIK constraint = (BasisTwoBoneIK)target;
            if (constraint.ResolveMid() == null || constraint.ResolveRoot() == null)
            {
                issues.Add(BasisSyncIssue.Error(
                    "Needs two bones above this one. Set Mid and Root explicitly, or place this on a " +
                    "bone with a grandparent."));
            }
            if (constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error("The first source is the IK target and is required."));
            }
        }
    }

    [CustomEditor(typeof(BasisChainIK))]
    public class BasisChainIKInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Chain IK";
        protected override string Subtitle => "Reaches a chain of any length at a target. Lives on the tip.";

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Chain",
                ("root", "Root Bone", "First bone of the chain. Must be an ancestor of this one.")));

            root.Add(Fields(so, "Solve",
                ("chainRotationWeight", "Chain Weight", "How strongly the chain bends toward the target."),
                ("tipRotationWeight", "Tip Weight", "How strongly the tip takes the target's rotation."),
                ("tolerance", "Tolerance", "How close the tip must get before the solve stops early."),
                ("maxIterations", "Max Iterations", "Iteration budget per frame. Longer chains need more.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisChainIK constraint = (BasisChainIK)target;
            if (constraint.root == null)
            {
                issues.Add(BasisSyncIssue.Error("Root Bone is required."));
            }
            else if (!IsAncestorOf(constraint.root, constraint.transform))
            {
                issues.Add(BasisSyncIssue.Error(
                    "Root Bone is not an ancestor of this transform, so there is no chain between them."));
            }
            if (constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error("The first source is the IK target and is required."));
            }
        }

        static bool IsAncestorOf(Transform candidate, Transform descendant)
        {
            Transform cursor = descendant;
            while (cursor != null)
            {
                if (cursor == candidate)
                {
                    return true;
                }
                cursor = cursor.parent;
            }
            return false;
        }
    }

    [CustomEditor(typeof(BasisMultiReferential))]
    public class BasisMultiReferentialInspector : BasisConstraintInspectorBase
    {
        protected override string Title => "Multi Referential";
        protected override string Subtitle => "Holds a set of transforms in a fixed arrangement.";
        protected override bool RequiresSources => false;

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, "Members",
                ("members", "Members", "The transforms held together. Order matters only in that Driver indexes into it."),
                ("driver", "Driver", "Which member leads. Safe to change at runtime.")));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisMultiReferential constraint = (BasisMultiReferential)target;
            if (constraint.members.Count < 2)
            {
                issues.Add(BasisSyncIssue.Error("Needs at least two members to hold an arrangement."));
                return;
            }
            for (int Index = 0; Index < constraint.members.Count; Index++)
            {
                if (constraint.members[Index] == null)
                {
                    issues.Add(BasisSyncIssue.Error(
                        $"Member {Index} is empty. Every member must be assigned or the set is skipped."));
                    return;
                }
            }
            if (constraint.BindPositions.Length != constraint.members.Count)
            {
                issues.Add(BasisSyncIssue.Warning(
                    "The arrangement has not been captured for the current members. Press Activate."));
            }
            if (constraint.driver < 0 || constraint.driver >= constraint.members.Count)
            {
                issues.Add(BasisSyncIssue.Warning(
                    "Driver is outside the member list and will be clamped."));
            }
        }
    }
}
