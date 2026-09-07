using System;
using System.Collections.Generic;
using UnityEngine;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;

namespace Basis.Shims
{
	/// <summary>
	/// Roster access and pose reads for sandboxed scripts. Players ARE <see cref="IBasisPlayer"/> —
	/// there is no parallel wrapper type. Identity comes straight off the interface
	/// (<c>player.DisplayName</c>, <c>player.PlayerPlatform</c>, <c>player.IsLocal</c>, …); this
	/// class only supplies what the interface deliberately withholds.
	///
	/// The withheld part is pose. Reading where someone is would otherwise mean handing over
	/// <c>AvatarTransform</c> or <c>BasisAvatar</c>, and a Transform is a write handle — a script
	/// could move, reparent or rescale another player. So the position and bone accessors here take
	/// a player and return a COPIED Vector3/Quaternion; the Transform never crosses the boundary.
	///
	/// The per-player accessors are extension methods, so they read as members:
	/// <code>
	/// foreach( IBasisPlayer p in BasisPlayersShim.All )
	///     if( !p.IsConsideredFallBackAvatar )
	///         Spawn( p.GetBonePosition( HumanBodyBones.Head ) );
	/// </code>
	/// </summary>
	public static class BasisPlayersShim
	{
		private static IBasisPlayer[] cached = Array.Empty<IBasisPlayer>();
		private static ushort[] cachedIds = Array.Empty<ushort>();
		private static string[] cachedNames = Array.Empty<string>();
		private static string[] cachedPlatforms = Array.Empty<string>();
		private static bool dirty = true;

		/// <summary>Number of players currently in the instance, including the local player.</summary>
		public static int Count => BasisNetworkPlayer.GetPlayerCount();

		/// <summary>Every player in the instance. Rebuilt only when someone joins or leaves.</summary>
		public static IBasisPlayer[] All
		{
			get { Rebuild(); return cached; }
		}

		/// <summary>The local player, or null before the local player has joined.</summary>
		public static IBasisPlayer Local
		{
			get
			{
				Rebuild();
				for( int i = 0; i < cached.Length; i++ )
					if( cached[i].IsLocal ) return cached[i];
				return null;
			}
		}

		/// <summary>Display names, index-matched with <see cref="All"/>.</summary>
		public static string[] DisplayNames
		{
			get { Rebuild(); return cachedNames; }
		}

		/// <summary>Reported platforms, index-matched with <see cref="All"/>.</summary>
		public static string[] Platforms
		{
			get { Rebuild(); return cachedPlatforms; }
		}

		/// <summary>Look a player up by network id. Returns null when that id is not present.</summary>
		public static IBasisPlayer GetById( ushort playerId )
		{
			Rebuild();
			for( int i = 0; i < cachedIds.Length; i++ )
				if( cachedIds[i] == playerId ) return cached[i];
			return null;
		}

		/// <summary>First player whose display name matches exactly, or null.</summary>
		public static IBasisPlayer GetByName( string displayName )
		{
			if( string.IsNullOrEmpty( displayName ) ) return null;
			Rebuild();
			for( int i = 0; i < cached.Length; i++ )
				if( cachedNames[i] == displayName ) return cached[i];
			return null;
		}

		/// <summary>How many players reported the given platform string, compared case-insensitively.</summary>
		public static int CountOnPlatform( string platform )
		{
			if( string.IsNullOrEmpty( platform ) ) return 0;
			Rebuild();
			int n = 0;
			for( int i = 0; i < cachedPlatforms.Length; i++ )
				if( string.Equals( cachedPlatforms[i], platform, StringComparison.OrdinalIgnoreCase ) ) n++;
			return n;
		}

		/// <summary>Nearest player to a world point, or null when nobody has a pose yet.</summary>
		public static IBasisPlayer Nearest( Vector3 point )
		{
			Rebuild();
			IBasisPlayer best = null;
			float bestDistance = float.MaxValue;
			for( int i = 0; i < cached.Length; i++ )
			{
				float d = cached[i].DistanceTo( point );
				if( d < 0f || d >= bestDistance ) continue;
				bestDistance = d;
				best = cached[i];
			}
			return best;
		}

		/// <summary>Players whose avatar root is within <paramref name="radius"/> of a world point.</summary>
		public static IBasisPlayer[] Within( Vector3 point, float radius )
		{
			Rebuild();
			List<IBasisPlayer> hits = new List<IBasisPlayer>();
			for( int i = 0; i < cached.Length; i++ )
			{
				float d = cached[i].DistanceTo( point );
				if( d >= 0f && d <= radius ) hits.Add( cached[i] );
			}
			return hits.ToArray();
		}

		//////////////////////////////////////////////////////////////////////////////////
		// PER-PLAYER  ///////////////////////////////////////////////////////////////////
		//////////////////////////////////////////////////////////////////////////////////

		/// <summary>Network id for this player, stable for their connection. 0 if they are gone.</summary>
		public static ushort GetPlayerId( this IBasisPlayer player )
		{
			return TryGetPlayerId( player, out ushort playerId ) ? playerId : (ushort)0;
		}

		/// <summary>Network id for this player. False when they are gone; id 0 is a real player, never a blank.</summary>
		public static bool TryGetPlayerId( this IBasisPlayer player, out ushort playerId )
		{
			playerId = 0;
			if( !Alive( player ) ) return false;
			Rebuild();
			for( int i = 0; i < cached.Length; i++ )
			{
				if( !ReferenceEquals( cached[i], player ) ) continue;
				playerId = cachedIds[i];
				return true;
			}
			return false;
		}

		/// <summary>True once the avatar exists and has finished setting up. Bone reads return zero until then.</summary>
		public static bool IsAvatarLoaded( this IBasisPlayer player )
		{
			BasisAvatar a = Avatar( player );
			return a != null && a.IsReady;
		}

		/// <summary>Humanoid scale of the worn avatar, 1 at default height. 0 when no avatar is loaded.</summary>
		public static float GetAvatarScale( this IBasisPlayer player )
		{
			BasisAvatar a = Avatar( player );
			return a != null ? a.HumanScale : 0f;
		}

		/// <summary>World position of the avatar root, or zero when they have no pose yet.</summary>
		public static Vector3 GetPosition( this IBasisPlayer player )
		{
			Transform t = Anchor( player );
			return t != null ? t.position : Vector3.zero;
		}

		/// <summary>World rotation of the avatar root, or identity when they have no pose yet.</summary>
		public static Quaternion GetRotation( this IBasisPlayer player )
		{
			Transform t = Anchor( player );
			return t != null ? t.rotation : Quaternion.identity;
		}

		/// <summary>
		/// World position of a humanoid bone, or zero when the avatar is missing that bone or has
		/// not loaded. A copy — there is no way to reach the Transform behind it.
		/// </summary>
		public static Vector3 GetBonePosition( this IBasisPlayer player, HumanBodyBones bone )
		{
			Transform t = Bone( player, bone );
			return t != null ? t.position : Vector3.zero;
		}

		/// <summary>World rotation of a humanoid bone, or identity when unavailable.</summary>
		public static Quaternion GetBoneRotation( this IBasisPlayer player, HumanBodyBones bone )
		{
			Transform t = Bone( player, bone );
			return t != null ? t.rotation : Quaternion.identity;
		}

		/// <summary>Distance from a world point to the avatar root. Returns -1 when they have no pose yet.</summary>
		public static float DistanceTo( this IBasisPlayer player, Vector3 point )
		{
			Transform t = Anchor( player );
			return t != null ? Vector3.Distance( t.position, point ) : -1f;
		}

		//////////////////////////////////////////////////////////////////////////////////

		private static bool Alive( IBasisPlayer player )
		{
			return player != null && !player.IsDestroyed;
		}

		private static BasisAvatar Avatar( IBasisPlayer player )
		{
			if( !Alive( player ) ) return null;
			BasisAvatar a = player.BasisAvatar;
			return a != null ? a : null;
		}

		private static Transform Anchor( IBasisPlayer player )
		{
			if( !Alive( player ) ) return null;
			Transform t = player.AvatarTransform;
			// Remote players fall back to the mouth marker, their only anchor before an avatar lands.
			return t != null ? t : player.Transform;
		}

		private static Transform Bone( IBasisPlayer player, HumanBodyBones bone )
		{
			BasisAvatar a = Avatar( player );
			BasisAvatarTransformStorage storage = a != null ? a.TransformStorage : null;
			return storage != null ? storage.Get( bone ) : null;
		}

		private static void Rebuild()
		{
			// The join/leave events cover the normal paths; the count check catches a roster that
			// moved without one firing (teardown between scenes, a player added during load).
			if( !dirty && cached.Length == BasisNetworkPlayer.GetPlayerCount() ) return;
			dirty = false;

			// Re-attached on every rebuild rather than hooked once, so the subscription survives a
			// network teardown that clears the static event fields between instances.
			BasisNetworkPlayer.OnPlayerJoined -= MarkDirty;
			BasisNetworkPlayer.OnPlayerJoined += MarkDirty;
			BasisNetworkPlayer.OnPlayerLeft -= MarkDirty;
			BasisNetworkPlayer.OnPlayerLeft += MarkDirty;

			BasisNetworkPlayer[] players = BasisNetworkPlayer.GetAllPlayers();
			List<IBasisPlayer> built = new List<IBasisPlayer>( players.Length );
			List<ushort> ids = new List<ushort>( players.Length );
			for( int i = 0; i < players.Length; i++ )
			{
				BasisNetworkPlayer np = players[i];
				IBasisPlayer p = np != null ? np.Player : null;
				if( p == null || p.IsDestroyed ) continue;
				built.Add( p );
				ids.Add( np.playerId );
			}

			cached = built.ToArray();
			cachedIds = ids.ToArray();
			cachedNames = new string[cached.Length];
			cachedPlatforms = new string[cached.Length];
			for( int i = 0; i < cached.Length; i++ )
			{
				string name = cached[i].SafeDisplayName;
				if( string.IsNullOrEmpty( name ) ) name = cached[i].DisplayName;
				cachedNames[i] = name ?? string.Empty;
				cachedPlatforms[i] = cached[i].PlayerPlatform ?? string.Empty;
			}
		}

		private static void MarkDirty( BasisNetworkPlayer player ) { dirty = true; }
	}
}
