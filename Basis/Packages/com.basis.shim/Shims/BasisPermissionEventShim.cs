using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Networking;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	/// <summary>
	/// Tells a sandboxed script when the local player's permissions change — the moment they are
	/// granted or lose admin or moderator — so a staff-only door, badge or control panel does not
	/// have to poll <see cref="BasisPermissionsShim.LocalIsAdmin"/> forever to notice.
	///
	/// Declare the callback and opt in once from <c>Start</c>:
	/// <code>
	/// void Start() { GetComponent&lt;BasisPermissionEventShim&gt;(); }
	/// void OnLocalPermissionsChanged( bool isAdmin, bool isModerator ) { }
	/// </code>
	/// A no-argument version is accepted too, for scripts that would rather read
	/// <see cref="BasisPermissionsShim"/> themselves. Cilbox creates the component on demand, so
	/// fetching it is the whole opt-in.
	///
	/// The callback also fires **once shortly after opting in**, carrying whatever is already known,
	/// so a script never has to read the starting value separately and never has to poll. That first
	/// call is deferred a frame rather than made inside <c>GetComponent</c>, so it cannot re-enter a
	/// script that is still running its own <c>Start</c>.
	///
	/// Permissions arrive with the join handshake, and the local set is data the client already
	/// holds — this is the same signal the client's own Settings panel uses to decide whether to
	/// show its Admin tab, so a world and the menu never disagree. Nothing here is a write handle
	/// and no other player's permissions cross the boundary; that stays behind
	/// <see cref="BasisPermissionsShim.IsAdmin"/>, which asks the server.
	///
	/// Callbacks are resolved by name off the script rather than subscribed as a delegate,
	/// deliberately: an interpreted delegate cannot be unsubscribed, and the underlying event
	/// outlives any one world script, so handing it one would leak the whole interpreted object.
	///
	/// This type is method-restricted in <c>CilboxSceneBasis.extraMethodWhitelist</c>,
	/// <c>CilboxAvatarBasis.extraMethodWhitelist</c> and <c>CilboxPropBasis.extraMethodWhitelist</c>.
	/// </summary>
	public class BasisPermissionEventShim : CilboxShim
	{
		public const string CallbackName = "OnLocalPermissionsChanged";

		/// <summary>Ceiling on callbacks delivered to one object per frame.</summary>
		public const int MaxDispatchesPerFrame = 8;

		private struct Binding
		{
			public CilboxProxy Proxy;
			public CilboxMethod Method;
			public bool WantsArguments;
		}

		private readonly List<Binding> bindings = new List<Binding>();
		private readonly object[] arguments = new object[2];
		private Action handler;
		private bool bound = false;
		private bool dispatchedInitial = false;
		private int dispatchedThisFrame;
		private int dispatchFrame = -1;

		private void OnEnable()
		{
			Bind();
			handler ??= OnPermissionsChanged;

			// A plain static Action field, so guard against double-adding across a disable/enable.
			BasisNetworkManagement.OnlocalPermissionsChanged -= handler;
			BasisNetworkManagement.OnlocalPermissionsChanged += handler;

			if( !dispatchedInitial ) StartCoroutine( DispatchInitial() );
		}

		private void OnDisable()
		{
			if( handler != null )
			{
				BasisNetworkManagement.OnlocalPermissionsChanged -= handler;
			}
		}

		/// <summary>
		/// Re-scans this GameObject for interpreted classes declaring the callback. Only needed if
		/// proxies appear after the shim; the scan otherwise happens once when it is enabled.
		/// </summary>
		public void Rebind()
		{
			bound = false;
			Bind();
		}

		/// <summary>
		/// The first call is deferred one frame so it cannot land inside the <c>Start</c> that asked
		/// for this component. The permission set is normally already populated by then — it arrives
		/// with the join handshake, well before world content runs — which is exactly why a script
		/// that only listened to the change event would otherwise miss its own starting state.
		/// </summary>
		private IEnumerator DispatchInitial()
		{
			yield return null;
			if( dispatchedInitial ) yield break;
			dispatchedInitial = true;
			Dispatch();
		}

		private void OnPermissionsChanged()
		{
			dispatchedInitial = true;
			Dispatch();
		}

		private void Bind()
		{
			if( bound ) return;
			bound = true;
			bindings.Clear();

			// One GameObject can carry several cilboxed scripts, each its own proxy.
			CilboxProxy[] proxies = GetComponents<CilboxProxy>();
			for( int i = 0; i < proxies.Length; i++ )
			{
				CilboxProxy p = proxies[i];
				CilboxClass cls = p != null ? p.cls : null;
				if( cls == null || cls.methodNameToIndex == null ) continue;

				uint idx;
				if( !cls.methodNameToIndex.TryGetValue( CallbackName, out idx ) ) continue;

				// Arity is settled here rather than at call time: Interpret() pushes exactly what it
				// is given, so a signature that does not match would corrupt the interpreter stack.
				CilboxMethod m = cls.methods[idx];
				if( m.isStatic ) continue;
				int parameterCount = m.signatureParameters != null ? m.signatureParameters.Length : 0;
				if( parameterCount != 0 && parameterCount != 2 ) continue;

				bindings.Add( new Binding { Proxy = p, Method = m, WantsArguments = parameterCount == 2 } );
			}
		}

		private void Dispatch()
		{
			if( bindings.Count == 0 ) return;

			// Cilbox meters interpreted opcodes, not the native work that reaches them, so the cap on
			// how often content can be re-entered has to live here.
			if( dispatchFrame != Time.frameCount )
			{
				dispatchFrame = Time.frameCount;
				dispatchedThisFrame = 0;
			}
			if( dispatchedThisFrame >= MaxDispatchesPerFrame ) return;

			arguments[0] = BasisPermissionsShim.LocalIsAdmin;
			arguments[1] = BasisPermissionsShim.LocalIsModerator;

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
					// One faulting script must not cost the other proxies on this object their
					// events. Cilbox has already disabled the offender by this point.
					Debug.LogException( e );
				}
			}
		}
	}
}
