using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Shims
{
	public static class BasisCameraShim
	{
		public const int WorldRendersPerFrame = 4;
		public const int PropRendersPerFrame = 1;
		public static int RemainingRenders(object self) => Cilbox.CilboxCameras.RemainingRenders(self);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxCameras
	{
		private sealed class Budget
		{
			internal CilboxBasisCommon box;
			internal int frame = -1, used;
			internal bool warned;
		}
		private static readonly List<Budget> budgets = new List<Budget>();
		private static int renderDepth;
		private static bool hooked;
		[NoAutoStaticsCleanup] private static readonly Action<ScriptableRenderContext, List<Camera>> beginHandler = (c, l) => renderDepth++;
		[NoAutoStaticsCleanup] private static readonly Action<ScriptableRenderContext, List<Camera>> endHandler = (c, l) => renderDepth = Math.Max(0, renderDepth - 1);
		internal static bool IsRedirect(CilboxBasisCommon box, Type declaringType, string name)
		{
			if (declaringType != typeof(Camera)) return false;
			if (box is CilboxPropBasis) return name == nameof(Camera.Render) || name == "set_cullingMask" || name == "set_orthographicSize";
			return box is CilboxSceneBasis && name == nameof(Camera.Render);
		}
		internal static MethodInfo Resolve(CilboxBasisCommon box, string name, SerializedTypeDescriptor[] parametersIn)
		{
			if (box.usage == null) return null;
			Type[] parameters = box.usage.DescriptorsToArrayOfNativeTypes(parametersIn);
			foreach (Type parameter in parameters) if (parameter == null) return null;
			Type[] withTarget = new Type[parameters.Length + 1];
			withTarget[0] = typeof(Camera);
			Array.Copy(parameters, 0, withTarget, 1, parameters.Length);
			string target = name == nameof(Camera.Render) ? nameof(Render) : name == "set_cullingMask" ? nameof(SetCullingMask) : nameof(SetOrthographicSize);
			MethodInfo method = typeof(CilboxCameras).GetMethod(target, BindingFlags.Static | BindingFlags.NonPublic, null, withTarget, null);
			if (method != null) Track(box);
			return method;
		}
		private static Budget Track(CilboxBasisCommon box)
		{
			for (int i = budgets.Count - 1; i >= 0; i--)
			{
				if (budgets[i].box == null) budgets.RemoveAt(i);
				else if (ReferenceEquals(budgets[i].box, box)) return budgets[i];
			}
			Budget budget = new Budget { box = box };
			budgets.Add(budget);
			if (!hooked)
			{
				hooked = true;
				RenderPipelineManager.beginContextRendering -= beginHandler;
				RenderPipelineManager.endContextRendering -= endHandler;
				RenderPipelineManager.beginContextRendering += beginHandler;
				RenderPipelineManager.endContextRendering += endHandler;
			}
			return budget;
		}
		private static Budget Running()
		{
			Budget found = null;
			for (int i = budgets.Count - 1; i >= 0; i--)
			{
				Budget budget = budgets[i];
				if (budget.box == null)
				{
					budgets.RemoveAt(i);
					continue;
				}
				if (budget.box.disabled || budget.box.interpreterAccountingDepth == 0) continue;
				if (found != null) return null;
				found = budget;
			}
			return found;
		}
		private static int Limit(CilboxBasisCommon box) => box is CilboxSceneBasis ? Basis.Shims.BasisCameraShim.WorldRendersPerFrame : box is CilboxPropBasis ? Basis.Shims.BasisCameraShim.PropRendersPerFrame : 0;
		internal static int RemainingRenders(object self)
		{
			CilboxBasisCommon box = (self as CilboxProxy)?.box as CilboxBasisCommon;
			if (box == null) return 0;
			Budget budget = Track(box);
			return budget.frame == Time.frameCount ? Math.Max(0, Limit(box) - budget.used) : Limit(box);
		}
		private static bool Owns(CilboxBasisCommon box, Camera camera)
		{
			if (camera == null || camera == Camera.main) return false;
			if (camera == Basis.Scripts.Drivers.BasisLocalCameraDriver.CameraInstance || (Basis.Scripts.Drivers.BasisLocalCameraDriver.Instance != null && Basis.Scripts.Drivers.BasisLocalCameraDriver.Instance.Camera == camera)) return false;
			bool resolved = BasisContent.TryResolve(camera.gameObject, out BasisContentBase content);
			if (box is CilboxSceneBasis) return resolved ? content is BasisScene : camera.gameObject.scene == box.gameObject.scene;
			return resolved && box.ownerRecorded && ReferenceEquals(box.ownerContent, content);
		}
		private static Budget Owner(Camera camera, string what)
		{
			Budget budget = Running();
			if (budget == null || !Owns(budget.box, camera)) throw new InvalidOperationException($"Camera.{what} only works on a camera inside the calling content.");
			return budget;
		}
		internal static void Render(Camera camera)
		{
			Budget budget = Owner(camera, nameof(Camera.Render));
			if (camera.targetTexture == null) throw new InvalidOperationException("Camera.Render needs a camera with a target texture.");
			if (renderDepth > 0) throw new InvalidOperationException("Camera.Render cannot be called while a frame is rendering.");
			if (budget.frame != Time.frameCount)
			{
				budget.frame = Time.frameCount;
				budget.used = 0;
			}
			if (budget.used >= Limit(budget.box))
			{
				if (!budget.warned) Debug.LogWarning($"Cilbox content exceeded {Limit(budget.box)} Camera.Render calls this frame; extra renders are skipped.");
				budget.warned = true;
				return;
			}
			budget.used++;
			camera.Render();
		}
		internal static void SetCullingMask(Camera camera, int mask)
		{
			Owner(camera, "cullingMask");
			camera.cullingMask = mask;
		}
		internal static void SetOrthographicSize(Camera camera, float size)
		{
			Owner(camera, "orthographicSize");
			if (float.IsNaN(size) || float.IsInfinity(size)) throw new ArgumentOutOfRangeException(nameof(size));
			camera.orthographicSize = size;
		}
	}
}
