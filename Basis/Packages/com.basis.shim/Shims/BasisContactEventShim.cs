using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Contacts;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	public class BasisContactEventShim : CilboxShim
	{
		public const string EnterCallbackName = "OnContactEnter";
		public const string ExitCallbackName = "OnContactExit";
		public const int MaxDispatchesPerFrame = 32;

		private struct Binding
		{
			public CilboxProxy Proxy;
			public CilboxMethod Method;
			public bool WantsArguments;
		}

		private readonly List<Binding> enterBindings = new List<Binding>();
		private readonly List<Binding> exitBindings = new List<Binding>();
		private readonly object[] arguments = new object[2];
		private BasisContactReceiver[] receivers = Array.Empty<BasisContactReceiver>();
		private Action<BasisContactReceiver> beginHandler, endHandler;
		private bool bound = false;
		private int dispatchedThisFrame;
		private int dispatchFrame = -1;

		private void OnEnable()
		{
			Bind();
			Subscribe();
		}

		private void OnDisable()
		{
			Unsubscribe();
		}

		public int GetReceiverCount()
		{
			return receivers.Length;
		}

		public int FindReceiver( string receiverName )
		{
			if( string.IsNullOrEmpty( receiverName ) ) return -1;
			for( int i = 0; i < receivers.Length; i++ )
			{
				if( receivers[i] != null && string.Equals( receivers[i].name, receiverName, StringComparison.Ordinal ) ) return i;
			}
			return -1;
		}

		public float GetValue( int receiverIndex )
		{
			BasisContactReceiver r = Receiver( receiverIndex );
			return r != null ? r.Value : 0f;
		}

		public bool IsTouched( int receiverIndex )
		{
			BasisContactReceiver r = Receiver( receiverIndex );
			return r != null && r.IsTouched;
		}

		public int GetTouchingPlayer( int receiverIndex )
		{
			BasisContactReceiver r = Receiver( receiverIndex );
			return r != null ? r.TouchingPlayerId : BasisContacts.NoPlayer;
		}

		public void Rebind()
		{
			Unsubscribe();
			bound = false;
			Bind();
			if( isActiveAndEnabled ) Subscribe();
		}

		private BasisContactReceiver Receiver( int index )
		{
			return index >= 0 && index < receivers.Length ? receivers[index] : null;
		}

		private void Subscribe()
		{
			beginHandler ??= OnBegin;
			endHandler ??= OnEnd;
			for( int i = 0; i < receivers.Length; i++ )
			{
				if( receivers[i] == null ) continue;
				receivers[i].OnContactBegin -= beginHandler;
				receivers[i].OnContactBegin += beginHandler;
				receivers[i].OnContactEnd -= endHandler;
				receivers[i].OnContactEnd += endHandler;
			}
		}

		private void Unsubscribe()
		{
			if( beginHandler == null ) return;
			for( int i = 0; i < receivers.Length; i++ )
			{
				if( receivers[i] == null ) continue;
				receivers[i].OnContactBegin -= beginHandler;
				receivers[i].OnContactEnd -= endHandler;
			}
		}

		private void Bind()
		{
			if( bound ) return;
			bound = true;
			enterBindings.Clear();
			exitBindings.Clear();
			receivers = GetComponentsInChildren<BasisContactReceiver>( true );

			CilboxProxy[] proxies = GetComponents<CilboxProxy>();
			for( int i = 0; i < proxies.Length; i++ )
			{
				CilboxProxy p = proxies[i];
				CilboxClass cls = p != null ? p.cls : null;
				if( cls == null || cls.methodNameToIndex == null ) continue;

				TryBind( p, cls, EnterCallbackName, enterBindings );
				TryBind( p, cls, ExitCallbackName, exitBindings );
			}
		}

		private static void TryBind( CilboxProxy proxy, CilboxClass cls, string name, List<Binding> into )
		{
			uint idx;
			if( !cls.methodNameToIndex.TryGetValue( name, out idx ) ) return;

			CilboxMethod m = cls.methods[idx];
			if( m.isStatic ) return;
			int parameterCount = m.signatureParameters != null ? m.signatureParameters.Length : 0;
			if( parameterCount != 0 && parameterCount != 2 ) return;

			into.Add( new Binding { Proxy = proxy, Method = m, WantsArguments = parameterCount == 2 } );
		}

		private void OnBegin( BasisContactReceiver receiver )
		{
			Dispatch( enterBindings, receiver );
		}

		private void OnEnd( BasisContactReceiver receiver )
		{
			Dispatch( exitBindings, receiver );
		}

		private void Dispatch( List<Binding> bindings, BasisContactReceiver receiver )
		{
			if( bindings.Count == 0 ) return;

			if( dispatchFrame != Time.frameCount )
			{
				dispatchFrame = Time.frameCount;
				dispatchedThisFrame = 0;
			}
			if( dispatchedThisFrame >= MaxDispatchesPerFrame ) return;

			arguments[0] = Array.IndexOf( receivers, receiver );
			arguments[1] = receiver.LastTouchingPlayerId;

			for( int i = 0; i < bindings.Count; i++ )
			{
				Binding binding = bindings[i];
				CilboxProxy p = binding.Proxy;
				if( p == null || p.disabled || !p.enabled ) continue;
				if( dispatchedThisFrame >= MaxDispatchesPerFrame ) return;
				dispatchedThisFrame++;

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
