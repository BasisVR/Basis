using System;
using System.Collections;
using System.Collections.Generic;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	/// <summary>
	/// Tells a sandboxed script when the player's graphics settings change, so a world that scales
	/// itself to the machine reacts the moment someone drops to Low instead of only at load.
	///
	/// Declare the callback and opt in once from <c>Start</c>:
	/// <code>
	/// void Start() { GetComponent&lt;BasisGraphicsSettingsEventShim&gt;(); }
	///
	/// void OnGraphicsSettingsChanged( string qualityLevel, int qualityTier )
	/// {
	///     bool cheap = qualityTier &lt;= BasisGraphicsSettingsShim.TierLow;
	///     backgroundDonut.SetActive( !cheap );
	///     RenderSettings.skybox = cheap ? flatSky : cloudSky;
	/// }
	/// </code>
	/// A no-argument version is accepted too, for scripts that would rather read
	/// <see cref="BasisGraphicsSettingsShim"/> themselves. Cilbox creates the component on demand,
	/// so fetching it is the whole opt-in.
	///
	/// The callback also fires **once shortly after opting in**, carrying the settings already in
	/// force, so a script never has to read the starting values separately and never has to poll.
	/// That first call is deferred a frame rather than made inside <c>GetComponent</c>, so it cannot
	/// re-enter a script that is still running its own <c>Start</c>.
	///
	/// <para>Dispatch is **coalesced to at most one call per frame**, and carries no key. That is
	/// deliberate: one Performance Mode level change rewrites dozens of settings in a single batch,
	/// and a per-key callback would either flood interpreted code or silently drop the tail of the
	/// batch once a per-frame cap cut in. One "something changed, re-read what you care about" is
	/// both cheaper and impossible to miss half of.</para>
	///
	/// Callbacks are resolved by name off the script rather than subscribed as a delegate,
	/// deliberately: an interpreted delegate cannot be unsubscribed, and the underlying event
	/// outlives any one world script, so handing it one would leak the whole interpreted object.
	///
	/// This type is method-restricted in <c>CilboxSceneBasis.extraMethodWhitelist</c>,
	/// <c>CilboxAvatarBasis.extraMethodWhitelist</c> and <c>CilboxPropBasis.extraMethodWhitelist</c>.
	/// </summary>
	public class BasisGraphicsSettingsEventShim : CilboxShim
	{
		public const string CallbackName = "OnGraphicsSettingsChanged";

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
		private bool dispatchPending;
		private int dispatchedFrame = -1;

		private void OnEnable()
		{
			Bind();
			handler ??= OnSettingsChanged;

			BasisGraphicsSettingsShim.EnsureWatching();
			BasisGraphicsSettingsShim.OnReadableSettingChanged -= handler;
			BasisGraphicsSettingsShim.OnReadableSettingChanged += handler;

			if( !dispatchedInitial ) StartCoroutine( DispatchInitial() );
		}

		private void OnDisable()
		{
			if( handler != null )
			{
				BasisGraphicsSettingsShim.OnReadableSettingChanged -= handler;
			}
			dispatchPending = false;
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
		/// for this component. Settings are loaded long before world content runs, so the values it
		/// carries are the real ones, not defaults.
		/// </summary>
		private IEnumerator DispatchInitial()
		{
			yield return null;
			if( dispatchedInitial ) yield break;
			dispatchedInitial = true;
			Dispatch();
		}

		/// <summary>
		/// A settings batch fires this once per written key. Only the first one in a frame arms the
		/// dispatch; the rest fold into it, and the callback runs at end of frame having seen the
		/// whole batch rather than each intermediate state.
		/// </summary>
		private void OnSettingsChanged()
		{
			dispatchedInitial = true;
			if( dispatchPending ) return;
			dispatchPending = true;
			StartCoroutine( DispatchCoalesced() );
		}

		/// <summary>
		/// Waits a frame rather than for end of frame: <c>WaitForEndOfFrame</c> never resumes on a
		/// headless client, which would strand the pending flag and silence every later change.
		/// </summary>
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

			// Coalescing already bounds this to one pass per frame; the guard covers the initial
			// dispatch landing in the same frame as a change that armed the coalesced one.
			if( dispatchedFrame == Time.frameCount ) return;
			dispatchedFrame = Time.frameCount;

			arguments[0] = BasisGraphicsSettingsShim.QualityLevel;
			arguments[1] = BasisGraphicsSettingsShim.QualityTier;

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
					// One faulting script must not cost the other proxies on this object their
					// events. Cilbox has already disabled the offender by this point.
					Debug.LogException( e );
				}
			}
		}
	}
}
