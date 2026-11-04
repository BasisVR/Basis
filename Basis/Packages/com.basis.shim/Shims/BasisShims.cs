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
using UrlSecurity = Basis.Scripts.Common.BasisUrlSecurity;

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
		/// <summary>Returns the active BasisVR UI language code, not the OS language.</summary>
		public static string GetCurrentLanguage()
		{
			return BasisLocalization.CurrentLanguage;
		}

		public static int GetExecutionBudgetUs( object self )
		{
			return (int)((CilboxProxy)self).box.timeoutLengthUs;
		}

		public static int GetRemainingExecutionBudgetUs( object self )
		{
			Cilbox.Cilbox box = ((CilboxProxy)self).box;
			long remainingTicks = box.interpreterAccountingDropDead - System.Diagnostics.Stopwatch.GetTimestamp();
			return remainingTicks > 0 ? (int)(remainingTicks / box.interpreterTicksInUs) : 0;
		}

		public static int GetLastFrameExecutionUs( object self )
		{
			return (int)((CilboxProxy)self).box.usSpentLastFrame;
		}

		// Read-only identity of the content (avatar, prop or scene) the calling script belongs to.
		// Resolved from the script's own object, so a script can only ask about its own content.
		// Creator links (Authorship.Contact), the bundle hash, CRC and password stay withheld.
		static BasisContentBase ContentOf(object self) => self is Component c && BasisContent.TryResolve(c, out BasisContentBase content) && CilboxBasisConsent.TryRunning(out CilboxBasisCommon box) && CilboxBasisConsent.TryOwner(box, box.gameObject, out BasisContentBase owner) && ReferenceEquals(content, owner) ? content : null;

		/// <summary>Stable id shared by every build and version of this content, or "" for content built before group ids existed.</summary>
		public static string GetContentGroupId(object self) => ContentOf(self)?.BasisBundleDescription?.ContentGroupId ?? string.Empty;

		/// <summary>Network id of this spawn, or "" until the server has assigned one. Also resolves for scene scripts.</summary>
		public static string GetContentNetworkId(object self) => ContentOf(self) is BasisContentBase content && content.TryGetNetworkGUIDIdentifier(out string id) ? id : string.Empty;

		/// <summary>Creator-facing name of this content.</summary>
		public static string GetContentName(object self) => ContentOf(self)?.BasisBundleDescription?.AssetBundleName ?? string.Empty;

		/// <summary>Author credit of this content.</summary>
		public static string GetContentAuthor(object self) => ContentOf(self)?.BasisBundleDescription?.Authorship.Author ?? string.Empty;

		/// <summary>Copy of this content's creator-declared tags; empty when none were declared.</summary>
		public static string[] GetContentTags(object self)
		{
			string[] tags = ContentOf(self)?.BasisBundleDescription?.Tags;
			return tags == null ? Array.Empty<string>() : (string[])tags.Clone();
		}

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

			if( !UrlSecurity.IsHttpUrlAllowed( stringUrl, out string reason ) )
			{
				callback( new IBasisImageDownload( null, null, "Security Failure: " + reason ) );
				return;
			}

			string dnsReason = await UrlSecurity.ValidateResolvedHostAsync( stringUrl );
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
								BasisTrustedUrls.AddDomain( uri );
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

			if( !UrlSecurity.IsHttpUrlAllowed( stringUrl, out string reason ) )
			{
				callback( new IBasisStringDownload( null, "Security Failure: " + reason ) );
				return;
			}

			string dnsReason = await UrlSecurity.ValidateResolvedHostAsync( stringUrl );
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


}
