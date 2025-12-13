using Basis;
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

		public static BasisNetworkShim MakeNetworkable( object o )
		{
			// Actually needs to be CilboxProxies.
			GameObject go = null;
			string setGUID = "";
			if( o is CilboxProxy )
			{
				CilboxProxy p = (CilboxProxy)o;
				go = p.gameObject;
				setGUID = p.buildTimeGuid + p.initialLoadPath;
			}
			else
			{
				MonoBehaviour p = (MonoBehaviour)o;
				go = p.gameObject;
				setGUID = p.GetInstanceID().ToString();
			}

			BasisNetworkShim bi;

			if( go.TryGetComponent<BasisNetworkShim>( out bi ) ) return bi;

			bi = go.AddComponent<BasisNetworkShim>();

			bi.AssignNetworkGUIDIdentifier(setGUID);
			Debug.Log( $"ADDING ASSIGN: {bi} {setGUID}");

			return bi;
		}

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

		public void DownloadImage( BasisUrl stringUrl, Action< IBasisImageDownload > callback )
		{
			if( stringUrl.url.Substring(0, 7) != "http://" && stringUrl.url.Substring(0, 8) != "https://" )
			{
				callback( new IBasisImageDownload( null, null, "Security Failure" ) );
				return;
			}

			UnityWebRequest www = new UnityWebRequest( stringUrl.url );

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
