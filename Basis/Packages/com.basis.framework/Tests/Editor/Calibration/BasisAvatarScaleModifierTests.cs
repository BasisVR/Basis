using Basis.Scripts.Avatar;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

public sealed class BasisAvatarScaleModifierTests
{
    [Test]
    public void PerAvatarScaleKey_IsStableAndContentBased()
    {
        Assert.That(BasisPerAvatarScale.KeyFor("avatar-123"), Is.EqualTo("avatarscale::DB81E245"));
        Assert.That(BasisPerAvatarScale.KeyFor("avatar-124"), Is.Not.EqualTo(BasisPerAvatarScale.KeyFor("avatar-123")));
    }

    [Test]
    public void Override_WritesBackToTheExplicitAvatarRootThatWasMeasured()
    {
        GameObject wrapper = new GameObject("AvatarWrapper");
        GameObject animatorObject = new GameObject("AnimatorRoot");
        animatorObject.transform.SetParent(wrapper.transform, false);
        Animator animator = animatorObject.AddComponent<Animator>();

        try
        {
            wrapper.transform.localScale = new Vector3(3f, 3f, 3f);
            animatorObject.transform.localScale = new Vector3(0.5f, 0.75f, 1.25f);

            var modifier = new BasisAvatarScaleModifier();
            modifier.ReInitialize(animator, wrapper.transform);
            modifier.SetAvatarheightOverride(2f);

            Assert.That(wrapper.transform.localScale, Is.EqualTo(new Vector3(6f, 6f, 6f)));
            Assert.That(animatorObject.transform.localScale, Is.EqualTo(new Vector3(0.5f, 0.75f, 1.25f)),
                "the nested Animator/import node must not be rewritten when AvatarTransform owns runtime scale");
        }
        finally
        {
            Object.DestroyImmediate(wrapper);
        }
    }

    [Test]
    public void ImportedCentimetreRig_IsScaledOnce_NotCompoundedThroughItsWrapper()
    {
        GameObject wrapper = new GameObject("AvatarWrapper");
        GameObject animatorObject = new GameObject("AnimatorRoot");
        GameObject eye = new GameObject("EyeMarker");
        animatorObject.transform.SetParent(wrapper.transform, false);
        eye.transform.SetParent(animatorObject.transform, false);
        Animator animator = animatorObject.AddComponent<Animator>();

        try
        {
            animatorObject.transform.localScale = Vector3.one * 0.01f;
            eye.transform.localPosition = Vector3.up * 160f;

            var modifier = new BasisAvatarScaleModifier();
            modifier.ReInitialize(animator, wrapper.transform);
            modifier.SetAvatarheightOverride(1.5f);

            Assert.That(eye.transform.position.y, Is.EqualTo(2.4f).Within(1e-4f),
                "a 160 cm rig imported at 0.01 and scaled 1.5x must render at 2.4 m, not receive 0.01 twice");
            Assert.That(wrapper.transform.localScale, Is.EqualTo(Vector3.one * 1.5f));
            Assert.That(animatorObject.transform.localScale, Is.EqualTo(Vector3.one * 0.01f));
        }
        finally
        {
            Object.DestroyImmediate(wrapper);
        }
    }

    [Test]
    public void NetworkScale_CarriesTheRuntimeScaleEvenWhenTheAnimatorIsAChild()
    {
        GameObject wrapper = new GameObject("AvatarWrapper");
        GameObject animatorObject = new GameObject("AnimatorRoot");
        animatorObject.transform.SetParent(wrapper.transform, false);
        Animator animator = animatorObject.AddComponent<Animator>();

        try
        {
            animatorObject.transform.localScale = Vector3.one * 0.01f;
            var modifier = new BasisAvatarScaleModifier();
            modifier.ReInitialize(animator, wrapper.transform);
            modifier.SetAvatarheightOverride(1.5f);

            float wire = modifier.NetworkScale(animatorObject.transform);
            Assert.That(wire, Is.EqualTo(0.015f).Within(1e-6f));
            Assert.That(BasisCalibrationMath.ScaleAuthoredMetricByRootRatio(1.6f, wire, 0.01f), Is.EqualTo(2.4f).Within(1e-4f));

            var rootModifier = new BasisAvatarScaleModifier();
            rootModifier.ReInitialize(animator, animatorObject.transform);
            rootModifier.SetAvatarheightOverride(2f);
            Assert.That(rootModifier.NetworkScale(animatorObject.transform), Is.EqualTo(0.02f).Within(1e-6f));
        }
        finally
        {
            Object.DestroyImmediate(wrapper);
        }
    }

    [Test]
    public void RemoteRenderedMetric_UsesRuntimeRatio_NotFullImportScale()
    {
        Assert.That(BasisCalibrationMath.ScaleAuthoredMetricByRootRatio(1.6f, 0.015f, 0.01f),
            Is.EqualTo(2.4f).Within(1e-5f));
        Assert.That(BasisCalibrationMath.ScaleAuthoredMetricByRootRatio(1.6f, 1.5f, 1f),
            Is.EqualTo(2.4f).Within(1e-5f));
    }

    [Test]
    public void TposeSnapshotRoleSet_IncludesBodyFitMeasurementJoints()
    {
        var roles = BasisAvatarIKStageCalibration.GetAllRolesAsTransform();

        Assert.That(roles.ContainsKey(BasisBoneTrackedRole.LeftUpperArm), Is.True);
        Assert.That(roles.ContainsKey(BasisBoneTrackedRole.RightUpperArm), Is.True);
        Assert.That(roles.ContainsKey(BasisBoneTrackedRole.LeftUpperLeg), Is.True);
        Assert.That(roles.ContainsKey(BasisBoneTrackedRole.RightUpperLeg), Is.True);
    }
}
