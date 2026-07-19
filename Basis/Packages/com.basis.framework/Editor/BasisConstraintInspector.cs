using System.Collections.Generic;
using Basis.Scripts.Constraints;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisConstraint))]
[CanEditMultipleObjects]
public class BasisConstraintInspector : BasisDocInspector_UI
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        root.Add(BasisSyncInspectorUI.Header(
            "Basis Constraint",
            "Replaces the six UnityEngine.Animations constraints with one component. Pick a Kind and only the fields that kind uses stay live. Constraints author into a flat Burst record, so a whole avatar solves as one batch instead of a callback per component."));

        root.Add(BasisSyncInspectorUI.ValidationContainer(Validate));

        VisualElement setup = BasisSyncInspectorUI.Card("Constraint");
        var kindField = new PropertyField(serializedObject.FindProperty("Kind"), "Kind");
        kindField.tooltip = "Which Unity constraint this stands in for. Position / Rotation / Scale drive one channel, Parent drives position and rotation together, Aim and LookAt orient the target at its sources.";
        setup.Add(kindField);
        setup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("ConstraintActive"), "Active"),
            "Off leaves the target's pose completely untouched, as if the component were not here."));
        setup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Weight")),
            "Overall blend from the target's own pose toward the constrained pose. 0 is a no-op, 1 fully drives the enabled axes."));
        setup.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Locked")),
            "Locked keeps axes this constraint does not drive at their live pose. Unlocked returns those axes to the captured rest pose instead."));
        root.Add(setup);

        VisualElement sourcesCard = BasisSyncInspectorUI.Card("Sources");
        sourcesCard.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Sources")),
            "Weighted transforms this constraint follows. Weights are normalised against each other, so two sources at 1 each blend 50/50."));
        sourcesCard.Add(SourceWeightReadout());
        root.Add(sourcesCard);

        VisualElement translation = BasisSyncInspectorUI.Card("Position");
        translation.Add(AxisMaskField("TranslationAxis", "Driven Axes",
            "Axes this constraint may move. Excluded axes keep the live pose, or the rest pose when unlocked."));
        translation.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("TranslationOffset"), "Offset"),
            "Added to the blended source position, after the sources are combined."));
        root.Add(translation);

        VisualElement rotation = BasisSyncInspectorUI.Card("Rotation");
        rotation.Add(AxisMaskField("RotationAxis", "Driven Axes",
            "Axes this constraint may rotate. Excluded axes keep the live pose, or the rest pose when unlocked."));
        rotation.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("RotationOffset"), "Offset (deg)"),
            "Applied on top of the blended source rotation, after the sources are combined."));
        root.Add(rotation);

        VisualElement scale = BasisSyncInspectorUI.Card("Scale");
        scale.Add(AxisMaskField("ScaleAxis", "Driven Axes",
            "Axes this constraint may scale. Excluded axes keep the live pose, or the rest pose when unlocked."));
        scale.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("ScaleOffset"), "Offset"),
            "Multiplied into the blended source scale. Leave at 1,1,1 for no change."));
        root.Add(scale);

        VisualElement aim = BasisSyncInspectorUI.Card("Aim");
        var aimVector = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("AimVector")),
            "Local axis pointed at the sources. LookAt always aims down local forward and ignores this.");
        var upVector = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("UpVector")),
            "Local axis kept aligned with the resolved world up. LookAt always uses local up and ignores this.");
        var roll = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("Roll"), "Roll (deg)"),
            "Twist about the aim axis, applied after aiming. LookAt only.");
        aim.Add(aimVector);
        aim.Add(upVector);
        aim.Add(roll);
        var worldUpKind = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("WorldUpKind"), "World Up"),
            "How the up direction used to resolve twist is chosen.");
        aim.Add(worldUpKind);
        var worldUpVector = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("WorldUpVector"), "Up Vector"),
            "Explicit up direction, used by the Vector and Object Rotation Up modes.");
        var worldUpObject = BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("WorldUpObject"), "Up Object"),
            "Transform supplying world up for the Object Up and Object Rotation Up modes.");
        aim.Add(worldUpVector);
        aim.Add(worldUpObject);
        root.Add(aim);

        VisualElement rest = BasisSyncInspectorUI.Card("Rest Pose", false);
        var restHint = new Label("Where unlocked axes fall back to, in world space — the solver resolves in world space. Captured from the transform when the component is added; press Capture Rest after re-posing the object.");
        restHint.style.whiteSpace = WhiteSpace.Normal;
        restHint.style.fontSize = 10;
        restHint.style.opacity = 0.8f;
        rest.Add(restHint);
        rest.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("TranslationAtRest"), "Position (world)"),
            "World position unlocked axes return to."));
        rest.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("RotationAtRest"), "Rotation (world, deg)"),
            "World euler rotation unlocked axes return to."));
        rest.Add(BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty("ScaleAtRest"), "Scale (lossy)"),
            "World (lossy) scale unlocked axes return to."));
        rest.Add(new Button(CaptureRest)
        {
            text = "Capture Rest",
            tooltip = "Store every selected target's current local pose as its rest pose.",
        });
        root.Add(rest);

        // Only the cards the selected Kind actually reads stay visible, so a Position constraint is not
        // padded out with aim vectors and scale offsets.
        void ApplyKind()
        {
            var kind = (BasisConstraintKind)serializedObject.FindProperty("Kind").enumValueIndex;
            bool usesAim = kind == BasisConstraintKind.Aim || kind == BasisConstraintKind.LookAt;
            bool isLookAt = kind == BasisConstraintKind.LookAt;

            Show(translation, kind == BasisConstraintKind.Position || kind == BasisConstraintKind.Parent);
            Show(rotation, kind == BasisConstraintKind.Rotation || kind == BasisConstraintKind.Parent || usesAim);
            Show(scale, kind == BasisConstraintKind.Scale);
            Show(aim, usesAim);

            // LookAt pins the aim/up axes to local forward/up and spins by Roll instead.
            Show(aimVector, usesAim && !isLookAt);
            Show(upVector, usesAim && !isLookAt);
            Show(roll, isLookAt);

            var upKind = (BasisWorldUpKind)serializedObject.FindProperty("WorldUpKind").enumValueIndex;
            Show(worldUpObject, usesAim && (upKind == BasisWorldUpKind.ObjectUp || upKind == BasisWorldUpKind.ObjectRotationUp));
            Show(worldUpVector, usesAim && (upKind == BasisWorldUpKind.Vector || upKind == BasisWorldUpKind.ObjectRotationUp));
        }

        ApplyKind();
        kindField.RegisterValueChangeCallback(_ => ApplyKind());
        worldUpKind.RegisterValueChangeCallback(_ => ApplyKind());

        root.Bind(serializedObject);

        var api = CreateApiReferenceFoldout();
        if (api != null) root.Add(api);
        return root;
    }

    private static void Show(VisualElement element, bool visible)
    {
        element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>
    /// Per-channel axis mask. <see cref="BasisConstraintAxis"/> is a [Flags] enum, so the stock property
    /// drawer already gives a multi-select dropdown — and unlike a hand-rolled field it binds, so undo and
    /// multi-object editing keep working.
    /// </summary>
    private VisualElement AxisMaskField(string propertyName, string label, string tooltip)
    {
        return BasisSyncInspectorUI.Described(new PropertyField(serializedObject.FindProperty(propertyName), label), tooltip);
    }

    /// <summary>
    /// Live view of how the authored weights actually normalise, which is the thing that most often
    /// surprises people — a single source at 0.25 still fully drives the target.
    /// </summary>
    private VisualElement SourceWeightReadout()
    {
        var box = new VisualElement();
        box.style.marginTop = 4;

        var label = new Label();
        label.style.whiteSpace = WhiteSpace.Normal;
        label.style.fontSize = 10;
        label.style.opacity = 0.8f;
        box.Add(label);

        void Refresh()
        {
            var t = target as BasisConstraint;
            if (t == null || t.Sources == null)
            {
                label.text = "";
                return;
            }

            float total = 0f;
            int valid = 0;
            for (int i = 0; i < t.Sources.Length; i++)
            {
                BasisConstraintSourceBinding binding = t.Sources[i];
                if (binding == null || binding.SourceTransform == null || binding.Weight <= 0f) continue;
                total += Mathf.Clamp01(binding.Weight);
                valid++;
            }

            if (valid == 0)
            {
                label.text = "No contributing source — this constraint is inert.";
                return;
            }

            var parts = new List<string>();
            for (int i = 0; i < t.Sources.Length; i++)
            {
                BasisConstraintSourceBinding binding = t.Sources[i];
                if (binding == null || binding.SourceTransform == null || binding.Weight <= 0f) continue;
                float share = Mathf.Clamp01(binding.Weight) / total;
                parts.Add($"{binding.SourceTransform.name} {share * 100f:0}%");
            }
            label.text = "Effective blend: " + string.Join("  •  ", parts);
        }

        Refresh();
        box.schedule.Execute(Refresh).Every(300);
        return box;
    }

    private void CaptureRest()
    {
        foreach (Object o in targets)
        {
            if (o is not BasisConstraint constraint) continue;
            Undo.RecordObject(constraint, "Capture Constraint Rest");
            constraint.CaptureRest();
            EditorUtility.SetDirty(constraint);
        }
        serializedObject.Update();
    }

    protected virtual List<BasisSyncIssue> Validate()
    {
        var issues = new List<BasisSyncIssue>();
        var t = target as BasisConstraint;
        if (t == null) return issues;

        if (t.Sources == null || t.Sources.Length == 0)
        {
            issues.Add(BasisSyncIssue.Error("No sources assigned — this constraint does nothing."));
        }
        else if (t.CountValidSources() == 0)
        {
            issues.Add(BasisSyncIssue.Error("Every source is empty or at zero weight — this constraint does nothing."));
        }

        if (t.Sources != null)
        {
            for (int i = 0; i < t.Sources.Length; i++)
            {
                BasisConstraintSourceBinding binding = t.Sources[i];
                if (binding == null) continue;
                if (binding.SourceTransform == t.transform)
                {
                    issues.Add(BasisSyncIssue.Error($"Source {i} targets this same transform — a constraint cannot follow itself."));
                }
                else if (binding.SourceTransform != null && t.transform.IsChildOf(binding.SourceTransform) && t.Kind == BasisConstraintKind.Parent)
                {
                    issues.Add(BasisSyncIssue.Warning($"Source {i} is an ancestor of this transform — parenting to it double-applies its motion."));
                }
            }
        }

        if (!t.ConstraintActive)
        {
            issues.Add(BasisSyncIssue.Warning("Active is off — the target's pose is left untouched."));
        }
        else if (t.Weight <= 0f)
        {
            issues.Add(BasisSyncIssue.Warning("Weight is 0 — the constraint is a no-op until you raise it."));
        }

        bool positionMasked = t.DrivesPosition && t.TranslationAxis == BasisConstraintAxis.None;
        bool rotationMasked = t.DrivesRotation && t.RotationAxis == BasisConstraintAxis.None;
        bool scaleMasked = t.DrivesScale && t.ScaleAxis == BasisConstraintAxis.None;
        bool anyChannelLive = (t.DrivesPosition && !positionMasked)
            || (t.DrivesRotation && !rotationMasked)
            || (t.DrivesScale && !scaleMasked);

        if (!anyChannelLive)
        {
            issues.Add(BasisSyncIssue.Warning("Every axis this kind drives is masked off — nothing will move."));
        }
        else
        {
            if (positionMasked) issues.Add(BasisSyncIssue.Warning("Every position axis is masked off — this constraint will not move the target."));
            if (rotationMasked) issues.Add(BasisSyncIssue.Warning("Every rotation axis is masked off — this constraint will not rotate the target."));
            if (scaleMasked) issues.Add(BasisSyncIssue.Warning("Every scale axis is masked off — this constraint will not scale the target."));
        }

        if (t.UsesAim)
        {
            if (t.Kind == BasisConstraintKind.Aim && t.AimVector == Vector3.zero)
            {
                issues.Add(BasisSyncIssue.Error("Aim Vector is zero — the target has no axis to point at its sources."));
            }
            bool needsUpObject = t.WorldUpKind == BasisWorldUpKind.ObjectUp || t.WorldUpKind == BasisWorldUpKind.ObjectRotationUp;
            if (needsUpObject && t.WorldUpObject == null)
            {
                issues.Add(BasisSyncIssue.Error($"World Up is set to {t.WorldUpKind} but no Up Object is assigned — up falls back to scene up."));
            }
        }

        return issues;
    }
}
