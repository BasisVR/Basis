using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Cilbox;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxAnimationEventGateTests : BasisCilboxGateRig
    {
        const string Shim = "Cilbox.CilboxAnimationEvents";
        const string AddEvent = "Method:UnityEngine.AnimationClip.AddEvent", SetEvents = "Method:UnityEngine.AnimationClip.set_events", FireEvents = "Method:UnityEngine.Animator.set_fireEvents";
        static readonly (Type Type, string Name, Type[] Parameters, bool Consent)[] Members =
        {
            (typeof(AnimationClip), nameof(AnimationClip.AddEvent), new[] { typeof(AnimationEvent) }, true),
            (typeof(AnimationClip), "set_events", new[] { typeof(AnimationEvent[]) }, true),
            (typeof(Animator), "set_fireEvents", new[] { typeof(bool) }, true),
            (typeof(Animator), "set_runtimeAnimatorController", new[] { typeof(RuntimeAnimatorController) }, false),
            (typeof(AnimatorOverrideController), "set_Item", new[] { typeof(string), typeof(AnimationClip) }, false),
            (typeof(AnimatorOverrideController), "set_Item", new[] { typeof(AnimationClip), typeof(AnimationClip) }, false),
            (typeof(AnimatorOverrideController), nameof(AnimatorOverrideController.ApplyOverrides), new[] { typeof(IList<KeyValuePair<AnimationClip, AnimationClip>>) }, false),
            (typeof(AnimatorOverrideController), "set_runtimeAnimatorController", new[] { typeof(RuntimeAnimatorController) }, false),
        };
        static AnimationEvent Escape() => new AnimationEvent { functionName = "Escape", time = 0f };
        AnimationClip ClipWithEvent(string name)
        {
            AnimationClip clip = Own(new AnimationClip { name = name });
            clip.events = new[] { Escape() };
            return clip;
        }
        MethodInfo Redirect(CilboxBasisCommon box, Type declaringType, string name, params Type[] parameters)
        {
            Assert.IsTrue(Resolve(box, declaringType, name, out MethodInfo method, parameters), $"{box.GetType().Name} refuses {declaringType.Name}.{name}; old scripts would stop loading.");
            Assert.IsNotNull(method, $"{box.GetType().Name} reaches {declaringType.Name}.{name} directly.");
            Assert.AreEqual(Shim, method.DeclaringType.FullName, $"{declaringType.Name}.{name}");
            return method;
        }
        [Test]
        public void EveryEventMember_IsRedirected_ForEveryBoxKind()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                foreach ((Type type, string name, Type[] parameters, _) in Members) Redirect(box, type, name, parameters);
            }
        }
        [Test]
        public void TheDryRun_AsksOnlyForTheMembersThatReviveEvents()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                SortedSet<string> items = DryRun(box, () =>
                {
                    foreach ((Type type, string name, Type[] parameters, _) in Members) Resolve(box, type, name, out _, parameters);
                });
                CollectionAssert.AreEquivalent(new[] { AddEvent, SetEvents, FireEvents }, items, boxType.Name);
            }
        }
        [Test]
        public void WithoutConsent_EventsCannotBeAddedOrTurnedOn()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                Run(box);
                AnimationClip clip = Own(new AnimationClip());
                Redirect(box, typeof(AnimationClip), nameof(AnimationClip.AddEvent), typeof(AnimationEvent)).Invoke(null, new object[] { clip, Escape() });
                Assert.AreEqual(0, clip.events.Length, $"{boxType.Name} added an event");
                Redirect(box, typeof(AnimationClip), "set_events", typeof(AnimationEvent[])).Invoke(null, new object[] { clip, new[] { Escape() } });
                Assert.AreEqual(0, clip.events.Length, $"{boxType.Name} set events");
                AnimationClip stripped = ClipWithEvent("stripped");
                Redirect(box, typeof(AnimationClip), "set_events", typeof(AnimationEvent[])).Invoke(null, new object[] { stripped, new AnimationEvent[0] });
                Assert.AreEqual(0, stripped.events.Length, $"{boxType.Name} could not clear events");
                Animator animator = Own(new GameObject("animator")).AddComponent<Animator>();
                animator.fireEvents = false;
                MethodInfo fire = Redirect(box, typeof(Animator), "set_fireEvents", typeof(bool));
                fire.Invoke(null, new object[] { animator, true });
                Assert.IsFalse(animator.fireEvents, $"{boxType.Name} turned events back on");
                animator.fireEvents = true;
                fire.Invoke(null, new object[] { animator, false });
                Assert.IsFalse(animator.fireEvents, $"{boxType.Name} could not turn events off");
                box.interpreterAccountingDepth = 0;
            }
        }
        [Test]
        public void WithConsent_EventsCanBeAddedAndTurnedOn()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            CilboxBasisCommon box = Box(typeof(CilboxPropBasis));
            Grant(box, AddEvent, SetEvents, FireEvents);
            Run(box);
            AnimationClip clip = Own(new AnimationClip());
            Redirect(box, typeof(AnimationClip), nameof(AnimationClip.AddEvent), typeof(AnimationEvent)).Invoke(null, new object[] { clip, Escape() });
            Assert.AreEqual(1, clip.events.Length);
            Redirect(box, typeof(AnimationClip), "set_events", typeof(AnimationEvent[])).Invoke(null, new object[] { clip, new[] { Escape(), Escape() } });
            Assert.AreEqual(2, clip.events.Length);
            Animator animator = Own(new GameObject("animator")).AddComponent<Animator>();
            animator.fireEvents = false;
            Redirect(box, typeof(Animator), "set_fireEvents", typeof(bool)).Invoke(null, new object[] { animator, true });
            Assert.IsTrue(animator.fireEvents);
        }
        [Test]
        public void SwappingControllersOrClips_StripsTheirEvents()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                Run(box);
                AnimationClip baseClip = ClipWithEvent("base");
                AnimatorController controller = Own(new AnimatorController { name = "controller" });
                controller.AddLayer("Base");
                controller.AddMotion(baseClip);
                Animator animator = Own(new GameObject("animator")).AddComponent<Animator>();
                animator.fireEvents = true;
                Redirect(box, typeof(Animator), "set_runtimeAnimatorController", typeof(RuntimeAnimatorController)).Invoke(null, new object[] { animator, controller });
                Assert.AreSame(controller, animator.runtimeAnimatorController, boxType.Name);
                Assert.AreEqual(0, baseClip.events.Length, $"{boxType.Name} assigned a controller with events");
                Assert.IsFalse(animator.fireEvents, boxType.Name);
                AnimatorOverrideController overrides = Own(new AnimatorOverrideController());
                AnimationClip wrapped = ClipWithEvent("wrapped");
                AnimatorController other = Own(new AnimatorController { name = "other" });
                other.AddLayer("Base");
                other.AddMotion(wrapped);
                Redirect(box, typeof(AnimatorOverrideController), "set_runtimeAnimatorController", typeof(RuntimeAnimatorController)).Invoke(null, new object[] { overrides, other });
                Assert.AreSame(other, overrides.runtimeAnimatorController, boxType.Name);
                Assert.AreEqual(0, wrapped.events.Length, $"{boxType.Name} wrapped a controller with events");
                AnimationClip byClip = ClipWithEvent("by clip");
                Redirect(box, typeof(AnimatorOverrideController), "set_Item", typeof(AnimationClip), typeof(AnimationClip)).Invoke(null, new object[] { overrides, wrapped, byClip });
                Assert.AreEqual(0, byClip.events.Length, $"{boxType.Name} overrode with an evented clip");
                AnimationClip byName = ClipWithEvent("by name");
                Redirect(box, typeof(AnimatorOverrideController), "set_Item", typeof(string), typeof(AnimationClip)).Invoke(null, new object[] { overrides, wrapped.name, byName });
                Assert.AreEqual(0, byName.events.Length, $"{boxType.Name} overrode by name with an evented clip");
                AnimationClip applied = ClipWithEvent("applied");
                IList<KeyValuePair<AnimationClip, AnimationClip>> pairs = new List<KeyValuePair<AnimationClip, AnimationClip>> { new KeyValuePair<AnimationClip, AnimationClip>(wrapped, applied) };
                Redirect(box, typeof(AnimatorOverrideController), nameof(AnimatorOverrideController.ApplyOverrides), typeof(IList<KeyValuePair<AnimationClip, AnimationClip>>)).Invoke(null, new object[] { overrides, pairs });
                Assert.AreEqual(0, applied.events.Length, $"{boxType.Name} applied an evented override");
                box.interpreterAccountingDepth = 0;
            }
        }
        [Test]
        public void ThePromptMarksAnimationEventsSevere()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[] { AddEvent, SetEvents, FireEvents });
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.animationEvents"), lines[0].Text);
            Assert.IsTrue(lines[0].Severe);
        }
    }
}
