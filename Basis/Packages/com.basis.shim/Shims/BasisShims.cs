using Basis.Shims;
using Basis.Scripts.BasisSdk;
using Basis.BasisUI;
using System;
using System.Collections.Generic;
using UnityEngine.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
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
		public static void AddEventTrigger(Component target, EventTriggerType eventType, UnityAction<BaseEventData> callback)
		{
			if (target == null || callback == null)
			{
				return;
			}
			EventTrigger eventTrigger = null;
			if (!target.TryGetComponent<EventTrigger>(out eventTrigger))
			{
				return;
			}

			EventTrigger.Entry entry = new EventTrigger.Entry
			{
				eventID = eventType,
				callback = new EventTrigger.TriggerEvent()
			};

			entry.callback.AddListener(callback);

			if (eventTrigger.triggers == null)
			{
				eventTrigger.triggers = new List<EventTrigger.Entry>();
			}

			eventTrigger.triggers.Add(entry);
		}
		public static BasisNetworkShim MakeNetworkable( object o )
		{
			if (o is not MonoBehaviour behaviour)
			{
				Debug.LogError( $"Object {o} is not a MonoBehaviour and cannot be made networkable." );
				return null;
			}

			return MakeNetworkable( behaviour );
		}

		public static BasisNetworkShim MakeNetworkable( MonoBehaviour mb )
		{
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

		public async void DownloadImage( string stringUrl, Action< IBasisImageDownload > callback )
		{
			try
			{
				await DownloadImageAsync( stringUrl, callback );
			}
			catch( Exception e )
			{
				Debug.LogError( $"[BasisImageDownloader] Download failed: {e}" );
			}
		}

		private async System.Threading.Tasks.Task DownloadImageAsync( string stringUrl, Action< IBasisImageDownload > callback )
		{
			if( callback == null ) return;

			if( !BasisMediaPlayerSecurity.IsUrlAllowed( stringUrl, out string reason ) )
			{
				callback( new IBasisImageDownload( null, null, "Security Failure: " + reason ) );
				return;
			}

			string dnsReason = await BasisMediaPlayerSecurity.ValidateResolvedHostAsync( stringUrl );
			if( dnsReason != null )
			{
				callback( new IBasisImageDownload( null, null, "Security Failure: " + dnsReason ) );
				return;
			}

			UnityWebRequest www = new UnityWebRequest( stringUrl );
			www.redirectLimit = 0;

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


	// Text/whitelist download result. Mirrors IBasisImageDownload for plain string payloads.
	public class IBasisStringDownload
	{
		public IBasisStringDownload( UnityWebRequest www, String majorFailure )
		{
			if (www != null && www.result == UnityWebRequest.Result.Success)
			{
				Success = true;
				Error = "";
				Result = www.downloadHandler != null ? www.downloadHandler.text : "";
			}
			else
			{
				Success = false;
				Error = (www != null) ? www.error : majorFailure;
				Result = null;
			}
		}
		public bool   Success { get; set; }
		public String Error { get; set; }
		public String Result { get; set; }
	}


	// Native (non-boxed) helper that lets sandboxed scripts fetch a remote text file over http(s)
	// without exposing raw UnityWebRequest. Mirrors BasisImageDownloader.
	public class BasisStringDownloader
	{
		System.Collections.Generic.HashSet< UnityWebRequest > InFlight = new System.Collections.Generic.HashSet< UnityWebRequest >();

		public void DownloadString( string stringUrl, Action< IBasisStringDownload > callback )
		{
			if( string.IsNullOrEmpty( stringUrl ) ||
				( !stringUrl.StartsWith( "http://" ) && !stringUrl.StartsWith( "https://" ) ) )
			{
				callback( new IBasisStringDownload( null, "Security Failure" ) );
				return;
			}

			// Gate the fetch behind the same user-consent flow the video player uses: trusted URLs
			// download immediately, untrusted ones must be approved via the URL prompt panel.
			if( BasisTrustedUrls.IsTrusted( stringUrl ) )
			{
				BeginRequest( stringUrl, callback );
				return;
			}

			Uri uri;
			try
			{
				uri = new Uri( stringUrl );
			}
			catch( Exception e )
			{
				Debug.LogError( $"[BasisStringDownloader] Failed to parse URL: {e}" );
				callback( new IBasisStringDownload( null, "Invalid URL" ) );
				return;
			}

			if( !BasisNotificationCenter.RouteToNotifications ) BasisMainMenu.Open();
			BasisMenuURLPromptPanel.CreateNew(
				stringUrl,
				response =>
				{
					if( !response.Accepted )
					{
						callback( new IBasisStringDownload( null, "User declined URL" ) );
						return;
					}
					if( response.RememberChoice )
					{
						switch( response.Scope )
						{
							case BasisMenuURLPromptPanel.RememberChoiceScope.URL:
								BasisTrustedUrls.Add( stringUrl );
								break;
							case BasisMenuURLPromptPanel.RememberChoiceScope.Hostname:
								BasisTrustedUrls.Add( uri.Scheme + "://" + uri.Host + "/*" );
								break;
							case BasisMenuURLPromptPanel.RememberChoiceScope.Domain:
								string[] parts = uri.Host.Split( '.' );
								string domain = parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : uri.Host;
								BasisTrustedUrls.Add( uri.Scheme + "://*." + domain + "/*" );
								break;
						}
					}
					BeginRequest( stringUrl, callback );
				},
				divertible: true
			);
		}

		private async void BeginRequest( string stringUrl, Action< IBasisStringDownload > callback )
		{
			try
			{
				await BeginRequestAsync( stringUrl, callback );
			}
			catch( Exception e )
			{
				Debug.LogError( $"[BasisStringDownloader] Download failed: {e}" );
			}
		}

		private async System.Threading.Tasks.Task BeginRequestAsync( string stringUrl, Action< IBasisStringDownload > callback )
		{
			if( callback == null ) return;

			if( !BasisMediaPlayerSecurity.IsUrlAllowed( stringUrl, out string reason ) )
			{
				callback( new IBasisStringDownload( null, "Security Failure: " + reason ) );
				return;
			}

			string dnsReason = await BasisMediaPlayerSecurity.ValidateResolvedHostAsync( stringUrl );
			if( dnsReason != null )
			{
				callback( new IBasisStringDownload( null, "Security Failure: " + dnsReason ) );
				return;
			}

			UnityWebRequest www = UnityWebRequest.Get( stringUrl );
			www.redirectLimit = 0;

			bool bCompleted = false;
			UnityWebRequestAsyncOperation req = www.SendWebRequest();
			InFlight.Add( www );

			Action <AsyncOperation> eventcb = (AsyncOperation obj) => {
				if( !bCompleted )
				{
					bCompleted = true;
					InFlight.Remove( www );
					callback( new IBasisStringDownload( www, null ) );
				}
			};

			req.completed += eventcb;

			if( !bCompleted && req.isDone )
			{
				eventcb( null );
			}
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
