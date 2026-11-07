using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	/// <summary>
	/// Calls <c>OnSpawned()</c> on the scripts beside it whenever the client spawns or respawns the
	/// local player, and once on opt-in if the local player is already in the world. Fetching the
	/// component is the opt-in. With the scene's Override Spawning set, the client leaves the player
	/// where they are and the callback places them.
	/// </summary>
	public class BasisSpawnEventShim : CilboxShim
	{
		public const string CallbackName = "OnSpawned";

		private class Binding
		{
			public CilboxProxy Proxy;
			public CilboxMethod Method;
			public int Delivered;
		}

		private readonly List<Binding> bindings = new List<Binding>();
		private readonly List<CilboxProxy> proxies = new List<CilboxProxy>();
		private Action handler;
		private Coroutine delivery;
		private WaitForSeconds retryInterval;
		private bool optedIn = false;
		private int spawns;

		private void OnEnable()
		{
			handler ??= Spawned;
			BasisSceneFactory.OnSpawnedEvent -= handler;
			BasisSceneFactory.OnSpawnedEvent += handler;
			if( !optedIn )
			{
				optedIn = true;
				// Before the local player is ready, its first spawn is still to come and counts then.
				if( BasisLocalPlayer.PlayerReady ) spawns++;
			}
			QueueDelivery();
		}

		private void OnDisable()
		{
			if( handler != null ) BasisSceneFactory.OnSpawnedEvent -= handler;
			if( delivery != null ) StopCoroutine( delivery );
			delivery = null;
		}

		public void Rebind()
		{
			QueueDelivery();
		}

		private void Spawned()
		{
			spawns++;
			QueueDelivery();
		}

		private void QueueDelivery()
		{
			if( delivery == null && isActiveAndEnabled ) delivery = StartCoroutine( Deliver() );
		}

		// Each script beside the shim gets the latest spawn once it can run. A script can be held
		// back (disabled until the player allows the world's scripts) or not loaded yet, so this
		// checks again until every one is up to date.
		private IEnumerator Deliver()
		{
			yield return null;
			while( !DeliverOwed() )
			{
				retryInterval ??= new WaitForSeconds( 0.25f );
				yield return retryInterval;
			}
			delivery = null;
		}

		private bool DeliverOwed()
		{
			if( spawns == 0 ) return true;
			bool waiting = Bind();
			int bindingCount = bindings.Count;
			for( int i = 0; i < bindingCount; i++ )
			{
				Binding binding = bindings[i];
				CilboxProxy p = binding.Proxy;
				if( binding.Delivered >= spawns || p == null || p.disabled ) continue;
				if( !p.isActiveAndEnabled ) { waiting = true; continue; }
				binding.Delivered = spawns;
				try
				{
					binding.Method.Interpret( p, null );
				}
				catch( Exception e )
				{
					BasisDebug.LogError( e, BasisDebug.LogTag.Shims );
				}
			}
			if( waiting ) return false;
			// A callback can spawn the player again; that spawn is owed too.
			for( int i = 0; i < bindingCount; i++ )
			{
				CilboxProxy p = bindings[i].Proxy;
				if( bindings[i].Delivered < spawns && p != null && !p.disabled ) return false;
			}
			return true;
		}

		// Adds a binding for each world script beside the shim that has an OnSpawned() and returns
		// true while one may still be on its way: none yet, or one that has not loaded.
		private bool Bind()
		{
			for( int i = bindings.Count - 1; i >= 0; i-- )
			{
				if( bindings[i].Proxy == null ) bindings.RemoveAt( i );
			}
			GetComponents( proxies );
			int proxyCount = proxies.Count;
			bool waiting = proxyCount == 0;
			for( int i = 0; i < proxyCount; i++ )
			{
				CilboxProxy p = proxies[i];
				// Spawn events are for world scripts only, even if a prop script adds this component.
				if( p == null || p.disabled || !( p.box is CilboxSceneBasis ) || IsBound( p ) ) continue;
				CilboxClass cls = p.cls;
				if( cls == null ) { waiting = true; continue; }
				if( cls.methodNameToIndex == null ) continue;
				uint idx;
				if( !cls.methodNameToIndex.TryGetValue( CallbackName, out idx ) ) continue;
				CilboxMethod m = cls.methods[idx];
				if( m.isStatic ) continue;
				int parameterCount = m.signatureParameters != null ? m.signatureParameters.Length : 0;
				if( parameterCount != 0 ) continue;
				bindings.Add( new Binding { Proxy = p, Method = m } );
			}
			return waiting;
		}

		private bool IsBound( CilboxProxy p )
		{
			int bindingCount = bindings.Count;
			for( int i = 0; i < bindingCount; i++ )
			{
				if( bindings[i].Proxy == p ) return true;
			}
			return false;
		}
	}
}
