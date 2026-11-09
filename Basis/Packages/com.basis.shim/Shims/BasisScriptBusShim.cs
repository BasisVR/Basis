using System;
using System.Collections.Generic;
using System.Globalization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisScriptBus
	{
		public const int KindWorld = 0, KindProp = 1, KindAvatar = 2;
		public const int FromWorld = 1, FromProps = 2, FromAvatars = 4, FromAnyone = 7;
		public const int TypeNone = 0, TypeText = 1, TypeNumber = 2, TypeBool = 3, TypeBytes = 4, TypeVector2 = 5, TypeVector3 = 6, TypeVector4 = 7, TypeQuaternion = 8, TypeColor = 9, TypeInts = 10, TypeFloats = 11, TypeStrings = 12, TypeVector3s = 13, TypeTransform = 14, TypeScript = 15, TypeTarget = 16;
		public const int MaxNameLength = 64, MaxTextLength = 4096, MaxDataLength = 65536, MaxArrayLength = 4096, MaxValues = 64, MaxPublishedBytes = 262144, MaxListeners = 32, MaxQueued = 256, MaxSendsPerSecond = 120, MaxBytesPerSecond = 1048576;
		public const int MaxTags = 16, MaxTagLength = 32, MaxDescriptionLength = 256;
		public static BasisScriptTarget Self(object self) => CilboxScriptBus.Self(self);
		public static BasisScriptTarget World => CilboxScriptBus.Target(BasisScene.Instance);
		public static BasisScriptTarget LocalAvatar => CilboxScriptBus.Target(BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.BasisAvatar : null);
		public static BasisScriptTarget Of(GameObject gameObject) => CilboxScriptBus.Of(gameObject);
		public static BasisScriptTarget Of(Component component) => component != null ? CilboxScriptBus.Of(component.gameObject) : null;
		public static BasisScriptTarget AvatarOf(ushort playerId) => CilboxScriptBus.AvatarOf(playerId);
		public static BasisScriptTarget[] All => CilboxScriptBus.All(null, null);
		public static BasisScriptTarget[] Listening(string channel) => CilboxScriptBus.All(channel, null);
		public static BasisScriptTarget[] Tagged(string tag) => CilboxScriptBus.All(null, tag);
		public static bool Listen(object self, string channel, Action<BasisScriptMessage> handler) => CilboxScriptBus.Listen(self, channel, FromAnyone, handler);
		public static bool Listen(object self, string channel, int from, Action<BasisScriptMessage> handler) => CilboxScriptBus.Listen(self, channel, from, handler);
		public static void Unlisten(object self, string channel) => CilboxScriptBus.Unlisten(self, channel);
		public static int Send(object self, BasisScriptTarget target, string channel, string text) => CilboxScriptBus.Send(self, target, channel, text, null, false);
		public static int Send(object self, BasisScriptTarget target, string channel, string text, object value) => CilboxScriptBus.Send(self, target, channel, text, value, true);
		public static int SendToScript(object self, BasisScriptReference script, string channel, string text) => CilboxScriptBus.SendToScript(self, script, channel, text, null, false);
		public static int SendToScript(object self, BasisScriptReference script, string channel, string text, object value) => CilboxScriptBus.SendToScript(self, script, channel, text, value, true);
		public static int Broadcast(object self, string channel, string text) => CilboxScriptBus.Send(self, null, channel, text, null, false);
		public static int Broadcast(object self, string channel, string text, object value) => CilboxScriptBus.Send(self, null, channel, text, value, true);
		public static bool Publish(object self, string key, object value) => CilboxScriptBus.Publish(self, key, value);
		public static void Unpublish(object self, string key) => CilboxScriptBus.Unpublish(self, key);
		public static bool Tag(object self, string tag) => CilboxScriptBus.Tag(self, tag, true);
		public static bool Untag(object self, string tag) => CilboxScriptBus.Tag(self, tag, false);
		public static bool Describe(object self, string description) => CilboxScriptBus.Describe(self, null, description);
		public static bool Describe(object self, string name, string description) => CilboxScriptBus.Describe(self, name, description);
		public static BasisSharedTransform Share(object self, Transform transform, bool writable) => CilboxScriptBus.Share(self, transform, writable);
		public static void Unshare(object self, Transform transform) => CilboxScriptBus.Unshare(self, transform);
	}

	public sealed class BasisScriptTarget
	{
		internal CilboxScriptBus.Entry Entry;
		internal BasisScriptTarget() { }
		public bool IsValid => CilboxScriptBus.Live(Entry);
		public int Kind => Entry.Kind;
		public string Name => IsValid ? Entry.Name : string.Empty;
		public bool HasScripts => IsValid && CilboxScriptBus.HasScripts(Entry);
		public bool BelongsToLocalPlayer => IsValid && CilboxScriptBus.Local(Entry);
		public int OwnerPlayerId => IsValid ? CilboxScriptBus.OwnerId(Entry) : -1;
		public string NetworkId => IsValid && Entry.Content != null && Entry.Content.TryGetNetworkGUIDIdentifier(out string id) && id != null ? id : string.Empty;
		public Vector3 Position => IsValid ? Entry.Root.position : Vector3.zero;
		public Quaternion Rotation => IsValid ? Entry.Root.rotation : Quaternion.identity;
		public string Description => IsValid ? Entry.Description : string.Empty;
		public string[] Tags => IsValid ? Entry.Tags.ToArray() : Array.Empty<string>();
		public string[] Keys => IsValid ? CilboxScriptBus.KeysOf(Entry) : Array.Empty<string>();
		public string[] Channels => IsValid ? CilboxScriptBus.ChannelsOf(Entry) : Array.Empty<string>();
		public bool HasTag(string tag) => IsValid && CilboxScriptBus.HasTag(Entry, tag);
		public string DescriptionOf(string name) => IsValid && name != null && Entry.Descriptions.TryGetValue(name, out string text) ? text : string.Empty;
		public bool Listens(string channel) => IsValid && CilboxScriptBus.Listens(Entry, channel);
		public bool HasValue(string key) => Find(key, out _);
		public int TypeOf(string key) => Find(key, out CilboxScriptBus.Value value) ? value.Type : BasisScriptBus.TypeNone;
		public string GetString(string key, string fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsString(value, fallback) : fallback;
		public float GetFloat(string key, float fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsFloat(value, fallback) : fallback;
		public int GetInt(string key, int fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsInt(value, fallback) : fallback;
		public bool GetBool(string key, bool fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsBool(value, fallback) : fallback;
		public Vector2 GetVector2(string key, Vector2 fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.As(value, BasisScriptBus.TypeVector2, fallback) : fallback;
		public Vector3 GetVector3(string key, Vector3 fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.As(value, BasisScriptBus.TypeVector3, fallback) : fallback;
		public Vector4 GetVector4(string key, Vector4 fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.As(value, BasisScriptBus.TypeVector4, fallback) : fallback;
		public Quaternion GetQuaternion(string key, Quaternion fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.As(value, BasisScriptBus.TypeQuaternion, fallback) : fallback;
		public Color GetColor(string key, Color fallback) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.As(value, BasisScriptBus.TypeColor, fallback) : fallback;
		public byte[] GetBytes(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.Array<byte>(value, BasisScriptBus.TypeBytes, true) : null;
		public int[] GetInts(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.Array<int>(value, BasisScriptBus.TypeInts, true) : null;
		public float[] GetFloats(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.Array<float>(value, BasisScriptBus.TypeFloats, true) : null;
		public string[] GetStrings(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.Array<string>(value, BasisScriptBus.TypeStrings, true) : null;
		public Vector3[] GetVector3s(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.Array<Vector3>(value, BasisScriptBus.TypeVector3s, true) : null;
		public BasisSharedTransform GetSharedTransform(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsShared(value) : null;
		public BasisScriptReference GetScript(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsScript(value) : null;
		public BasisScriptTarget GetTarget(string key) => Find(key, out CilboxScriptBus.Value value) ? CilboxScriptBus.AsTarget(value) : null;
		bool Find(string key, out CilboxScriptBus.Value value)
		{
			value = default;
			return IsValid && key != null && Entry.Values.TryGetValue(key, out value);
		}
	}

	public sealed class BasisScriptMessage
	{
		internal CilboxScriptBus.Listener Receiver;
		internal CilboxScriptBus.Value Value;
		internal BasisScriptMessage() { }
		public int Id { get; internal set; }
		public int ReplyTo { get; internal set; }
		public string Channel { get; internal set; }
		public string Text { get; internal set; }
		public BasisScriptTarget Sender { get; internal set; }
		public BasisScriptReference SenderScript { get; internal set; }
		public int ValueType => Value.Type;
		public byte[] Data => CilboxScriptBus.Array<byte>(Value, BasisScriptBus.TypeBytes, false);
		public string GetString(string fallback) => CilboxScriptBus.AsString(Value, fallback);
		public float GetFloat(float fallback) => CilboxScriptBus.AsFloat(Value, fallback);
		public int GetInt(int fallback) => CilboxScriptBus.AsInt(Value, fallback);
		public bool GetBool(bool fallback) => CilboxScriptBus.AsBool(Value, fallback);
		public Vector2 GetVector2(Vector2 fallback) => CilboxScriptBus.As(Value, BasisScriptBus.TypeVector2, fallback);
		public Vector3 GetVector3(Vector3 fallback) => CilboxScriptBus.As(Value, BasisScriptBus.TypeVector3, fallback);
		public Vector4 GetVector4(Vector4 fallback) => CilboxScriptBus.As(Value, BasisScriptBus.TypeVector4, fallback);
		public Quaternion GetQuaternion(Quaternion fallback) => CilboxScriptBus.As(Value, BasisScriptBus.TypeQuaternion, fallback);
		public Color GetColor(Color fallback) => CilboxScriptBus.As(Value, BasisScriptBus.TypeColor, fallback);
		public byte[] GetBytes() => CilboxScriptBus.Array<byte>(Value, BasisScriptBus.TypeBytes, false);
		public int[] GetInts() => CilboxScriptBus.Array<int>(Value, BasisScriptBus.TypeInts, false);
		public float[] GetFloats() => CilboxScriptBus.Array<float>(Value, BasisScriptBus.TypeFloats, false);
		public string[] GetStrings() => CilboxScriptBus.Array<string>(Value, BasisScriptBus.TypeStrings, false);
		public Vector3[] GetVector3s() => CilboxScriptBus.Array<Vector3>(Value, BasisScriptBus.TypeVector3s, false);
		public BasisSharedTransform GetSharedTransform() => CilboxScriptBus.AsShared(Value);
		public BasisScriptReference GetScript() => CilboxScriptBus.AsScript(Value);
		public BasisScriptTarget GetTarget() => CilboxScriptBus.AsTarget(Value);
		public int Reply(string text) => CilboxScriptBus.Reply(this, text, null, false);
		public int Reply(string text, object value) => CilboxScriptBus.Reply(this, text, value, true);
	}

	public sealed class BasisScriptReference
	{
		internal CilboxProxy Proxy;
		internal CilboxBasisCommon Box;
		internal string Class;
		internal bool Removed;
		internal BasisScriptReference() { }
		public bool IsValid => CilboxScriptBus.Live(this);
		public string ClassName => IsValid ? Class : string.Empty;
		public BasisScriptTarget Target => IsValid ? CilboxScriptBus.EntryOf(Box, Proxy)?.Handle : null;
		public bool Listens(string channel) => IsValid && CilboxScriptBus.Listens(this, channel);
		public object Resolve(object self) => IsValid && ReferenceEquals(CilboxScriptBus.Running(self, out _), Box) ? Proxy : null;
	}

	public sealed class BasisSharedTransform
	{
		internal Transform Node;
		internal CilboxScriptBus.Entry From;
		internal string Label;
		internal bool CanWrite, Removed;
		internal BasisSharedTransform() { }
		bool CanEdit => CanWrite && CilboxScriptBus.Live(this);
		public bool IsValid => CilboxScriptBus.Live(this);
		public bool Writable => CanEdit;
		public string Name => IsValid ? Label : string.Empty;
		public BasisScriptTarget Owner => IsValid ? From.Handle : null;
		public Vector3 Position => IsValid ? Node.position : Vector3.zero;
		public Quaternion Rotation => IsValid ? Node.rotation : Quaternion.identity;
		public Vector3 LocalPosition => IsValid ? Node.localPosition : Vector3.zero;
		public Quaternion LocalRotation => IsValid ? Node.localRotation : Quaternion.identity;
		public Vector3 LocalScale => IsValid ? Node.localScale : Vector3.one;
		public Vector3 LossyScale => IsValid ? Node.lossyScale : Vector3.one;
		public Vector3 Forward => IsValid ? Node.forward : Vector3.forward;
		public Vector3 Up => IsValid ? Node.up : Vector3.up;
		public Vector3 Right => IsValid ? Node.right : Vector3.right;
		public bool ActiveSelf => IsValid && Node.gameObject.activeSelf;
		public bool ActiveInHierarchy => IsValid && Node.gameObject.activeInHierarchy;
		public Vector3 TransformPoint(Vector3 point) => IsValid ? Node.TransformPoint(point) : point;
		public Vector3 InverseTransformPoint(Vector3 point) => IsValid ? Node.InverseTransformPoint(point) : point;
		public Vector3 TransformDirection(Vector3 direction) => IsValid ? Node.TransformDirection(direction) : direction;
		public Vector3 InverseTransformDirection(Vector3 direction) => IsValid ? Node.InverseTransformDirection(direction) : direction;
		public bool SetPosition(Vector3 position)
		{
			if (!CanEdit || !CilboxScriptBus.Finite(position)) return false;
			Node.position = position;
			return true;
		}
		public bool SetRotation(Quaternion rotation)
		{
			if (!CanEdit || !CilboxScriptBus.Unit(rotation, out rotation)) return false;
			Node.rotation = rotation;
			return true;
		}
		public bool SetPositionAndRotation(Vector3 position, Quaternion rotation)
		{
			if (!CanEdit || !CilboxScriptBus.Finite(position) || !CilboxScriptBus.Unit(rotation, out rotation)) return false;
			Node.SetPositionAndRotation(position, rotation);
			return true;
		}
		public bool SetLocalPosition(Vector3 position)
		{
			if (!CanEdit || !CilboxScriptBus.Finite(position)) return false;
			Node.localPosition = position;
			return true;
		}
		public bool SetLocalRotation(Quaternion rotation)
		{
			if (!CanEdit || !CilboxScriptBus.Unit(rotation, out rotation)) return false;
			Node.localRotation = rotation;
			return true;
		}
		public bool SetLocalScale(Vector3 scale)
		{
			if (!CanEdit || !CilboxScriptBus.Finite(scale)) return false;
			Node.localScale = scale;
			return true;
		}
		public bool SetActive(bool active) => CanEdit && CilboxScriptBus.QueueActive(this, active);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxScriptBus
	{
		internal const int MaxDeliveriesPerTick = 1024, MaxClassNameLength = 128, MaxPendingActives = 1024, MaxDescriptions = BasisScriptBus.MaxValues + BasisScriptBus.MaxListeners;
		internal struct Value
		{
			internal int Type;
			internal object Item;
			internal double Number;
			internal long Size;
		}
		internal sealed class Entry
		{
			internal object Key;
			internal BasisContentBase Content;
			internal CilboxBasisCommon Box;
			internal Transform Root;
			internal BasisScriptTarget Handle;
			internal string Name, Description = string.Empty;
			internal int Kind, Queued;
			internal bool Removed, HasContent;
			internal double WindowStart;
			internal int WindowSends;
			internal long WindowBytes, PublishedBytes;
			internal readonly Dictionary<string, Value> Values = new Dictionary<string, Value>(StringComparer.Ordinal);
			internal readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
			internal readonly List<string> Tags = new List<string>();
		}
		internal sealed class Listener
		{
			internal Entry Entry;
			internal CilboxProxy Owner;
			internal CilboxBasisCommon Box;
			internal string Channel;
			internal int From;
			internal Action<BasisScriptMessage> Handler;
			internal bool Removed;
		}
		struct PendingActive
		{
			internal BasisSharedTransform Share;
			internal bool Active;
		}
		struct Pending
		{
			internal Listener To;
			internal BasisScriptMessage Message;
		}
		static readonly Dictionary<object, Entry> entries = new Dictionary<object, Entry>();
		static readonly List<Entry> order = new List<Entry>();
		static readonly List<Listener> listeners = new List<Listener>();
		static readonly Dictionary<CilboxProxy, BasisScriptReference> scripts = new Dictionary<CilboxProxy, BasisScriptReference>();
		static readonly List<CilboxProxy> deadScripts = new List<CilboxProxy>();
		static readonly Dictionary<Transform, BasisSharedTransform> readShares = new Dictionary<Transform, BasisSharedTransform>(), writeShares = new Dictionary<Transform, BasisSharedTransform>();
		static readonly List<Transform> deadShares = new List<Transform>();
		static readonly List<PendingActive> actives = new List<PendingActive>();
		static List<Pending> queue = new List<Pending>(), working = new List<Pending>();
		static int nextId;
		static bool ticking;

		internal static int EntryCount => order.Count;
		internal static int ListenerCount => listeners.Count;
		internal static int QueuedCount => queue.Count;

		internal static bool Blocks(Type type, string name)
		{
			if (type == typeof(BasisScriptBus))
			{
				switch (name)
				{
					case nameof(BasisScriptBus.Self):
					case "get_" + nameof(BasisScriptBus.World):
					case "get_" + nameof(BasisScriptBus.LocalAvatar):
					case nameof(BasisScriptBus.Of):
					case nameof(BasisScriptBus.AvatarOf):
					case "get_" + nameof(BasisScriptBus.All):
					case nameof(BasisScriptBus.Listening):
					case nameof(BasisScriptBus.Tagged):
					case nameof(BasisScriptBus.Listen):
					case nameof(BasisScriptBus.Unlisten):
					case nameof(BasisScriptBus.Send):
					case nameof(BasisScriptBus.SendToScript):
					case nameof(BasisScriptBus.Broadcast):
					case nameof(BasisScriptBus.Publish):
					case nameof(BasisScriptBus.Unpublish):
					case nameof(BasisScriptBus.Tag):
					case nameof(BasisScriptBus.Untag):
					case nameof(BasisScriptBus.Describe):
					case nameof(BasisScriptBus.Share):
					case nameof(BasisScriptBus.Unshare):
						return false;
				}
				return true;
			}
			if (type == typeof(BasisScriptTarget))
			{
				switch (name)
				{
					case "get_" + nameof(BasisScriptTarget.IsValid):
					case "get_" + nameof(BasisScriptTarget.Kind):
					case "get_" + nameof(BasisScriptTarget.Name):
					case "get_" + nameof(BasisScriptTarget.HasScripts):
					case "get_" + nameof(BasisScriptTarget.BelongsToLocalPlayer):
					case "get_" + nameof(BasisScriptTarget.OwnerPlayerId):
					case "get_" + nameof(BasisScriptTarget.NetworkId):
					case "get_" + nameof(BasisScriptTarget.Position):
					case "get_" + nameof(BasisScriptTarget.Rotation):
					case "get_" + nameof(BasisScriptTarget.Description):
					case "get_" + nameof(BasisScriptTarget.Tags):
					case "get_" + nameof(BasisScriptTarget.Keys):
					case "get_" + nameof(BasisScriptTarget.Channels):
					case nameof(BasisScriptTarget.HasTag):
					case nameof(BasisScriptTarget.DescriptionOf):
					case nameof(BasisScriptTarget.Listens):
					case nameof(BasisScriptTarget.HasValue):
					case nameof(BasisScriptTarget.TypeOf):
					case nameof(BasisScriptTarget.GetString):
					case nameof(BasisScriptTarget.GetFloat):
					case nameof(BasisScriptTarget.GetInt):
					case nameof(BasisScriptTarget.GetBool):
					case nameof(BasisScriptTarget.GetVector2):
					case nameof(BasisScriptTarget.GetVector3):
					case nameof(BasisScriptTarget.GetVector4):
					case nameof(BasisScriptTarget.GetQuaternion):
					case nameof(BasisScriptTarget.GetColor):
					case nameof(BasisScriptTarget.GetBytes):
					case nameof(BasisScriptTarget.GetInts):
					case nameof(BasisScriptTarget.GetFloats):
					case nameof(BasisScriptTarget.GetStrings):
					case nameof(BasisScriptTarget.GetVector3s):
					case nameof(BasisScriptTarget.GetSharedTransform):
					case nameof(BasisScriptTarget.GetScript):
					case nameof(BasisScriptTarget.GetTarget):
						return false;
				}
				return true;
			}
			if (type == typeof(BasisSharedTransform))
			{
				switch (name)
				{
					case "get_" + nameof(BasisSharedTransform.IsValid):
					case "get_" + nameof(BasisSharedTransform.Writable):
					case "get_" + nameof(BasisSharedTransform.Name):
					case "get_" + nameof(BasisSharedTransform.Owner):
					case "get_" + nameof(BasisSharedTransform.Position):
					case "get_" + nameof(BasisSharedTransform.Rotation):
					case "get_" + nameof(BasisSharedTransform.LocalPosition):
					case "get_" + nameof(BasisSharedTransform.LocalRotation):
					case "get_" + nameof(BasisSharedTransform.LocalScale):
					case "get_" + nameof(BasisSharedTransform.LossyScale):
					case "get_" + nameof(BasisSharedTransform.Forward):
					case "get_" + nameof(BasisSharedTransform.Up):
					case "get_" + nameof(BasisSharedTransform.Right):
					case "get_" + nameof(BasisSharedTransform.ActiveSelf):
					case "get_" + nameof(BasisSharedTransform.ActiveInHierarchy):
					case nameof(BasisSharedTransform.TransformPoint):
					case nameof(BasisSharedTransform.InverseTransformPoint):
					case nameof(BasisSharedTransform.TransformDirection):
					case nameof(BasisSharedTransform.InverseTransformDirection):
					case nameof(BasisSharedTransform.SetPosition):
					case nameof(BasisSharedTransform.SetRotation):
					case nameof(BasisSharedTransform.SetPositionAndRotation):
					case nameof(BasisSharedTransform.SetLocalPosition):
					case nameof(BasisSharedTransform.SetLocalRotation):
					case nameof(BasisSharedTransform.SetLocalScale):
					case nameof(BasisSharedTransform.SetActive):
						return false;
				}
				return true;
			}
			if (type == typeof(BasisScriptReference))
			{
				switch (name)
				{
					case "get_" + nameof(BasisScriptReference.IsValid):
					case "get_" + nameof(BasisScriptReference.ClassName):
					case "get_" + nameof(BasisScriptReference.Target):
					case nameof(BasisScriptReference.Listens):
					case nameof(BasisScriptReference.Resolve):
						return false;
				}
				return true;
			}
			if (type == typeof(BasisScriptMessage))
			{
				switch (name)
				{
					case "get_" + nameof(BasisScriptMessage.Id):
					case "get_" + nameof(BasisScriptMessage.ReplyTo):
					case "get_" + nameof(BasisScriptMessage.Channel):
					case "get_" + nameof(BasisScriptMessage.Text):
					case "get_" + nameof(BasisScriptMessage.Sender):
					case "get_" + nameof(BasisScriptMessage.SenderScript):
					case "get_" + nameof(BasisScriptMessage.ValueType):
					case "get_" + nameof(BasisScriptMessage.Data):
					case nameof(BasisScriptMessage.GetString):
					case nameof(BasisScriptMessage.GetFloat):
					case nameof(BasisScriptMessage.GetInt):
					case nameof(BasisScriptMessage.GetBool):
					case nameof(BasisScriptMessage.GetVector2):
					case nameof(BasisScriptMessage.GetVector3):
					case nameof(BasisScriptMessage.GetVector4):
					case nameof(BasisScriptMessage.GetQuaternion):
					case nameof(BasisScriptMessage.GetColor):
					case nameof(BasisScriptMessage.GetBytes):
					case nameof(BasisScriptMessage.GetInts):
					case nameof(BasisScriptMessage.GetFloats):
					case nameof(BasisScriptMessage.GetStrings):
					case nameof(BasisScriptMessage.GetVector3s):
					case nameof(BasisScriptMessage.GetSharedTransform):
					case nameof(BasisScriptMessage.GetScript):
					case nameof(BasisScriptMessage.GetTarget):
					case nameof(BasisScriptMessage.Reply):
						return false;
				}
				return true;
			}
			return false;
		}

		internal static BasisScriptTarget Self(object self)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null ? EntryOf(box, proxy)?.Handle : null;
		}

		internal static BasisScriptTarget Of(GameObject gameObject)
		{
			if (gameObject == null) return null;
			return BasisContent.TryResolve(gameObject, out BasisContentBase content) ? Target(content) : null;
		}

		internal static BasisScriptTarget Target(BasisContentBase content)
		{
			if (content == null) return null;
			if (entries.TryGetValue(content, out Entry found) && Live(found)) return found.Handle;
			return Add(content, content, null).Handle;
		}

		internal static BasisScriptTarget AvatarOf(ushort playerId)
		{
			BasisNetworkPlayer local = BasisNetworkPlayer.LocalPlayer;
			if (local != null && local.playerId == playerId && BasisLocalPlayer.Instance != null) return Target(BasisLocalPlayer.Instance.BasisAvatar);
			if (!BasisNetworkPlayers.GetPlayerById(playerId, out BasisNetworkPlayer player) || player?.Player == null) return null;
			return Target(player.Player.BasisAvatar);
		}

		internal static BasisScriptTarget[] All(string channel, string tag)
		{
			Purge();
			string wanted = tag != null ? TagName(tag) : null;
			if (tag != null && wanted == null) return System.Array.Empty<BasisScriptTarget>();
			List<BasisScriptTarget> found = new List<BasisScriptTarget>();
			for (int i = 0; i < order.Count; i++)
			{
				Entry entry = order[i];
				if (!Live(entry)) continue;
				if (wanted != null ? entry.Tags.Contains(wanted) : channel == null ? HasScripts(entry) : Listens(entry, channel)) found.Add(entry.Handle);
			}
			return found.ToArray();
		}

		internal static bool Listen(object self, string channel, int from, Action<BasisScriptMessage> handler)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			string name = Name(channel);
			if (box == null || name == null || handler == null || (from & BasisScriptBus.FromAnyone) == 0 || !Accepts(box, handler)) return false;
			Entry entry = EntryOf(box, proxy);
			if (entry == null) return false;
			Purge();
			int count = 0;
			for (int i = 0; i < listeners.Count; i++)
			{
				Listener listener = listeners[i];
				if (listener.Entry != entry) continue;
				if (ReferenceEquals(listener.Owner, proxy) && listener.Channel == name)
				{
					listener.Handler = handler;
					listener.From = from & BasisScriptBus.FromAnyone;
					return true;
				}
				count++;
			}
			if (count >= BasisScriptBus.MaxListeners) return false;
			listeners.Add(new Listener { Entry = entry, Owner = proxy, Box = box, Channel = name, From = from & BasisScriptBus.FromAnyone, Handler = handler });
			return true;
		}

		internal static void Unlisten(object self, string channel)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			string name = Name(channel);
			if (box == null || name == null) return;
			for (int i = listeners.Count - 1; i >= 0; i--)
			{
				if (ReferenceEquals(listeners[i].Owner, proxy) && listeners[i].Channel == name) Drop(i);
			}
		}

		internal static int Send(object self, BasisScriptTarget target, string channel, string text, object value, bool hasValue)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null ? Post(EntryOf(box, proxy), proxy, box, target, null, channel, text, value, hasValue, 0) : 0;
		}

		internal static int SendToScript(object self, BasisScriptReference script, string channel, string text, object value, bool hasValue)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null && Live(script) ? Post(EntryOf(box, proxy), proxy, box, null, script.Proxy, channel, text, value, hasValue, 0) : 0;
		}

		internal static int Reply(BasisScriptMessage message, string text, object value, bool hasValue)
		{
			Listener receiver = message?.Receiver;
			if (receiver == null || !Alive(receiver) || receiver.Box.interpreterAccountingDepth == 0 || message.Sender == null) return 0;
			return Post(receiver.Entry, receiver.Owner, receiver.Box, message.Sender, null, message.Channel, text, value, hasValue, message.Id);
		}

		internal static bool Publish(object self, string key, object item)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			Entry entry = box != null ? EntryOf(box, proxy) : null;
			string name = Name(key);
			if (entry == null || name == null || !Capture(item, entry, box, out Value value)) return false;
			bool known = entry.Values.TryGetValue(name, out Value old);
			if (!known && entry.Values.Count >= BasisScriptBus.MaxValues) return false;
			long total = entry.PublishedBytes - (known ? old.Size : 0) + value.Size;
			if (total > BasisScriptBus.MaxPublishedBytes) return false;
			entry.Values[name] = value;
			entry.PublishedBytes = total;
			return true;
		}

		internal static void Unpublish(object self, string key)
		{
			Entry entry = Owned(self);
			if (entry == null || key == null || !entry.Values.TryGetValue(key, out Value old)) return;
			entry.Values.Remove(key);
			entry.PublishedBytes -= old.Size;
		}

		internal static bool Tag(object self, string tag, bool add)
		{
			Entry entry = Owned(self);
			string name = TagName(tag);
			if (entry == null || name == null) return false;
			if (!add) return entry.Tags.Remove(name);
			if (entry.Tags.Contains(name)) return true;
			if (entry.Tags.Count >= BasisScriptBus.MaxTags) return false;
			entry.Tags.Add(name);
			entry.Tags.Sort(StringComparer.Ordinal);
			return true;
		}

		internal static bool Describe(object self, string name, string description)
		{
			Entry entry = Owned(self);
			if (entry == null) return false;
			string text = CilboxMenus.Clean(description, BasisScriptBus.MaxDescriptionLength, false);
			if (name == null)
			{
				entry.Description = text;
				return true;
			}
			string key = Name(name);
			if (key == null) return false;
			if (text.Length == 0)
			{
				entry.Descriptions.Remove(key);
				return true;
			}
			if (!entry.Descriptions.ContainsKey(key) && entry.Descriptions.Count >= MaxDescriptions) return false;
			entry.Descriptions[key] = text;
			return true;
		}

		internal static bool Live(Entry entry) => entry != null && !entry.Removed && entry.Root != null && (!entry.HasContent || entry.Content != null) && (entry.HasContent || (entry.Box != null && !entry.Box.disabled));

		internal static bool HasScripts(Entry entry)
		{
			if (entry.Box != null && !entry.Box.disabled) return true;
			for (int i = 0; i < listeners.Count; i++)
			{
				if (listeners[i].Entry == entry && Alive(listeners[i])) return true;
			}
			return false;
		}

		internal static bool Live(BasisScriptReference script) => script != null && !script.Removed && script.Proxy != null && !script.Proxy.disabled && script.Box != null && !script.Box.disabled && ReferenceEquals(script.Proxy.box, script.Box);

		internal static bool Listens(BasisScriptReference script, string channel)
		{
			string name = Name(channel);
			if (name == null) return false;
			for (int i = 0; i < listeners.Count; i++)
			{
				if (ReferenceEquals(listeners[i].Owner, script.Proxy) && listeners[i].Channel == name && Alive(listeners[i])) return true;
			}
			return false;
		}

		internal static BasisScriptReference Reference(CilboxProxy proxy, CilboxBasisCommon box)
		{
			if (proxy == null || box == null) return null;
			if (scripts.TryGetValue(proxy, out BasisScriptReference known))
			{
				if (Live(known)) return known;
				known.Removed = true;
			}
			BasisScriptReference script = new BasisScriptReference { Proxy = proxy, Box = box, Class = CilboxMenus.Clean(proxy.className ?? string.Empty, MaxClassNameLength, false) };
			scripts[proxy] = script;
			return script;
		}

		internal static bool Listens(Entry entry, string channel)
		{
			string name = Name(channel);
			if (name == null) return false;
			for (int i = 0; i < listeners.Count; i++)
			{
				if (listeners[i].Entry == entry && listeners[i].Channel == name && Alive(listeners[i])) return true;
			}
			return false;
		}

		internal static bool HasTag(Entry entry, string tag)
		{
			string name = TagName(tag);
			return name != null && entry.Tags.Contains(name);
		}

		internal static bool Local(Entry entry)
		{
			if (entry.Content is BasisAvatar avatar) return BasisLocalPlayer.Instance != null && ReferenceEquals(BasisLocalPlayer.Instance.BasisAvatar, avatar);
			return entry.Kind == BasisScriptBus.KindProp && entry.Content != null && entry.Content.SpawnedByLocalPlayer;
		}

		internal static int OwnerId(Entry entry)
		{
			BasisNetworkPlayer local = BasisNetworkPlayer.LocalPlayer;
			if (entry.Content is BasisAvatar avatar)
			{
				if (avatar.TryGetLinkedPlayer(out ushort id)) return id;
				if (local != null && BasisLocalPlayer.Instance != null && ReferenceEquals(BasisLocalPlayer.Instance.BasisAvatar, avatar)) return local.playerId;
				foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
				{
					if (player?.Player != null && ReferenceEquals(player.Player.BasisAvatar, avatar)) return player.playerId;
				}
				return -1;
			}
			if (entry.Kind != BasisScriptBus.KindProp || entry.Content == null) return -1;
			if (entry.Content.SpawnedByLocalPlayer) return local != null ? local.playerId : -1;
			IBasisPlayer creator = BasisPlayersShim.GetByUUID(entry.Content.CreatorUUID);
			return creator != null && creator.TryGetPlayerId(out ushort creatorId) ? creatorId : -1;
		}

		internal static string[] KeysOf(Entry entry)
		{
			string[] keys = new string[entry.Values.Count];
			entry.Values.Keys.CopyTo(keys, 0);
			System.Array.Sort(keys, StringComparer.Ordinal);
			return keys;
		}

		internal static string[] ChannelsOf(Entry entry)
		{
			List<string> found = new List<string>();
			for (int i = 0; i < listeners.Count; i++)
			{
				if (listeners[i].Entry == entry && Alive(listeners[i]) && !found.Contains(listeners[i].Channel)) found.Add(listeners[i].Channel);
			}
			found.Sort(StringComparer.Ordinal);
			return found.ToArray();
		}

		internal static bool Capture(object item, out Value value) => Capture(item, null, null, out value);

		internal static bool Capture(object item, Entry from, CilboxBasisCommon box, out Value value)
		{
			value = default;
			switch (item)
			{
				case null: return false;
				case string text:
					if (text.Length > BasisScriptBus.MaxTextLength) return false;
					value = new Value { Type = BasisScriptBus.TypeText, Item = text, Size = 16 + 2L * text.Length };
					return true;
				case bool flag:
					value = new Value { Type = BasisScriptBus.TypeBool, Number = flag ? 1d : 0d, Size = 8 };
					return true;
				case byte[] bytes:
					if (bytes.Length > BasisScriptBus.MaxDataLength) return false;
					value = new Value { Type = BasisScriptBus.TypeBytes, Item = bytes.Clone(), Size = 16 + bytes.Length };
					return true;
				case Vector2 v2:
					if (!Finite(v2.x) || !Finite(v2.y)) return false;
					value = new Value { Type = BasisScriptBus.TypeVector2, Item = v2, Size = 16 };
					return true;
				case Vector3 v3:
					if (!Finite(v3.x) || !Finite(v3.y) || !Finite(v3.z)) return false;
					value = new Value { Type = BasisScriptBus.TypeVector3, Item = v3, Size = 16 };
					return true;
				case Vector4 v4:
					if (!Finite(v4.x) || !Finite(v4.y) || !Finite(v4.z) || !Finite(v4.w)) return false;
					value = new Value { Type = BasisScriptBus.TypeVector4, Item = v4, Size = 16 };
					return true;
				case Quaternion q:
					if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
					value = new Value { Type = BasisScriptBus.TypeQuaternion, Item = q, Size = 16 };
					return true;
				case Color c:
					if (!Finite(c.r) || !Finite(c.g) || !Finite(c.b) || !Finite(c.a)) return false;
					value = new Value { Type = BasisScriptBus.TypeColor, Item = c, Size = 16 };
					return true;
				case int[] ints:
					if (ints.Length > BasisScriptBus.MaxArrayLength) return false;
					value = new Value { Type = BasisScriptBus.TypeInts, Item = ints.Clone(), Size = 16 + 4L * ints.Length };
					return true;
				case float[] floats:
					if (floats.Length > BasisScriptBus.MaxArrayLength) return false;
					for (int i = 0; i < floats.Length; i++)
					{
						if (!Finite(floats[i])) return false;
					}
					value = new Value { Type = BasisScriptBus.TypeFloats, Item = floats.Clone(), Size = 16 + 4L * floats.Length };
					return true;
				case string[] strings:
					if (strings.Length > BasisScriptBus.MaxArrayLength) return false;
					string[] copy = new string[strings.Length];
					long chars = 0;
					for (int i = 0; i < strings.Length; i++)
					{
						copy[i] = strings[i] ?? string.Empty;
						if (copy[i].Length > BasisScriptBus.MaxTextLength) return false;
						chars += copy[i].Length;
					}
					if (2L * chars > BasisScriptBus.MaxDataLength) return false;
					value = new Value { Type = BasisScriptBus.TypeStrings, Item = copy, Size = 16 + 8L * copy.Length + 2L * chars };
					return true;
				case Vector3[] points:
					if (points.Length > BasisScriptBus.MaxArrayLength) return false;
					for (int i = 0; i < points.Length; i++)
					{
						if (!Finite(points[i].x) || !Finite(points[i].y) || !Finite(points[i].z)) return false;
					}
					value = new Value { Type = BasisScriptBus.TypeVector3s, Item = points.Clone(), Size = 16 + 12L * points.Length };
					return true;
				case CilboxProxy script:
					if (box == null || script == null || script.disabled || !ReferenceEquals(script.box, box)) return false;
					value = new Value { Type = BasisScriptBus.TypeScript, Item = Reference(script, box), Size = 16 };
					return true;
				case BasisScriptReference reference:
					if (!Live(reference)) return false;
					value = new Value { Type = BasisScriptBus.TypeScript, Item = reference, Size = 16 };
					return true;
				case BasisScriptTarget target:
					if (!Live(target.Entry)) return false;
					value = new Value { Type = BasisScriptBus.TypeTarget, Item = target, Size = 16 };
					return true;
				case BasisSharedTransform shared:
					if (!Live(shared)) return false;
					value = new Value { Type = BasisScriptBus.TypeTransform, Item = shared, Size = 16 };
					return true;
				case UnityEngine.Object unityObject:
					Transform node = unityObject is GameObject gameObject ? gameObject.transform : unityObject is Component component ? component.transform : null;
					BasisSharedTransform readOnly = from != null ? ShareOf(node, from, false) : null;
					if (readOnly == null) return false;
					value = new Value { Type = BasisScriptBus.TypeTransform, Item = readOnly, Size = 16 };
					return true;
			}
			Type type = item.GetType();
			if (!type.IsPrimitive || item is char || item is IntPtr || item is UIntPtr) return false;
			double number = Convert.ToDouble(item, CultureInfo.InvariantCulture);
			if (double.IsNaN(number) || double.IsInfinity(number)) return false;
			value = new Value { Type = BasisScriptBus.TypeNumber, Number = number, Size = 8 };
			return true;
		}

		internal static Value Duplicate(Value value)
		{
			if (value.Item is System.Array array) value.Item = array.Clone();
			return value;
		}

		internal static string AsString(Value value, string fallback)
		{
			switch (value.Type)
			{
				case BasisScriptBus.TypeText: return (string)value.Item;
				case BasisScriptBus.TypeNumber: return value.Number.ToString("R", CultureInfo.InvariantCulture);
				case BasisScriptBus.TypeBool: return value.Number != 0d ? "true" : "false";
			}
			return fallback;
		}

		internal static float AsFloat(Value value, float fallback) => value.Type == BasisScriptBus.TypeNumber || value.Type == BasisScriptBus.TypeBool ? (float)value.Number : fallback;

		internal static int AsInt(Value value, int fallback) => (value.Type == BasisScriptBus.TypeNumber || value.Type == BasisScriptBus.TypeBool) && value.Number >= int.MinValue && value.Number <= int.MaxValue ? (int)Math.Round(value.Number) : fallback;

		internal static bool AsBool(Value value, bool fallback) => value.Type == BasisScriptBus.TypeNumber || value.Type == BasisScriptBus.TypeBool ? value.Number != 0d : fallback;

		internal static T As<T>(Value value, int type, T fallback) where T : struct => value.Type == type && value.Item is T item ? item : fallback;

		internal static T[] Array<T>(Value value, int type, bool copy) => value.Type == type && value.Item is T[] items ? copy ? (T[])items.Clone() : items : null;

		internal static BasisSharedTransform AsShared(Value value) => value.Type == BasisScriptBus.TypeTransform && value.Item is BasisSharedTransform shared && Live(shared) ? shared : null;

		internal static BasisScriptReference AsScript(Value value) => value.Type == BasisScriptBus.TypeScript && value.Item is BasisScriptReference script && Live(script) ? script : null;

		internal static BasisScriptTarget AsTarget(Value value) => value.Type == BasisScriptBus.TypeTarget && value.Item is BasisScriptTarget target && Live(target.Entry) ? target : null;

		internal static bool Live(BasisSharedTransform shared) => shared != null && !shared.Removed && shared.Node != null && Live(shared.From);

		internal static BasisSharedTransform Share(object self, Transform transform, bool writable)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null ? ShareOf(transform, EntryOf(box, proxy), writable) : null;
		}

		internal static void Unshare(object self, Transform transform)
		{
			Entry entry = Owned(self);
			if (entry == null || transform == null) return;
			Revoke(readShares, transform, entry);
			Revoke(writeShares, transform, entry);
		}

		internal static bool QueueActive(BasisSharedTransform shared, bool active)
		{
			for (int i = 0; i < actives.Count; i++)
			{
				if (!ReferenceEquals(actives[i].Share.Node, shared.Node)) continue;
				actives[i] = new PendingActive { Share = shared, Active = active };
				return true;
			}
			if (actives.Count >= MaxPendingActives) return false;
			actives.Add(new PendingActive { Share = shared, Active = active });
			Start();
			return true;
		}

		static BasisSharedTransform ShareOf(Transform transform, Entry from, bool writable)
		{
			if (transform == null || from == null || !Shareable(transform, from)) return null;
			Dictionary<Transform, BasisSharedTransform> shares = writable ? writeShares : readShares;
			if (shares.TryGetValue(transform, out BasisSharedTransform known))
			{
				if (Live(known) && known.From == from) return known;
				known.Removed = true;
			}
			BasisSharedTransform shared = new BasisSharedTransform { Node = transform, From = from, CanWrite = writable, Label = CilboxMenus.Clean(transform.name, BasisScriptBus.MaxNameLength, false) };
			shares[transform] = shared;
			return shared;
		}

		static void Revoke(Dictionary<Transform, BasisSharedTransform> shares, Transform transform, Entry entry)
		{
			if (!shares.TryGetValue(transform, out BasisSharedTransform known) || known.From != entry) return;
			known.Removed = true;
			shares.Remove(transform);
		}

		static void PurgeShares(Dictionary<Transform, BasisSharedTransform> shares)
		{
			foreach (KeyValuePair<Transform, BasisSharedTransform> pair in shares)
			{
				if (!Live(pair.Value)) deadShares.Add(pair.Key);
			}
			for (int i = 0; i < deadShares.Count; i++)
			{
				shares[deadShares[i]].Removed = true;
				shares.Remove(deadShares[i]);
			}
			deadShares.Clear();
		}

		static bool Shareable(Transform transform, Entry from)
		{
			if (!Live(from)) return false;
			GameObject gameObject = transform.gameObject;
			BasisContentBase nearest = gameObject.GetComponentInParent<BasisContentBase>(true);
			if (nearest != null) return from.HasContent && ReferenceEquals(nearest, from.Content);
			if (gameObject.GetComponentInParent<BasisPlayer>(true) != null) return false;
			if (!from.HasContent) return transform.IsChildOf(from.Root);
			return from.Content is BasisScene && BasisContent.TryResolve(gameObject, out BasisContentBase owner) && ReferenceEquals(owner, from.Content);
		}

		static void ApplyActives()
		{
			for (int i = 0; i < actives.Count; i++)
			{
				PendingActive pending = actives[i];
				if (!pending.Share.CanWrite || !Live(pending.Share)) continue;
				try
				{
					pending.Share.Node.gameObject.SetActive(pending.Active);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
			actives.Clear();
		}

		internal static void Tick()
		{
			if (actives.Count > 0) ApplyActives();
			List<Pending> batch = queue;
			queue = working;
			working = batch;
			int delivered = 0, i = 0;
			for (; i < batch.Count && delivered < MaxDeliveriesPerTick; i++)
			{
				Pending pending = batch[i];
				pending.To.Entry.Queued--;
				if (pending.To.Removed || !Alive(pending.To) || !Live(pending.To.Entry)) continue;
				delivered++;
				try
				{
					pending.To.Handler.Invoke(pending.Message);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
			if (i < batch.Count) queue.InsertRange(0, batch.GetRange(i, batch.Count - i));
			batch.Clear();
			Purge();
			if (queue.Count == 0 && actives.Count == 0) Stop();
		}

		internal static void Reset()
		{
			entries.Clear();
			order.Clear();
			listeners.Clear();
			scripts.Clear();
			deadScripts.Clear();
			readShares.Clear();
			writeShares.Clear();
			deadShares.Clear();
			actives.Clear();
			queue.Clear();
			working.Clear();
			nextId = 0;
			Stop();
		}

		static int Post(Entry from, CilboxProxy sender, CilboxBasisCommon box, BasisScriptTarget target, CilboxProxy only, string channel, string text, object item, bool hasValue, int replyTo)
		{
			string name = Name(channel);
			if (from == null || !Live(from) || name == null || (target != null && !Live(target.Entry))) return 0;
			if (text != null && text.Length > BasisScriptBus.MaxTextLength) return 0;
			Value value = default;
			if (hasValue && item != null && !Capture(item, from, box, out value)) return 0;
			int kindBit = 1 << from.Kind, found = 0;
			for (int i = 0; i < listeners.Count; i++)
			{
				if (Matches(listeners[i], target, only, sender, name, kindBit) && listeners[i].Entry.Queued < BasisScriptBus.MaxQueued) found++;
			}
			long bytes = found * (value.Size + 2L * (text != null ? text.Length : 0));
			if (found == 0 || !Spend(from, bytes)) return 0;
			int id = ++nextId;
			if (id <= 0) id = nextId = 1;
			bool first = true;
			BasisScriptReference script = Reference(sender, box);
			for (int i = 0; i < listeners.Count; i++)
			{
				Listener listener = listeners[i];
				if (!Matches(listener, target, only, sender, name, kindBit) || listener.Entry.Queued >= BasisScriptBus.MaxQueued) continue;
				BasisScriptMessage message = new BasisScriptMessage { Receiver = listener, Id = id, ReplyTo = replyTo, Channel = name, Text = text ?? string.Empty, Value = first ? value : Duplicate(value), Sender = from.Handle, SenderScript = script };
				first = false;
				listener.Entry.Queued++;
				queue.Add(new Pending { To = listener, Message = message });
			}
			Start();
			return id;
		}

		static bool Matches(Listener listener, BasisScriptTarget target, CilboxProxy only, CilboxProxy sender, string channel, int kindBit)
		{
			if (listener.Removed || listener.Channel != channel || (listener.From & kindBit) == 0 || !Alive(listener)) return false;
			if (only != null) return ReferenceEquals(listener.Owner, only);
			return target != null ? listener.Entry == target.Entry : !ReferenceEquals(listener.Owner, sender);
		}

		static Entry Owned(object self)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null ? EntryOf(box, proxy) : null;
		}

		internal static Entry EntryOf(CilboxBasisCommon box, CilboxProxy proxy)
		{
			if (CilboxBasisConsent.TryOwner(box, proxy.gameObject, out BasisContentBase content) && content != null)
			{
				if (entries.TryGetValue(content, out Entry known) && Live(known))
				{
					if (known.Box == null || known.Box.disabled) known.Box = box;
					return known;
				}
				return Add(content, content, box);
			}
			if (entries.TryGetValue(box, out Entry boxed) && Live(boxed)) return boxed;
			return Add(box, null, box);
		}

		static Entry Add(object key, BasisContentBase content, CilboxBasisCommon box)
		{
			Purge();
			if (entries.TryGetValue(key, out Entry old))
			{
				old.Removed = true;
				order.Remove(old);
			}
			Entry entry = new Entry { Key = key, Content = content, HasContent = content != null, Box = box, Root = content != null ? content.transform : box.transform };
			entry.Kind = content is BasisAvatar || box is CilboxAvatarBasis ? BasisScriptBus.KindAvatar : content is BasisProp || (content == null && box is CilboxPropBasis) ? BasisScriptBus.KindProp : BasisScriptBus.KindWorld;
			string name = content != null ? content.BasisBundleDescription?.AssetBundleName : null;
			if (string.IsNullOrEmpty(name)) name = entry.Root.gameObject.scene.name;
			if (string.IsNullOrEmpty(name)) name = entry.Root.name;
			entry.Name = CilboxMenus.Clean(name, BasisScriptBus.MaxNameLength, false);
			entry.Handle = new BasisScriptTarget { Entry = entry };
			entries[key] = entry;
			order.Add(entry);
			return entry;
		}

		static void Purge()
		{
			for (int i = listeners.Count - 1; i >= 0; i--)
			{
				if (i < listeners.Count && (listeners[i].Removed || !Alive(listeners[i]))) Drop(i);
			}
			if (scripts.Count > 0)
			{
				foreach (KeyValuePair<CilboxProxy, BasisScriptReference> pair in scripts)
				{
					if (!Live(pair.Value)) deadScripts.Add(pair.Key);
				}
				for (int i = 0; i < deadScripts.Count; i++)
				{
					scripts[deadScripts[i]].Removed = true;
					scripts.Remove(deadScripts[i]);
				}
				deadScripts.Clear();
			}
			if (readShares.Count > 0) PurgeShares(readShares);
			if (writeShares.Count > 0) PurgeShares(writeShares);
			for (int i = order.Count - 1; i >= 0; i--)
			{
				Entry entry = order[i];
				if (Live(entry)) continue;
				entry.Removed = true;
				entry.Values.Clear();
				entry.Descriptions.Clear();
				entry.Tags.Clear();
				order.RemoveAt(i);
				if (entries.TryGetValue(entry.Key, out Entry current) && current == entry) entries.Remove(entry.Key);
			}
		}

		static void Drop(int index)
		{
			listeners[index].Removed = true;
			listeners[index].Handler = null;
			listeners.RemoveAt(index);
		}

		static bool Alive(Listener listener) => listener.Owner != null && !listener.Owner.disabled && listener.Box != null && !listener.Box.disabled;

		static void Start()
		{
			if (ticking) return;
			ticking = true;
			BasisFrameClock.OnTick += Tick;
			BasisFrameClock.AddRequest();
		}

		static void Stop()
		{
			if (!ticking) return;
			ticking = false;
			BasisFrameClock.OnTick -= Tick;
			BasisFrameClock.RemoveRequest();
		}

		static bool Spend(Entry entry, long bytes)
		{
			double now = Time.realtimeSinceStartupAsDouble;
			if (now - entry.WindowStart >= 1d || now < entry.WindowStart)
			{
				entry.WindowStart = now;
				entry.WindowSends = 0;
				entry.WindowBytes = 0;
			}
			if (entry.WindowSends >= BasisScriptBus.MaxSendsPerSecond || entry.WindowBytes + bytes > BasisScriptBus.MaxBytesPerSecond) return false;
			entry.WindowSends++;
			entry.WindowBytes += bytes;
			return true;
		}

		static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

		internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

		internal static bool Unit(Quaternion value, out Quaternion unit)
		{
			unit = Quaternion.identity;
			if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
			float length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
			if (length < 1e-6f || !Finite(length)) return false;
			unit = new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
			return true;
		}

		static string Name(string value)
		{
			if (string.IsNullOrEmpty(value) || value.Length > BasisScriptBus.MaxNameLength) return null;
			for (int i = 0; i < value.Length; i++)
			{
				if (char.IsControl(value[i])) return null;
			}
			return value;
		}

		static string TagName(string value)
		{
			if (string.IsNullOrEmpty(value) || value.Length > BasisScriptBus.MaxTagLength) return null;
			char[] chars = value.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				char c = char.ToLowerInvariant(chars[i]);
				if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')) return null;
				chars[i] = c;
			}
			return new string(chars);
		}

		internal static CilboxBasisCommon Running(object self, out CilboxProxy proxy)
		{
			proxy = self as CilboxProxy;
			if (proxy == null || proxy.disabled || !(proxy.box is CilboxBasisCommon box) || box == null || box.disabled || box.interpreterAccountingDepth == 0) return null;
			return box;
		}

		static bool Accepts(CilboxBasisCommon box, Delegate callback)
		{
			foreach (Delegate part in callback.GetInvocationList())
			{
				if (!(part.Target is CilboxPlatform.DelegateRepackage repackage) || repackage.meth == null || repackage.meth.parentClass == null || !ReferenceEquals(repackage.meth.parentClass.box, box)) return false;
				if (!ReferenceEquals(repackage.o, null) && !ReferenceEquals(repackage.o.box, box)) return false;
			}
			return true;
		}
	}
}
