using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using BasisPermissions;
using UnityEngine;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace Basis.Shims
{
	/// <summary>
	/// Permission and role checks for sandboxed scripts — "am I staff", "is that player staff".
	///
	/// The two halves are not symmetric, because the data isn't. The local player's own permission
	/// nodes arrive with the join handshake and sit in memory, so <see cref="LocalHasPermission"/>
	/// and friends answer immediately. Nobody else's do: the server never broadcasts a peer's
	/// permissions, and the full table is gated behind basis.permissions.view. So every question
	/// about another player is a round trip, and the answer arrives on a callback.
	///
	/// <code>
	/// // gate a door on the local player
	/// if( BasisPermissionsShim.LocalIsModerator ) door.SetActive( true );
	///
	/// // badge someone else — the answer lands a frame or two later
	/// BasisPermissionsShim.IsAdmin( player, isAdmin =&gt; badge.SetActive( isAdmin ) );
	/// </code>
	///
	/// This tells a world who the staff in the room are, which is what a staff badge or a
	/// moderator-only door needs — and also what a hostile world would use to behave differently
	/// while a moderator is watching. That trade is deliberate and lives here rather than in the
	/// whitelist so it is in one readable place.
	///
	/// Nothing here is a write handle: every method returns a bool, and a UUID never crosses the
	/// boundary. Players are named by the <see cref="IBasisPlayer"/> the roster already handed out.
	/// </summary>
	public static class BasisPermissionsShim
	{
		/// <summary>How long an answer about another player is reused before asking again.</summary>
		private const float CacheSeconds = 5f;

		/// <summary>How long a question waits for an answer before failing closed.</summary>
		private const float TimeoutSeconds = 10f;

		/// <summary>Questions that may be outstanding at once. Past this, new ones answer false.</summary>
		private const int MaxInFlight = 32;

		/// <summary>Cached answers kept before the whole cache is dropped. Bounds a script that asks about everything.</summary>
		private const int MaxCacheEntries = 256;

		/// <summary>Longest node or group name a query may name. Matches the server's own cap.</summary>
		private const int MaxValueLength = 128;

		private struct CachedAnswer
		{
			public bool Held;
			public float ExpiresAt;
		}

		private struct PendingQuestion
		{
			public List<Action<bool>> Waiting;
			public float DeadlineAt;
		}

		private static readonly Dictionary<string, CachedAnswer> Answers = new Dictionary<string, CachedAnswer>();
		private static readonly Dictionary<string, PendingQuestion> InFlight = new Dictionary<string, PendingQuestion>();
		private static readonly List<string> Expired = new List<string>();

		//////////////////////////////////////////////////////////////////////////////////
		// LOCAL PLAYER  /////////////////////////////////////////////////////////////////
		//////////////////////////////////////////////////////////////////////////////////

		/// <summary>
		/// Whether the local player holds a permission node, e.g. "basis.moderation.kick".
		/// Wildcards the server granted are already resolved, so a "*" admin answers true to
		/// every known node. Answers immediately — this is data the client already holds.
		/// </summary>
		public static bool LocalHasPermission( string node )
		{
			if( string.IsNullOrEmpty( node ) ) return false;
			HashSet<string> permissions = BasisNetworkManagement.LocalPermissions;
			return permissions != null && permissions.Contains( node );
		}

		/// <summary>
		/// Whether the local player is an admin. Same node the client's own Settings panel uses to
		/// decide whether to show its Admin tab, so a world and the menu never disagree.
		/// </summary>
		public static bool LocalIsAdmin => LocalHasPermission( PermNodes.PermissionsView );

		/// <summary>
		/// Whether the local player is a moderator — the node behind the Settings Moderator tab.
		/// Admins are not implicitly moderators; a server grants the two separately.
		/// </summary>
		public static bool LocalIsModerator => LocalHasPermission( PermNodes.PlayerModeration );

		/// <summary>
		/// Every permission node the local player holds, as a fresh copy. Empty before the join
		/// handshake completes.
		/// </summary>
		public static string[] LocalPermissionNodes
		{
			get
			{
				HashSet<string> permissions = BasisNetworkManagement.LocalPermissions;
				if( permissions == null || permissions.Count == 0 ) return Array.Empty<string>();
				string[] copy = new string[permissions.Count];
				permissions.CopyTo( copy );
				return copy;
			}
		}

		//////////////////////////////////////////////////////////////////////////////////
		// ANOTHER PLAYER  ///////////////////////////////////////////////////////////////
		//////////////////////////////////////////////////////////////////////////////////

		/// <summary>
		/// Whether a player holds a permission node. The callback always runs exactly once, and
		/// may run before this method returns — a cached answer, a question about the local
		/// player, or a rejected question all answer on the spot. Anything the server cannot or
		/// will not answer comes back false, including a player who has left and a question that
		/// timed out, so a script that gates on the result fails closed.
		/// </summary>
		public static void HasPermission( IBasisPlayer player, string node, Action<bool> callback )
		{
			Ask( player, AdminPermissionQueryKind.Node, node, callback );
		}

		/// <summary>
		/// Whether a player belongs to a permission group ("role"), counting groups inherited
		/// through a parent chain. Unlike the node check this always asks the server, even about
		/// the local player: the client is told its own nodes, never its own group names.
		/// </summary>
		public static void IsInGroup( IBasisPlayer player, string group, Action<bool> callback )
		{
			Ask( player, AdminPermissionQueryKind.Group, group, callback );
		}

		/// <summary>Whether a player is an admin. Same node as <see cref="LocalIsAdmin"/>.</summary>
		public static void IsAdmin( IBasisPlayer player, Action<bool> callback )
		{
			Ask( player, AdminPermissionQueryKind.Node, PermNodes.PermissionsView, callback );
		}

		/// <summary>Whether a player is a moderator. Same node as <see cref="LocalIsModerator"/>.</summary>
		public static void IsModerator( IBasisPlayer player, Action<bool> callback )
		{
			Ask( player, AdminPermissionQueryKind.Node, PermNodes.PlayerModeration, callback );
		}

		/// <summary>
		/// Drop every cached answer, so the next question goes to the server. Worth calling after
		/// a world promotes or demotes someone; otherwise the cache turns over on its own.
		/// </summary>
		public static void ClearCache()
		{
			Answers.Clear();
		}

		//////////////////////////////////////////////////////////////////////////////////

		private static void Ask( IBasisPlayer player, AdminPermissionQueryKind kind, string value, Action<bool> callback )
		{
			if( callback == null ) return;

			if( player == null || player.IsDestroyed ||
				string.IsNullOrEmpty( value ) || value.Length > MaxValueLength )
			{
				callback( false );
				return;
			}

			// A node question about ourselves is already answered in memory. A group question is
			// not — LocalPermissions carries nodes only — so that one still goes to the server.
			if( player.IsLocal && kind == AdminPermissionQueryKind.Node )
			{
				callback( LocalHasPermission( value ) );
				return;
			}

			ushort playerId = player.GetPlayerId();
			if( playerId == 0 )
			{
				callback( false );
				return;
			}

			Sweep();

			string key = Key( playerId, kind, value );

			if( Answers.TryGetValue( key, out CachedAnswer cached ) )
			{
				callback( cached.Held );
				return;
			}

			// Same question already on the wire: wait on that answer rather than sending a second
			// copy. The reply echoes the question, so one answer settles every waiter.
			if( InFlight.TryGetValue( key, out PendingQuestion pending ) )
			{
				pending.Waiting.Add( callback );
				return;
			}

			if( InFlight.Count >= MaxInFlight )
			{
				callback( false );
				return;
			}

			InFlight[key] = new PendingQuestion
			{
				Waiting = new List<Action<bool>> { callback },
				DeadlineAt = Time.realtimeSinceStartup + TimeoutSeconds,
			};

			// Re-attached rather than hooked once, so the subscription survives a network teardown
			// that clears the static event field between instances.
			BasisNetworkModeration.OnPermissionQueryResult -= OnAnswer;
			BasisNetworkModeration.OnPermissionQueryResult += OnAnswer;

			if( kind == AdminPermissionQueryKind.Group )
			{
				BasisNetworkModeration.QueryPermissionGroup( playerId, value );
			}
			else
			{
				BasisNetworkModeration.QueryPermissionNode( playerId, value );
			}
		}

		private static void OnAnswer( BasisNetworkModeration.PermissionQueryResult result )
		{
			string key = Key( result.PlayerId, result.Kind, result.Value );

			if( Answers.Count >= MaxCacheEntries ) Answers.Clear();
			Answers[key] = new CachedAnswer
			{
				Held = result.Held,
				ExpiresAt = Time.realtimeSinceStartup + CacheSeconds,
			};

			if( !InFlight.TryGetValue( key, out PendingQuestion pending ) ) return;
			InFlight.Remove( key );
			Deliver( pending.Waiting, result.Held );
		}

		/// <summary>
		/// Expires stale answers and fails out questions the server never answered — it drops what
		/// is over its rate limit silently, so a timeout is a normal outcome, not a fault. Driven
		/// off the ask path rather than a per-frame tick: nothing here needs to happen while no
		/// script is asking.
		/// </summary>
		private static void Sweep()
		{
			float now = Time.realtimeSinceStartup;

			if( Answers.Count > 0 )
			{
				Expired.Clear();
				foreach( KeyValuePair<string, CachedAnswer> entry in Answers )
					if( entry.Value.ExpiresAt <= now ) Expired.Add( entry.Key );
				for( int i = 0; i < Expired.Count; i++ ) Answers.Remove( Expired[i] );
				Expired.Clear();
			}

			if( InFlight.Count == 0 ) return;

			// Timed-out waiters are pulled out and answered from a local list, never the shared
			// one: answering runs script code, which can ask again and re-enter this sweep.
			List<PendingQuestion> timedOut = null;
			foreach( KeyValuePair<string, PendingQuestion> entry in InFlight )
			{
				if( entry.Value.DeadlineAt > now ) continue;
				( timedOut ??= new List<PendingQuestion>() ).Add( entry.Value );
				Expired.Add( entry.Key );
			}

			if( timedOut == null ) return;

			for( int i = 0; i < Expired.Count; i++ ) InFlight.Remove( Expired[i] );
			Expired.Clear();

			for( int i = 0; i < timedOut.Count; i++ ) Deliver( timedOut[i].Waiting, false );
		}

		/// <summary>
		/// Answers every waiter, and keeps going if one throws. A waiter is interpreted script
		/// code; one bad callback must not strand the rest on a question that will never be asked
		/// again, because the answer is now cached. Logged unreported — a world script throwing is
		/// remote content misbehaving, and reporting it would fan a per-frame throw out to disk
		/// and the server.
		/// </summary>
		private static void Deliver( List<Action<bool>> waiting, bool held )
		{
			for( int i = 0; i < waiting.Count; i++ )
			{
				try
				{
					waiting[i]( held );
				}
				catch( Exception e )
				{
					BasisDebug.LogErrorUnreported( $"[BasisPermissionsShim] Permission callback threw: {e}", BasisDebug.LogTag.Shims );
				}
			}
		}

		private static string Key( ushort playerId, AdminPermissionQueryKind kind, string value )
		{
			return $"{playerId}|{(byte)kind}|{value}";
		}
	}
}
