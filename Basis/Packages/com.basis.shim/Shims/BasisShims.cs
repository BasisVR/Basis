using Basis.Shims;
using Basis.Scripts.BasisSdk;
using System;
using UnityEngine.Networking;
using UnityEngine;
using Cilbox;

namespace Basis
{
	// PUBLIC FACING

	[Serializable]
	public class BasisUrl
	{
		// XXX TODO: Make sure cilbox can't change this.
		//public String Get() { return strUrl; }
		//[SerializeField] private String strUrl;
		[field:SerializeField] public String url { get; private set; }
	};

	public class SafeUtil
	{
		[Obsolete("Use GetComponent instead. This is a shim for the old system and should be removed at a later point.")]

		public static IBasisNetworkShimCompatible MakeNetworkable( object o )
		{
			if (o is not MonoBehaviour behaviour)
			{
				Debug.LogError( $"Object {o} is not a MonoBehaviour and cannot be made networkable." );
				return null;
			}

			return MakeNetworkable( behaviour );
		}
		[Obsolete("Use GetComponent instead. This is a shim for the old system and should be removed at a later point.")]
		public static IBasisNetworkShimCompatible MakeNetworkable( MonoBehaviour mb )
		{
			return MakeNetworkShim( mb );
		}

		public static IBasisNetworkShimCompatible MakeNetworkShim( object o )
		{
			if (o is not MonoBehaviour behaviour)
			{
				Debug.LogError( $"Object {o} is not a MonoBehaviour and cannot be made networkable." );
				return null;
			}

			return MakeNetworkShim( behaviour );
		}

		public static IBasisNetworkShimCompatible MakeNetworkShim( MonoBehaviour mb )
		{
			GameObject avatar = BasisAvatar.GetGameObject( mb.gameObject );
			if( avatar != null )
			{
				BasisAvatarNetworkShim[] avatarShims = avatar.GetComponents<BasisAvatarNetworkShim>();
				foreach( BasisAvatarNetworkShim existingShim in avatarShims )
				{
					if( existingShim.IsAssignedTo( mb ) )
					{
						existingShim.EnsureNetworkAssigned();
						return existingShim;
					}
				}

				BasisAvatarNetworkShim avatarShim = avatar.AddComponent<BasisAvatarNetworkShim>();
				avatarShim.AssignScriptOwner( mb );
				avatarShim.EnsureNetworkAssigned();
				return avatarShim;
			}

			if( mb.gameObject.TryGetComponent<BasisNetworkShim>( out BasisNetworkShim bi ) ) return bi;

			return mb.gameObject.AddComponent<BasisNetworkShim>();
		}

		[Obsolete("Use the direct interactable component instead. This is a shim for the old system and should be removed at a later point.")]
		public static BasisInteractableShim MakeInteractable( object o )
		{
			// Actually needs to be CilboxProxies.
			GameObject go = null;
			if( o is CilboxProxy )
				go = ((CilboxProxy)o).gameObject;
			else
				go = ((MonoBehaviour)o).gameObject;

			BasisInteractableShim bi;
			if( go.TryGetComponent<BasisInteractableShim>( out bi ) ) return bi;

			return go.AddComponent<BasisInteractableShim>();
		}
	}


	public class IBasisImageDownload
	{
		public IBasisImageDownload( UnityWebRequest www, DownloadHandlerTexture dht, String majorFailure )
		{
			if (www != null && www.result == UnityWebRequest.Result.Success)
			{
				Success = true;
				Error = "";
				Result = dht.texture;
				SizeInMemoryBytes = dht.data.Length;
			}
			else
			{
				Success = false;
				Error = (dht != null) ? dht.error : majorFailure;
				Result = null;
			}
		}
		public bool    Success { get; set; }
		public String  Error { get; set; }
		public int     SizeInMemoryBytes { get; set; }
		public Texture Result { get; set; }
	}


	public class BasisImageDownloader
	{
		System.Collections.Generic.HashSet< UnityWebRequest > InFlight = new System.Collections.Generic.HashSet< UnityWebRequest >();

		public void DownloadImage( string stringUrl, Action< IBasisImageDownload > callback )
		{
			if( stringUrl.Substring(0, 7) != "http://" && stringUrl.Substring(0, 8) != "https://" )
			{
				callback( new IBasisImageDownload( null, null, "Security Failure" ) );
				return;
			}

			UnityWebRequest www = new UnityWebRequest( stringUrl );

			/////////////////////////////////////////////////////////////////
			DownloadHandlerTexture dht = new DownloadHandlerTexture(true);
			www.downloadHandler = dht;
			/////////////////////////////////////////////////////////////////

			bool bCompleted = false;

			UnityWebRequestAsyncOperation req = www.SendWebRequest();

			InFlight.Add( www );

			Action <AsyncOperation> eventcb = (AsyncOperation obj) => {
				if( !bCompleted )
				{
					bCompleted = true;
					InFlight.Remove( www );
					callback( new IBasisImageDownload( www, dht, null ) );
				}
			};

			req.completed += eventcb;

			if( !bCompleted && req.isDone )
			{
				eventcb( null );
			}

			return;
		}

		public void Dispose()
		{
			foreach( var www in InFlight )
			{
				www.Dispose();
			}
		}
	}




#if false
// If we ever allow raw.
	public class BasisImageDownloader
	{
		public void DownloadImage( BasisUrl stringUrl, Action callback, TextureInfo rgbInfo)
		{
			UnityWebRequest www = UnityWebRequest.Get( stringUrl.Get() );
			UnityWebRequestAsyncOperation req = www.SendWebRequest();

			bool bCompleted = false;

			req.completed += (AsyncOperation obj) => {
				if( !bCompleted )
				{
					bCompleted = true;
					DownloadHandler dh = www.downloadHandler;
					callback( www.result == UnityWebRequest.Result.Success, dh.error, dh.GetData() );
				}
			};

			if( !bCompleted && req.isDone )
			{
				req.completed( null );
			}
		}
	};
#endif

}
