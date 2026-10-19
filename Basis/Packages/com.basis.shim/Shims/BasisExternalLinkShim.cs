using System;
using Basis.BasisUI;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisExternalLinkResult
	{
		Opened = 0,
		Declined = 1,
		InvalidUrl = 2,
		Blocked = 3,
		NoInteraction = 4,
		Busy = 5,
		Unsupported = 6,
	}

	public static class BasisExternalLinkShim
	{
		public static bool IsValidUrl( string url ) => BasisExternalLinks.TryParse( url, out _, out _ );

		public static void Open( object self, string url ) => Open( self, url, null );

		public static void Open( object self, string url, Action<BasisExternalLinkResult> onResult )
		{
			CilboxProxy proxy = RunningProxy( self );
			bool running = proxy != null;
			BasisExternalLinks.Request( url, running ? proxy.gameObject : null, result =>
			{
				if( onResult == null || ( running && proxy == null ) ) return;
				onResult( (BasisExternalLinkResult)(int)result );
			} );
		}

		internal static void OpenURL( string url )
		{
			if( BasisExternalLinks.IsAsking || !BasisExternalLinks.TryParse( url, out _, out _ ) ) return;
			BasisExternalLinks.Request( url, RunningBox(), null );
		}

		private static CilboxProxy RunningProxy( object self )
		{
			if( !( self is CilboxProxy proxy ) || proxy == null ) return null;
			if( !( proxy.box is CilboxBasisCommon box ) || box == null || box is CilboxAvatarBasis ) return null;
			return box.interpreterAccountingDepth > 0 ? proxy : null;
		}

		private static GameObject RunningBox()
		{
			GameObject running = null;
			foreach( CilboxBasisCommon box in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>( FindObjectsInactive.Include ) )
			{
				if( box == null || box.interpreterAccountingDepth == 0 ) continue;
				if( running != null || box is CilboxAvatarBasis ) return null;
				running = box.gameObject;
			}
			return running;
		}
	}
}
