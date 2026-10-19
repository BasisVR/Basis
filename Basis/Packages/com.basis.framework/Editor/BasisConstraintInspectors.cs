using System.Collections.Generic;
using Basis.Editor.Localization;
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
                        BasisEditorLocalization.Get("editor.constraint.base.noSources")));
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
            { text = BasisEditorLocalization.Get("editor.constraint.base.activate") };
            activate.style.flexGrow = 1;
            BasisSyncInspectorUI.Described(activate,
                BasisEditorLocalization.Get("editor.constraint.base.activate.tooltip"));

            Button zero = new Button(() =>
            {
                Undo.RecordObject(Constraint, "Zero Constraint");
                ZeroNeutral();
                EditorUtility.SetDirty(Constraint);
            })
            { text = BasisEditorLocalization.Get("editor.constraint.base.zero") };
            zero.style.flexGrow = 1;
            BasisSyncInspectorUI.Described(zero,
                BasisEditorLocalization.Get("editor.constraint.base.zero.tooltip"));

            row.Add(activate);
            row.Add(zero);
            return row;
        }

        /// <summary>Clears whatever this kind treats as its neutral pose.</summary>
        protected virtual void ZeroNeutral() { }

        VisualElement BuildCommon(SerializedObject so)
        {
            VisualElement card = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.constraint.base.card"));
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("constraintActive"), BasisEditorLocalization.Get("editor.constraint.base.active")),
                BasisEditorLocalization.Get("editor.constraint.base.active.tooltip")));
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("weight"), BasisEditorLocalization.Get("editor.constraint.base.weight")),
                BasisEditorLocalization.Get("editor.constraint.base.weight.tooltip")));
            card.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("locked"), BasisEditorLocalization.Get("editor.constraint.base.locked")),
                BasisEditorLocalization.Get("editor.constraint.base.locked.tooltip")));

            if (RequiresSources)
            {
                VisualElement sources = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.constraint.base.sourcesCard"));
                sources.Add(BasisSyncInspectorUI.Described(
                    new PropertyField(so.FindProperty("sources"), BasisEditorLocalization.Get("editor.constraint.base.sources")),
                    BasisEditorLocalization.Get("editor.constraint.base.sources.tooltip")));
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
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.position.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.position.subtitle");

        protected override void ZeroNeutral()
        {
            BasisPositionConstraint constraint = (BasisPositionConstraint)target;
            constraint.translationAtRest = Vector3.zero;
            constraint.translationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.common.position"),
                ("translationAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.positionAtRest.tooltip")),
                ("translationOffset", BasisEditorLocalization.Get("editor.constraint.common.offset"), BasisEditorLocalization.Get("editor.constraint.position.offset.tooltip")),
                ("translationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeAxes"), BasisEditorLocalization.Get("editor.constraint.position.freezeAxes.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisRotationConstraint))]
    public class BasisRotationConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.rotation.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.rotation.subtitle");

        protected override void ZeroNeutral()
        {
            BasisRotationConstraint constraint = (BasisRotationConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.common.rotation"),
                ("rotationAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.rotationAtRest.tooltip")),
                ("rotationOffset", BasisEditorLocalization.Get("editor.constraint.common.offset"), BasisEditorLocalization.Get("editor.constraint.rotation.offset.tooltip")),
                ("rotationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeAxes.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisScaleConstraint))]
    public class BasisScaleConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.scale.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.scale.subtitle");

        protected override void ZeroNeutral()
        {
            BasisScaleConstraint constraint = (BasisScaleConstraint)target;
            constraint.scaleAtRest = Vector3.one;
            constraint.scaleOffset = Vector3.one;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.common.scale"),
                ("scaleAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.scaleAtRest.tooltip")),
                ("scaleOffset", BasisEditorLocalization.Get("editor.constraint.common.offset"), BasisEditorLocalization.Get("editor.constraint.scale.offset.tooltip")),
                ("scalingAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeAxes.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisParentConstraint))]
    public class BasisParentConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.parent.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.parent.subtitle");

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
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.parent.card"),
                ("translationAtRest", BasisEditorLocalization.Get("editor.constraint.parent.positionAtRest"), BasisEditorLocalization.Get("editor.constraint.common.positionAtRest.tooltip")),
                ("rotationAtRest", BasisEditorLocalization.Get("editor.constraint.parent.rotationAtRest"), BasisEditorLocalization.Get("editor.constraint.common.rotationAtRest.tooltip")),
                ("translationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes.tooltip")),
                ("rotationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes.tooltip"))));

            VisualElement offsets = BasisSyncInspectorUI.Card(BasisEditorLocalization.Get("editor.constraint.parent.offsetsCard"), false);
            offsets.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("translationOffsets"), BasisEditorLocalization.Get("editor.constraint.parent.positionOffsets")),
                BasisEditorLocalization.Get("editor.constraint.parent.positionOffsets.tooltip")));
            offsets.Add(BasisSyncInspectorUI.Described(
                new PropertyField(so.FindProperty("rotationOffsets"), BasisEditorLocalization.Get("editor.constraint.parent.rotationOffsets")),
                BasisEditorLocalization.Get("editor.constraint.parent.rotationOffsets.tooltip")));
            root.Add(offsets);
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisParentConstraint constraint = (BasisParentConstraint)target;
            if (constraint.sourceCount > 0 && constraint.translationOffsets.Length != constraint.sourceCount)
            {
                issues.Add(BasisSyncIssue.Warning(
                    BasisEditorLocalization.Get("editor.constraint.parent.offsetMismatch")));
            }
        }
    }

    [CustomEditor(typeof(BasisAimConstraint))]
    public class BasisAimConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.aim.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.aim.subtitle");

        protected override void ZeroNeutral()
        {
            BasisAimConstraint constraint = (BasisAimConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.aim.card"),
                ("aimVector", BasisEditorLocalization.Get("editor.constraint.aim.aimVector"), BasisEditorLocalization.Get("editor.constraint.aim.aimVector.tooltip")),
                ("upVector", BasisEditorLocalization.Get("editor.constraint.common.upVector"), BasisEditorLocalization.Get("editor.constraint.aim.upVector.tooltip")),
                ("rotationAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.rotationAtRest.tooltip")),
                ("rotationOffset", BasisEditorLocalization.Get("editor.constraint.common.offset"), BasisEditorLocalization.Get("editor.constraint.common.aimOffset.tooltip")),
                ("rotationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeAxes.tooltip"))));

            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.aim.worldUpCard"),
                ("worldUpType", BasisEditorLocalization.Get("editor.common.mode"), BasisEditorLocalization.Get("editor.constraint.aim.worldUpMode.tooltip")),
                ("worldUpObject", BasisEditorLocalization.Get("editor.constraint.common.upObject"), BasisEditorLocalization.Get("editor.constraint.aim.worldUpObject.tooltip")),
                ("worldUpVector", BasisEditorLocalization.Get("editor.constraint.common.upVector"), BasisEditorLocalization.Get("editor.constraint.aim.worldUpVector.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisAimConstraint constraint = (BasisAimConstraint)target;
            if (constraint.aimVector == Vector3.zero)
            {
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.constraint.aim.zeroVector")));
            }
            bool needsObject = constraint.worldUpType == BasisConstraintWorldUp.ObjectUp
                || constraint.worldUpType == BasisConstraintWorldUp.ObjectRotationUp;
            if (needsObject && constraint.worldUpObject == null)
            {
                issues.Add(BasisSyncIssue.Warning(
                    BasisEditorLocalization.Get("editor.constraint.aim.needsUpObject")));
            }
        }
    }

    [CustomEditor(typeof(BasisLookAtConstraint))]
    public class BasisLookAtConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.lookAt.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.lookAt.subtitle");

        protected override void ZeroNeutral()
        {
            BasisLookAtConstraint constraint = (BasisLookAtConstraint)target;
            constraint.rotationAtRest = Vector3.zero;
            constraint.rotationOffset = Vector3.zero;
        }

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.lookAt.card"),
                ("roll", BasisEditorLocalization.Get("editor.constraint.lookAt.roll"), BasisEditorLocalization.Get("editor.constraint.lookAt.roll.tooltip")),
                ("rotationAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.rotationAtRest.tooltip")),
                ("rotationOffset", BasisEditorLocalization.Get("editor.constraint.common.offset"), BasisEditorLocalization.Get("editor.constraint.common.aimOffset.tooltip")),
                ("useUpObject", BasisEditorLocalization.Get("editor.constraint.lookAt.useUpObject"), BasisEditorLocalization.Get("editor.constraint.lookAt.useUpObject.tooltip")),
                ("worldUpObject", BasisEditorLocalization.Get("editor.constraint.common.upObject"), BasisEditorLocalization.Get("editor.constraint.lookAt.upObject.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisBlendConstraint))]
    public class BasisBlendConstraintInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.blend.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.blend.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.blend.card"),
                ("blendPosition", BasisEditorLocalization.Get("editor.constraint.blend.blendPosition"), BasisEditorLocalization.Get("editor.constraint.blend.blendPosition.tooltip")),
                ("positionWeight", BasisEditorLocalization.Get("editor.constraint.common.position"), BasisEditorLocalization.Get("editor.constraint.blend.weight.tooltip")),
                ("blendRotation", BasisEditorLocalization.Get("editor.constraint.blend.blendRotation"), BasisEditorLocalization.Get("editor.constraint.blend.blendRotation.tooltip")),
                ("rotationWeight", BasisEditorLocalization.Get("editor.constraint.common.rotation"), BasisEditorLocalization.Get("editor.constraint.blend.weight.tooltip")),
                ("translationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes.tooltip")),
                ("rotationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            if (((BasisBlendConstraint)target).sourceCount < 2)
            {
                issues.Add(BasisSyncIssue.Error(
                    BasisEditorLocalization.Get("editor.constraint.blend.needsTwoSources")));
            }
        }
    }

    [CustomEditor(typeof(BasisOverrideTransform))]
    public class BasisOverrideTransformInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.override.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.override.subtitle");
        protected override bool RequiresSources => false;

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.override.card"),
                ("space", BasisEditorLocalization.Get("editor.constraint.override.space"), BasisEditorLocalization.Get("editor.constraint.override.space.tooltip")),
                ("useSource", BasisEditorLocalization.Get("editor.constraint.override.useSource"), BasisEditorLocalization.Get("editor.constraint.override.useSource.tooltip")),
                ("position", BasisEditorLocalization.Get("editor.constraint.common.position"), BasisEditorLocalization.Get("editor.constraint.override.position.tooltip")),
                ("rotation", BasisEditorLocalization.Get("editor.constraint.common.rotation"), BasisEditorLocalization.Get("editor.constraint.override.rotation.tooltip")),
                ("positionWeight", BasisEditorLocalization.Get("editor.constraint.common.positionWeight"), BasisEditorLocalization.Get("editor.constraint.override.positionWeight.tooltip")),
                ("rotationWeight", BasisEditorLocalization.Get("editor.constraint.common.rotationWeight"), BasisEditorLocalization.Get("editor.constraint.override.rotationWeight.tooltip")),
                ("translationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezePositionAxes.tooltip")),
                ("rotationAxis", BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes"), BasisEditorLocalization.Get("editor.constraint.common.freezeRotationAxes.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisOverrideTransform constraint = (BasisOverrideTransform)target;
            if (constraint.useSource && constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error(
                    BasisEditorLocalization.Get("editor.constraint.override.useSourceNoSource")));
            }
        }
    }

    [CustomEditor(typeof(BasisDampedTransform))]
    public class BasisDampedTransformInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.damped.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.damped.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.damped.card"),
                ("dampPosition", BasisEditorLocalization.Get("editor.constraint.damped.dampPosition"), BasisEditorLocalization.Get("editor.constraint.damped.damp.tooltip")),
                ("dampRotation", BasisEditorLocalization.Get("editor.constraint.damped.dampRotation"), BasisEditorLocalization.Get("editor.constraint.damped.damp.tooltip")),
                ("maintainAim", BasisEditorLocalization.Get("editor.constraint.damped.maintainAim"), BasisEditorLocalization.Get("editor.constraint.damped.maintainAim.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisTwistCorrection))]
    public class BasisTwistCorrectionInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.twistCorrection.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.twistCorrection.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.twistCorrection.card"),
                ("twistAxis", BasisEditorLocalization.Get("editor.constraint.twistCorrection.axis"), BasisEditorLocalization.Get("editor.constraint.twistCorrection.axis.tooltip")),
                ("twistWeight", BasisEditorLocalization.Get("editor.constraint.twistCorrection.share"), BasisEditorLocalization.Get("editor.constraint.twistCorrection.share.tooltip"))));
        }
    }

    [CustomEditor(typeof(BasisTwistChain))]
    public class BasisTwistChainInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.twistChain.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.twistChain.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.twistChain.title"),
                ("blend", BasisEditorLocalization.Get("editor.constraint.twistChain.blend"), BasisEditorLocalization.Get("editor.constraint.twistChain.blend.tooltip")),
                ("rotationAtRest", BasisEditorLocalization.Get("editor.constraint.common.atRest"), BasisEditorLocalization.Get("editor.constraint.common.rotationAtRest.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            if (((BasisTwistChain)target).sourceCount < 2)
            {
                issues.Add(BasisSyncIssue.Error(
                    BasisEditorLocalization.Get("editor.constraint.twistChain.needsTwoSources")));
            }
        }
    }

    [CustomEditor(typeof(BasisTwoBoneIK))]
    public class BasisTwoBoneIKInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.twoBone.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.twoBone.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("sdk.authoredMotion.field.chain.label"),
                ("mid", BasisEditorLocalization.Get("editor.constraint.twoBone.mid"), BasisEditorLocalization.Get("editor.constraint.twoBone.mid.tooltip")),
                ("root", BasisEditorLocalization.Get("editor.constraint.common.rootBone"), BasisEditorLocalization.Get("editor.constraint.twoBone.root.tooltip"))));

            root.Add(Fields(so, BasisEditorLocalization.Get("editor.common.target"),
                ("targetPositionWeight", BasisEditorLocalization.Get("editor.constraint.common.positionWeight"), BasisEditorLocalization.Get("editor.constraint.twoBone.positionWeight.tooltip")),
                ("targetRotationWeight", BasisEditorLocalization.Get("editor.constraint.common.rotationWeight"), BasisEditorLocalization.Get("editor.constraint.common.tipRotation.tooltip")),
                ("hintWeight", BasisEditorLocalization.Get("editor.constraint.twoBone.hintWeight"), BasisEditorLocalization.Get("editor.constraint.twoBone.hintWeight.tooltip")),
                ("maintainTargetOffset", BasisEditorLocalization.Get("editor.constraint.twoBone.maintainOffset"), BasisEditorLocalization.Get("editor.constraint.twoBone.maintainOffset.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisTwoBoneIK constraint = (BasisTwoBoneIK)target;
            if (constraint.ResolveMid() == null || constraint.ResolveRoot() == null)
            {
                issues.Add(BasisSyncIssue.Error(
                    BasisEditorLocalization.Get("editor.constraint.twoBone.needsBones")));
            }
            if (constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.constraint.common.noIkTarget")));
            }
        }
    }

    [CustomEditor(typeof(BasisChainIK))]
    public class BasisChainIKInspector : BasisConstraintInspectorBase
    {
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.chainIk.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.chainIk.subtitle");

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("sdk.authoredMotion.field.chain.label"),
                ("root", BasisEditorLocalization.Get("editor.constraint.common.rootBone"), BasisEditorLocalization.Get("editor.constraint.chainIk.root.tooltip"))));

            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.chainIk.solveCard"),
                ("chainRotationWeight", BasisEditorLocalization.Get("editor.constraint.chainIk.chainWeight"), BasisEditorLocalization.Get("editor.constraint.chainIk.chainWeight.tooltip")),
                ("tipRotationWeight", BasisEditorLocalization.Get("editor.constraint.chainIk.tipWeight"), BasisEditorLocalization.Get("editor.constraint.common.tipRotation.tooltip")),
                ("tolerance", BasisEditorLocalization.Get("editor.constraint.chainIk.tolerance"), BasisEditorLocalization.Get("editor.constraint.chainIk.tolerance.tooltip")),
                ("maxIterations", BasisEditorLocalization.Get("editor.constraint.chainIk.maxIterations"), BasisEditorLocalization.Get("editor.constraint.chainIk.maxIterations.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisChainIK constraint = (BasisChainIK)target;
            if (constraint.root == null)
            {
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.constraint.chainIk.rootRequired")));
            }
            else if (!IsAncestorOf(constraint.root, constraint.transform))
            {
                issues.Add(BasisSyncIssue.Error(
                    BasisEditorLocalization.Get("editor.constraint.chainIk.rootNotAncestor")));
            }
            if (constraint.sourceCount == 0)
            {
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.constraint.common.noIkTarget")));
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
        protected override string Title => BasisEditorLocalization.Get("editor.constraint.multiRef.title");
        protected override string Subtitle => BasisEditorLocalization.Get("editor.constraint.multiRef.subtitle");
        protected override bool RequiresSources => false;

        protected override void BuildSpecific(VisualElement root, SerializedObject so)
        {
            root.Add(Fields(so, BasisEditorLocalization.Get("editor.constraint.multiRef.card"),
                ("members", BasisEditorLocalization.Get("editor.constraint.multiRef.members"), BasisEditorLocalization.Get("editor.constraint.multiRef.members.tooltip")),
                ("driver", BasisEditorLocalization.Get("editor.constraint.multiRef.driver"), BasisEditorLocalization.Get("editor.constraint.multiRef.driver.tooltip"))));
        }

        protected override void Validate(List<BasisSyncIssue> issues)
        {
            BasisMultiReferential constraint = (BasisMultiReferential)target;
            if (constraint.members.Count < 2)
            {
                issues.Add(BasisSyncIssue.Error(BasisEditorLocalization.Get("editor.constraint.multiRef.needsTwoMembers")));
                return;
            }
            for (int Index = 0; Index < constraint.members.Count; Index++)
            {
                if (constraint.members[Index] == null)
                {
                    issues.Add(BasisSyncIssue.Error(
                        BasisEditorLocalization.Get("editor.constraint.multiRef.emptyMember", Index)));
                    return;
                }
            }
            if (constraint.BindPositions.Length != constraint.members.Count)
            {
                issues.Add(BasisSyncIssue.Warning(
                    BasisEditorLocalization.Get("editor.constraint.multiRef.notCaptured")));
            }
            if (constraint.driver < 0 || constraint.driver >= constraint.members.Count)
            {
                issues.Add(BasisSyncIssue.Warning(
                    BasisEditorLocalization.Get("editor.constraint.multiRef.driverOutOfRange")));
            }
        }
    }
}
