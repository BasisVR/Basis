using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxAnimationEvents
	{
		[NoAutoStaticsCleanup] private static readonly AnimationEvent[] none = new AnimationEvent[0];
		private static bool warned;
		internal static readonly string AddEventItem = CilboxBasisCommon.MethodItem(typeof(AnimationClip), nameof(AnimationClip.AddEvent)), SetEventsItem = CilboxBasisCommon.MethodItem(typeof(AnimationClip), "set_events"), FireEventsItem = CilboxBasisCommon.MethodItem(typeof(Animator), "set_fireEvents");
		internal static bool IsRedirect(Type declaringType, string name)
		{
			if (declaringType == typeof(AnimationClip)) return name == nameof(AnimationClip.AddEvent) || name == "set_events";
			if (declaringType == typeof(Animator)) return name == "set_fireEvents" || name == "set_runtimeAnimatorController";
			if (declaringType == typeof(AnimatorOverrideController)) return name == "set_Item" || name == nameof(AnimatorOverrideController.ApplyOverrides) || name == "set_runtimeAnimatorController";
			return false;
		}
		internal static bool NeedsConsent(Type declaringType, string name) => declaringType == typeof(AnimationClip) || (declaringType == typeof(Animator) && name == "set_fireEvents");
		internal static MethodInfo Resolve(CilboxUsage usage, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn)
		{
			if (usage == null) return null;
			Type[] parameters = usage.DescriptorsToArrayOfNativeTypes(parametersIn);
			if (parameters == null) return null;
			foreach (Type parameter in parameters) if (parameter == null) return null;
			Type[] withTarget = new Type[parameters.Length + 1];
			withTarget[0] = declaringType;
			Array.Copy(parameters, 0, withTarget, 1, parameters.Length);
			return typeof(CilboxAnimationEvents).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, withTarget, null);
		}
		private static bool Granted(string item) => CilboxBasisConsent.TryRunning(out CilboxBasisCommon box) && CilboxBasisConsent.Permits(box, item);
		private static bool AnyGranted() => CilboxBasisConsent.TryRunning(out CilboxBasisCommon box) && (CilboxBasisConsent.Permits(box, AddEventItem) || CilboxBasisConsent.Permits(box, SetEventsItem) || CilboxBasisConsent.Permits(box, FireEventsItem));
		private static void Refused(string what)
		{
			if (warned) return;
			warned = true;
			Debug.LogWarning($"[Cilbox] A script tried to {what} without permission; animation events stay off for scripts.");
		}
		internal static void AddEvent(AnimationClip clip, AnimationEvent evt)
		{
			if (clip == null) throw new NullReferenceException();
			if (Granted(AddEventItem)) clip.AddEvent(evt);
			else Refused("add an animation event");
		}
		internal static void set_events(AnimationClip clip, AnimationEvent[] events)
		{
			if (clip == null) throw new NullReferenceException();
			if (events == null || events.Length == 0) clip.events = none;
			else if (Granted(SetEventsItem)) clip.events = events;
			else Refused("set animation events");
		}
		internal static void set_fireEvents(Animator animator, bool value)
		{
			if (animator == null) throw new NullReferenceException();
			if (!value || Granted(FireEventsItem)) animator.fireEvents = value;
			else Refused("turn animation events back on");
		}
		internal static void set_runtimeAnimatorController(Animator animator, RuntimeAnimatorController controller)
		{
			if (animator == null) throw new NullReferenceException();
			if (!AnyGranted())
			{
				Strip(controller);
				animator.fireEvents = false;
			}
			animator.runtimeAnimatorController = controller;
		}
		internal static void set_runtimeAnimatorController(AnimatorOverrideController overrides, RuntimeAnimatorController controller)
		{
			if (overrides == null) throw new NullReferenceException();
			if (!AnyGranted()) Strip(controller);
			overrides.runtimeAnimatorController = controller;
		}
		internal static void set_Item(AnimatorOverrideController overrides, string name, AnimationClip clip)
		{
			if (overrides == null) throw new NullReferenceException();
			if (!AnyGranted()) Strip(clip);
			overrides[name] = clip;
		}
		internal static void set_Item(AnimatorOverrideController overrides, AnimationClip original, AnimationClip clip)
		{
			if (overrides == null) throw new NullReferenceException();
			if (!AnyGranted()) Strip(clip);
			overrides[original] = clip;
		}
		internal static void ApplyOverrides(AnimatorOverrideController overrides, IList<KeyValuePair<AnimationClip, AnimationClip>> pairs)
		{
			if (overrides == null) throw new NullReferenceException();
			if (pairs != null && !AnyGranted())
			{
				foreach (KeyValuePair<AnimationClip, AnimationClip> pair in pairs) Strip(pair.Value);
			}
			overrides.ApplyOverrides(pairs);
		}
		internal static void Strip(RuntimeAnimatorController controller)
		{
			for (int safety = 8; controller != null && safety-- > 0;)
			{
				AnimationClip[] clips = controller.animationClips;
				if (clips != null) foreach (AnimationClip clip in clips) Strip(clip);
				if (!(controller is AnimatorOverrideController overrides)) return;
				RuntimeAnimatorController next = overrides.runtimeAnimatorController;
				if (ReferenceEquals(next, controller)) return;
				controller = next;
			}
		}
		internal static void Strip(AnimationClip clip)
		{
			if (clip != null && clip.events.Length > 0) clip.events = none;
		}
	}
}
