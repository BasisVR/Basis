using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	public class BasisPlatformEventShim : CilboxShim
	{
		public const string CallbackName = "OnPlatformChanged";

		private struct Binding
		{
			public CilboxProxy Proxy;
			public CilboxMethod Method;
			public bool WantsArguments;
		}

		private readonly List<Binding> bindings = new List<Binding>();
		private readonly object[] arguments = new object[3];
		private Action handler;
		private bool bound = false;
		private bool dispatchedInitial = false;
		private bool dispatchPending;
		private int dispatchedFrame = -1;

		private void OnEnable()
		{
			Bind();
			handler ??= Changed;
			BasisPlatformDetection.OnChanged -= handler;
			BasisPlatformDetection.OnChanged += handler;
			if( !dispatchedInitial ) StartCoroutine( DispatchInitial() );
		}

		private void OnDisable()
		{
			if( handler != null ) BasisPlatformDetection.OnChanged -= handler;
			dispatchPending = false;
		}

		public void Rebind()
		{
			bound = false;
			Bind();
		}

		private IEnumerator DispatchInitial()
		{
			yield return null;
			if( dispatchedInitial ) yield break;
			dispatchedInitial = true;
			Dispatch();
		}

		private void Changed()
		{
			dispatchedInitial = true;
			if( dispatchPending ) return;
			dispatchPending = true;
			StartCoroutine( DispatchCoalesced() );
		}

		private IEnumerator DispatchCoalesced()
		{
			yield return null;
			if( !dispatchPending ) yield break;
			dispatchPending = false;
			Dispatch();
		}

		private void Bind()
		{
			if( bound ) return;
			bound = true;
			bindings.Clear();
			CilboxProxy[] proxies = GetComponents<CilboxProxy>();
			for( int i = 0; i < proxies.Length; i++ )
			{
				CilboxProxy p = proxies[i];
				CilboxClass cls = p != null ? p.cls : null;
				if( cls == null || cls.methodNameToIndex == null ) continue;
				uint idx;
				if( !cls.methodNameToIndex.TryGetValue( CallbackName, out idx ) ) continue;
				CilboxMethod m = cls.methods[idx];
				if( m.isStatic ) continue;
				int parameterCount = m.signatureParameters != null ? m.signatureParameters.Length : 0;
				if( parameterCount != 0 && parameterCount != 3 ) continue;
				bindings.Add( new Binding { Proxy = p, Method = m, WantsArguments = parameterCount == 3 } );
			}
		}

		private void Dispatch()
		{
			if( bindings.Count == 0 ) return;
			if( dispatchedFrame == Time.frameCount ) return;
			dispatchedFrame = Time.frameCount;
			arguments[0] = BasisPlatformDetection.CurrentMode;
			arguments[1] = BasisPlatformDetection.IsVR;
			arguments[2] = BasisPlatformDetection.IsHeadsetWorn;
			for( int i = 0; i < bindings.Count; i++ )
			{
				Binding binding = bindings[i];
				CilboxProxy p = binding.Proxy;
				if( p == null || p.disabled || !p.enabled ) continue;
				try
				{
					binding.Method.Interpret( p, binding.WantsArguments ? arguments : null );
				}
				catch( Exception e )
				{
					Debug.LogException( e );
				}
			}
		}
	}
}
