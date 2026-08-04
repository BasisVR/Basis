#if UNITY_EDITOR
using System.Collections.Generic;

namespace Basis.Shims.Editor
{
    /// <summary>A member the sandbox has to allow for a catalog entry to be usable.</summary>
    internal readonly struct CilboxApiRequirement
    {
        public readonly string TypeName;

        /// <summary>The method the example calls, or null when holding the type is enough.</summary>
        public readonly string MemberName;

        public CilboxApiRequirement(string typeName, string memberName = null)
        {
            TypeName = typeName;
            MemberName = memberName;
        }
    }

    /// <summary>One documented capability: what it is, what it needs, and how it is written.</summary>
    internal sealed class CilboxApiEntry
    {
        public string GroupKey;
        public string TitleKey;
        public string SummaryKey;

        /// <summary>C# source. Never localized — translating an identifier would break the example.</summary>
        public string Example;

        public CilboxApiRequirement[] Requires = new CilboxApiRequirement[0];

        /// <summary>Availability, answered by the sandboxes themselves rather than declared here.</summary>
        public bool AvailableIn(CilboxBoxInfo box)
        {
            if (box == null) return false;
            foreach (CilboxApiRequirement requirement in Requires)
            {
                if (!BasisCilboxPermissionModel.IsMemberAllowed(box, requirement.TypeName, requirement.MemberName))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>A rule about cilboxed scripts that costs an afternoon to rediscover.</summary>
    internal sealed class CilboxApiNote
    {
        public string TitleKey;
        public string BodyKey;
        public string Example;
    }

    /// <summary>
    /// The hand-written half of the window: what a cilboxed script can actually do, in the order
    /// someone writing one needs it.
    ///
    /// <para>Only the prose is written here. Whether an entry is available in a given box is asked
    /// of that box at draw time through <see cref="CilboxApiEntry.AvailableIn"/>, so an entry cannot
    /// keep claiming a capability after the whitelist behind it is edited away.</para>
    /// </summary>
    internal static class BasisCilboxApiCatalog
    {
        public const string GroupLifecycle = "sdk.cilbox.group.lifecycle";
        public const string GroupPlayers = "sdk.cilbox.group.players";
        public const string GroupAvatar = "sdk.cilbox.group.avatar";
        public const string GroupNetworking = "sdk.cilbox.group.networking";
        public const string GroupObjects = "sdk.cilbox.group.objects";
        public const string GroupPhysics = "sdk.cilbox.group.physics";
        public const string GroupRendering = "sdk.cilbox.group.rendering";
        public const string GroupAudio = "sdk.cilbox.group.audio";
        public const string GroupInteraction = "sdk.cilbox.group.interaction";
        public const string GroupData = "sdk.cilbox.group.data";
        public const string GroupMedia = "sdk.cilbox.group.media";

        public static readonly string[] GroupOrder =
        {
            GroupLifecycle,
            GroupPlayers,
            GroupAvatar,
            GroupNetworking,
            GroupObjects,
            GroupPhysics,
            GroupRendering,
            GroupAudio,
            GroupInteraction,
            GroupData,
            GroupMedia,
        };

        public static readonly List<CilboxApiEntry> Entries = new List<CilboxApiEntry>
        {
            // ---------------------------------------------------------- lifecycle
            new CilboxApiEntry
            {
                GroupKey = GroupLifecycle,
                TitleKey = "sdk.cilbox.api.cilboxable.title",
                SummaryKey = "sdk.cilbox.api.cilboxable.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.MonoBehaviour") },
                Example =
@"using UnityEngine;
using Cilbox;

[Cilboxable]
public class MySpinner : MonoBehaviour
{
    public float degreesPerSecond = 90f;

    void Update()
    {
        transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f);
    }
}",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupLifecycle,
                TitleKey = "sdk.cilbox.api.lifecycle.title",
                SummaryKey = "sdk.cilbox.api.lifecycle.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.MonoBehaviour") },
                Example =
@"// Forwarded to a cilboxed script by CilboxProxy:
void Awake() { }
void Start() { }            // do one-time setup here, not in OnEnable
void Update() { }
void FixedUpdate() { }
void OnEnable() { }         // the FIRST OnEnable never arrives
void OnDisable() { }
void OnDestroy() { }        // do teardown here
void OnTriggerEnter(Collider other) { }
void OnTriggerStay(Collider other) { }
void OnTriggerExit(Collider other) { }
void OnCollisionEnter(Collision c) { }
void OnCollisionStay(Collision c) { }
void OnCollisionExit(Collision c) { }

// NOT forwarded: LateUpdate, OnGUI, OnAnimatorIK, OnParticleCollision, coroutines.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupLifecycle,
                TitleKey = "sdk.cilbox.api.time.title",
                SummaryKey = "sdk.cilbox.api.time.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Time") },
                Example =
@"float dt = Time.deltaTime;
float fixedDt = Time.fixedDeltaTime;
float now = Time.time;
int frame = Time.frameCount;",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupLifecycle,
                TitleKey = "sdk.cilbox.api.debug.title",
                SummaryKey = "sdk.cilbox.api.debug.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Debug") },
                Example =
@"// UnityEngine.Debug is swapped for Basis.Shims.BasisDebugPropsShim, which tags
// the message with the content it came from. Write it as plain Debug.
Debug.Log(""door opened"");
Debug.LogWarning($""no anchor on {name}"");",
            },

            // ---------------------------------------------------------- players
            new CilboxApiEntry
            {
                GroupKey = GroupPlayers,
                TitleKey = "sdk.cilbox.api.playersRoster.title",
                SummaryKey = "sdk.cilbox.api.playersRoster.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisPlayersShim", "GetById") },
                Example =
@"using Basis.Shims;
using Basis.Scripts.BasisSdk.Players;

int count = BasisPlayersShim.Count;

IBasisPlayer nearest = BasisPlayersShim.Nearest(transform.position);
if (nearest != null)
{
    Debug.Log($""{nearest.DisplayName} at {nearest.DistanceTo(transform.position):F1}m"");
}

foreach (IBasisPlayer player in BasisPlayersShim.Within(transform.position, 5f))
{
    Vector3 head = player.GetBonePosition(HumanBodyBones.Head);
}",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupPlayers,
                TitleKey = "sdk.cilbox.api.playerReads.title",
                SummaryKey = "sdk.cilbox.api.playerReads.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Scripts.BasisSdk.Players.IBasisPlayer", "get_DisplayName") },
                Example =
@"// Allowed on IBasisPlayer: reads only.
bool isLocal = player.IsLocal;
string name = player.DisplayName;
string safe = player.SafeDisplayName;
bool fallback = player.IsConsideredFallBackAvatar;

// Blocked on purpose: get_AvatarTransform, get_GameObject, get_UUID and every
// setter. A Transform is an unrestricted write handle once handed over, and UUID
// is stable across visits, so it would let a world correlate people between them.
// Poses come from BasisPlayersShim as copied vectors instead.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupPlayers,
                TitleKey = "sdk.cilbox.api.localPlayer.title",
                SummaryKey = "sdk.cilbox.api.localPlayer.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Scripts.BasisSdk.Players.BasisLocalPlayer", "Teleport") },
                Example =
@"using Basis.Scripts.BasisSdk.Players;

BasisLocalPlayer local = BasisLocalPlayer.Instance;
local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
local.Teleport(spawnPoint.position, spawnPoint.rotation, BasisTeleportMode.Instant);

local.SetDefaultMovementSpeed(3.5f);
local.SetJumpHeight(1.2f);
local.Immobilize(true);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupPlayers,
                TitleKey = "sdk.cilbox.api.playspaceInput.title",
                SummaryKey = "sdk.cilbox.api.playspaceInput.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisPlayspaceInputShim", "SetLocomotion") },
                Example =
@"using Basis.Shims;

// Avatar box only. Drives the wearer's own locomotion; blend decides how it mixes
// with their real input.
BasisPlayspaceInputShim input = avatar.PlayspaceInput;
input.SetLocomotion(BasisPlayerInputBlend.Additive,
    movement: new Vector2(0f, 1f), turn: 0f, jump: false, crouchDelta: 0f);

input.SetVerticalDelta(BasisPlayerInputBlend.Additive, 0.25f);
input.Clear();",
            },

            // ---------------------------------------------------------- avatar
            new CilboxApiEntry
            {
                GroupKey = GroupAvatar,
                TitleKey = "sdk.cilbox.api.avatarShim.title",
                SummaryKey = "sdk.cilbox.api.avatarShim.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisAvatarShim") },
                Example =
@"using Basis.Scripts.BasisSdk;

// In the avatar box the name BasisAvatar resolves to BasisAvatarShim, so write
// BasisAvatar and let the box do the swap.
BasisAvatar avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();

if (avatar != null && avatar.IsOwnedLocally)
{
    Animator animator = avatar.Animator;
    SkinnedMeshRenderer face = avatar.FaceVisemeMesh;
    float scale = avatar.HumanScale;
}

if (avatar.TryGetLinkedPlayer(out ushort playerId)) { }",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupAvatar,
                TitleKey = "sdk.cilbox.api.vixxy.title",
                SummaryKey = "sdk.cilbox.api.vixxy.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisVixxyShim", "HasControl") },
                Example =
@"using Basis.Shims;
using HVR.Vixxy;

// item is a serialized field on the script.
if (BasisVixxyShim.HasControl(item))
{
    float min = BasisVixxyShim.MinValue(item);
    float max = BasisVixxyShim.MaxValue(item);
    string title = BasisVixxyShim.Title(item);

    item.ApplyValue(Mathf.Lerp(min, max, 0.5f));
    float current = item.GetValue();
}

// GetValue() is meaningful only on the wearer's client. On a remote avatar the
// control never resolves and it returns 0. Gate on avatar.IsOwnedLocally.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupAvatar,
                TitleKey = "sdk.cilbox.api.blendShapeSync.title",
                SummaryKey = "sdk.cilbox.api.blendShapeSync.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisBlendShapeSyncShim") },
                Example =
@"using Basis.Shims;

// Bulk blendshape read/write, cheaper than one interpreted call per shape.
var sync = GetComponent<BasisBlendShapeSyncShim>();
sync.Epsilon = 0.01f;
sync.Enabled = true;",
            },

            // ---------------------------------------------------------- networking
            new CilboxApiEntry
            {
                GroupKey = GroupNetworking,
                TitleKey = "sdk.cilbox.api.makeNetworkable.title",
                SummaryKey = "sdk.cilbox.api.makeNetworkable.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.SafeUtil") },
                Example =
@"using Basis;
using Basis.Network.Core;
using UnityEngine;
using Cilbox;

[Cilboxable]
public class Door : MonoBehaviour
{
    BasisNetworkShim net;

    void Start()
    {
        // A cilboxed script CANNOT derive from BasisNetworkBehaviour — the proxy
        // that replaces it is not one, so the overrides would never fire. Get the
        // shim instead; cilbox adds the component on GetComponent.
        net = gameObject.GetComponent<BasisNetworkShim>();

        // SafeUtil.MakeNetworkable(this) does the same thing and returns the shim.

        net.NetworkReady += OnReady;
        net.NetworkMessageReceived += OnMessage;
    }

    void OnDestroy()
    {
        if (net == null) return;
        net.NetworkReady -= OnReady;
        net.NetworkMessageReceived -= OnMessage;
    }

    void OnReady() { }

    void OnMessage(ushort sender, byte[] buffer, DeliveryMethod method)
    {
        if (buffer != null && buffer.Length > 0) open = buffer[0] != 0;
    }

    void Broadcast(bool value)
    {
        net.SendCustomNetworkEvent(new byte[] { value ? (byte)1 : (byte)0 },
            DeliveryMethod.ReliableSequenced);
    }
}",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupNetworking,
                TitleKey = "sdk.cilbox.api.ownership.title",
                SummaryKey = "sdk.cilbox.api.ownership.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.BasisNetworkBehaviour") },
                Example =
@"if (net.HasNetworkID && net.IsOwnedLocallyOnServer)
{
    // Only the owner should be writing authoritative state.
}

net.RequestOwnershipIfNone();
net.OwnershipTransfer += newOwner => Debug.Log($""owner {newOwner.playerId}"");",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupNetworking,
                TitleKey = "sdk.cilbox.api.delayedEvents.title",
                SummaryKey = "sdk.cilbox.api.delayedEvents.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.BasisNetworkBehaviour") },
                Example =
@"// Cilboxed scripts have no coroutines. Delayed callbacks replace them.
net.SendCustomEventDelayedSeconds(Close, 3f);
net.SendCustomEventDelayedFrames(Rebuild, 2);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupNetworking,
                TitleKey = "sdk.cilbox.api.transformSync.title",
                SummaryKey = "sdk.cilbox.api.transformSync.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisTransformSyncShim") },
                Example =
@"using Basis.Shims;

var sync = GetComponent<BasisTransformSyncShim>();
sync.Channels = BasisTransformSyncShim.ChannelPose;
sync.Space = BasisTransformSyncShim.SpaceWorld;
sync.Enabled = true;",
            },

            // ---------------------------------------------------------- objects
            new CilboxApiEntry
            {
                GroupKey = GroupObjects,
                TitleKey = "sdk.cilbox.api.transform.title",
                SummaryKey = "sdk.cilbox.api.transform.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Transform") },
                Example =
@"transform.position = target.position;
transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
transform.localScale = Vector3.one * 0.5f;
transform.SetParent(anchor, worldPositionStays: true);

Transform child = transform.Find(""Handle"");
int count = transform.childCount;",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupObjects,
                TitleKey = "sdk.cilbox.api.getComponent.title",
                SummaryKey = "sdk.cilbox.api.getComponent.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Component") },
                Example =
@"// this.GetComponent* is unrestricted — the declaring type is Component.
var body = GetComponent<Rigidbody>();
var lamps = GetComponentsInChildren<Light>();

// gameObject.GetComponent* is a different declaring type and IS gated per box.
// The avatar and scene boxes allow the walk methods; the prop box allows only
// GetComponent. When in doubt use the this.* spelling, which always works.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupObjects,
                TitleKey = "sdk.cilbox.api.gameObject.title",
                SummaryKey = "sdk.cilbox.api.gameObject.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.GameObject", "SetActive") },
                Example =
@"gameObject.SetActive(false);
bool on = gameObject.activeSelf;
int layer = gameObject.layer;

// Always blocked: AddComponent, SendMessage, SendMessageUpwards, BroadcastMessage.
// Those reach behaviours by name and would step around the sandbox entirely.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupObjects,
                TitleKey = "sdk.cilbox.api.instantiate.title",
                SummaryKey = "sdk.cilbox.api.instantiate.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Object") },
                Example =
@"// Every Instantiate overload is redirected to a sanitizing shim: the copy is
// built under a disabled parent, components the box disallows are destroyed and
// persistent UnityEvent listeners are stripped before it goes live.
GameObject copy = Instantiate(prefab, transform.position, Quaternion.identity);

// Prefer pooling — SetActive(false) and reuse — over Instantiate per shot.",
            },

            // ---------------------------------------------------------- physics
            new CilboxApiEntry
            {
                GroupKey = GroupPhysics,
                TitleKey = "sdk.cilbox.api.rigidbody.title",
                SummaryKey = "sdk.cilbox.api.rigidbody.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Rigidbody") },
                Example =
@"var body = GetComponent<Rigidbody>();
body.AddForce(Vector3.up * 5f, ForceMode.Impulse);
body.linearVelocity = Vector3.zero;
body.isKinematic = true;",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupPhysics,
                TitleKey = "sdk.cilbox.api.raycast.title",
                SummaryKey = "sdk.cilbox.api.raycast.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Physics", "Raycast") },
                Example =
@"if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, 20f))
{
    Debug.Log($""hit {hit.collider.name} at {hit.distance:F2}m"");
}

Collider[] near = Physics.OverlapSphere(transform.position, 3f);

// Read-only statics such as Physics.gravity are allowed; the simulation-control
// entry points are not.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupPhysics,
                TitleKey = "sdk.cilbox.api.triggers.title",
                SummaryKey = "sdk.cilbox.api.triggers.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Collider") },
                Example =
@"void OnTriggerEnter(Collider other)
{
    if (other.CompareTag(""Player"")) open = true;
}

void OnCollisionEnter(Collision collision)
{
    Vector3 point = collision.contacts[0].point;
}",
            },

            // ---------------------------------------------------------- rendering
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.material.title",
                SummaryKey = "sdk.cilbox.api.material.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Material") },
                Example =
@"var renderer = GetComponent<MeshRenderer>();
renderer.material.color = Color.red;
renderer.material.SetFloat(""_Metallic"", 0.3f);

// A MaterialPropertyBlock avoids instancing a material per object.
var block = new MaterialPropertyBlock();
renderer.GetPropertyBlock(block);
block.SetColor(""_BaseColor"", Color.cyan);
renderer.SetPropertyBlock(block);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.blit.title",
                SummaryKey = "sdk.cilbox.api.blit.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Graphics", "Blit") },
                Example =
@"// Graphics.Blit is the only Graphics entry point available, and there is no GL
// immediate mode at all. Anything drawn per frame goes through a blit + shader.
Graphics.Blit(source, destination, blitMaterial);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.gpuReadback.title",
                SummaryKey = "sdk.cilbox.api.gpuReadback.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Rendering.AsyncGPUReadback", "Request") },
                Example =
@"using UnityEngine.Rendering;
using Unity.Collections;

AsyncGPUReadback.Request(renderTexture, 0, request =>
{
    if (request.hasError) return;
    NativeArray<Color32> pixels = request.GetData<Color32>();
});",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.light.title",
                SummaryKey = "sdk.cilbox.api.light.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Light", "set_intensity") },
                Example =
@"var lamp = GetComponent<Light>();
lamp.intensity = 2f;
lamp.color = Color.white;
lamp.range = 12f;
lamp.shadows = LightShadows.Soft;

// Command-buffer attachment is blocked: it hides GPU work outside the sandbox.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.camera.title",
                SummaryKey = "sdk.cilbox.api.camera.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Camera", "WorldToScreenPoint") },
                Example =
@"Camera main = Camera.main;
Vector3 screen = main.WorldToScreenPoint(transform.position);
Ray ray = main.ScreenPointToRay(screen);
float fov = main.fieldOfView;

// Queries and property accessors only. Render(), CopyFrom(), SetTargetBuffers()
// and the command-buffer methods are blocked.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupRendering,
                TitleKey = "sdk.cilbox.api.particles.title",
                SummaryKey = "sdk.cilbox.api.particles.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.ParticleSystem") },
                Example =
@"var particles = GetComponent<ParticleSystem>();
particles.Play();
particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);

ParticleSystem.MainModule main = particles.main;
main.startColor = Color.green;",
            },

            // ---------------------------------------------------------- audio
            new CilboxApiEntry
            {
                GroupKey = GroupAudio,
                TitleKey = "sdk.cilbox.api.audioSource.title",
                SummaryKey = "sdk.cilbox.api.audioSource.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.AudioSource") },
                Example =
@"var source = GetComponent<AudioSource>();
source.clip = clip;
source.volume = 0.8f;
source.spatialBlend = 1f;
source.Play();
source.PlayOneShot(otherClip);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupAudio,
                TitleKey = "sdk.cilbox.api.voiceRouting.title",
                SummaryKey = "sdk.cilbox.api.voiceRouting.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisVoiceRoutingShim", "RouteVoiceToObject") },
                Example =
@"using Basis.Shims;

// Consent is checked before a route is opened, and the check is not optional.
if (BasisVoiceRoutingShim.HasConsent(player))
{
    int route = BasisVoiceRoutingShim.RouteVoiceToObject(player, speakerAnchor, 1f, 25f);
    // later
    BasisVoiceRoutingShim.StopVoiceRoute(route);
}",
            },

            // ---------------------------------------------------------- interaction
            new CilboxApiEntry
            {
                GroupKey = GroupInteraction,
                TitleKey = "sdk.cilbox.api.interactable.title",
                SummaryKey = "sdk.cilbox.api.interactable.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject") },
                Example =
@"using Basis.Scripts.BasisSdk.Interactions;

// Only the event fields are reachable; the component's methods are all blocked.
void Start()
{
    interactable.OnInteractStartEvent += WhenGrabbed;
    interactable.OnInteractEndEvent += WhenReleased;
}

void OnDestroy()
{
    interactable.OnInteractStartEvent -= WhenGrabbed;
    interactable.OnInteractEndEvent -= WhenReleased;
}",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupInteraction,
                TitleKey = "sdk.cilbox.api.seat.title",
                SummaryKey = "sdk.cilbox.api.seat.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Scripts.BasisSdk.Interactions.BasisSeat", "TrySeatLocalPlayer") },
                Example =
@"using Basis.Scripts.BasisSdk.Interactions;

if (seat.IsAvailable) seat.TrySeatLocalPlayer();
if (seat.IsLocalPlayerSeated) seat.SetOccupantYaw(90f);
seat.EjectLocalPlayer();",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupInteraction,
                TitleKey = "sdk.cilbox.api.jiggle.title",
                SummaryKey = "sdk.cilbox.api.jiggle.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.BasisJiggleEventShim", "FindJiggleRig") },
                Example =
@"using Basis.Shims;

// Adding the component is the opt-in. The callbacks are found by name on your
// own script, so there is nothing to subscribe to.
void Start()
{
    var jiggle = GetComponent<BasisJiggleEventShim>();
    int rig = jiggle.FindJiggleRig(""Tail"");
}

void OnJiggleGrabStart(int rigIndex) { }
void OnJiggleGrabEnd(int rigIndex) { }",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupInteraction,
                TitleKey = "sdk.cilbox.api.ui.title",
                SummaryKey = "sdk.cilbox.api.ui.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.UI.Button") },
                Example =
@"using UnityEngine.UI;
using TMPro;

void Start()
{
    button.onClick.AddListener(OnClick);
    label.text = ""Ready"";
}

void OnDestroy() { button.onClick.RemoveAllListeners(); }

void OnClick() { }",
            },

            // ---------------------------------------------------------- data
            new CilboxApiEntry
            {
                GroupKey = GroupData,
                TitleKey = "sdk.cilbox.api.collections.title",
                SummaryKey = "sdk.cilbox.api.collections.summary",
                Requires = new[] { new CilboxApiRequirement("System.Collections.Generic.List`1") },
                Example =
@"using System.Collections.Generic;

var open = new List<int>();
var byName = new Dictionary<string, Transform>();
var seen = new HashSet<ushort>();

open.Add(3);
byName[""Door""] = doorTransform;",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupData,
                TitleKey = "sdk.cilbox.api.math.title",
                SummaryKey = "sdk.cilbox.api.math.summary",
                Requires = new[] { new CilboxApiRequirement("UnityEngine.Mathf") },
                Example =
@"float t = Mathf.Clamp01(elapsed / duration);
float eased = Mathf.SmoothStep(0f, 1f, t);
Vector3 point = Vector3.Lerp(a, b, eased);
Quaternion turn = Quaternion.Slerp(from, to, eased);
float angle = Vector3.Angle(transform.forward, toTarget);",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupData,
                TitleKey = "sdk.cilbox.api.binary.title",
                SummaryKey = "sdk.cilbox.api.binary.summary",
                Requires = new[] { new CilboxApiRequirement("System.IO.BinaryWriter") },
                Example =
@"using System.IO;

byte[] Pack(int id, float value)
{
    using (var stream = new MemoryStream())
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write(id);
        writer.Write(value);
        return stream.ToArray();
    }
}

// BitConverter and Convert are also available, pinned to their conversion methods.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupData,
                TitleKey = "sdk.cilbox.api.download.title",
                SummaryKey = "sdk.cilbox.api.download.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.BasisImageDownloader") },
                Example =
@"using Basis;

// The URL is checked before any request goes out: scheme and literal-address
// gate, then a DNS check so a name pointing at a private address is caught, and
// redirects are refused so a later 302 cannot reach a host that never passed.
var downloader = new BasisImageDownloader();
downloader.DownloadImage(""https://example.com/sign.png"", result =>
{
    if (result.Success) material.mainTexture = result.Result;
    else Debug.LogWarning(result.Error);
});",
            },

            // ---------------------------------------------------------- media
            new CilboxApiEntry
            {
                GroupKey = GroupMedia,
                TitleKey = "sdk.cilbox.api.mediaPlayer.title",
                SummaryKey = "sdk.cilbox.api.mediaPlayer.summary",
                Requires = new[] { new CilboxApiRequirement("BasisMediaPlayer", "get_OutputTexture") },
                Example =
@"// Read-only. There is no event, so poll it.
void Update()
{
    Texture frame = mediaPlayer.OutputTexture;
    if (frame != null && frame != lastFrame)
    {
        lastFrame = frame;
        Graphics.Blit(frame, target, cropMaterial);
    }
}

// LoadUrl, Play, Stop, Seek and CaptureScreenshot are blocked, so a scene cannot
// step around the video URL trust prompt.",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupMedia,
                TitleKey = "sdk.cilbox.api.videoPlayer.title",
                SummaryKey = "sdk.cilbox.api.videoPlayer.summary",
                Requires = new[] { new CilboxApiRequirement("Basis.Shims.VideoPlayerShim") },
                Example =
@"using UnityEngine.Video;

// UnityEngine.Video.VideoPlayer is swapped for VideoPlayerShim, which adds the
// URL trust prompt. Write it as VideoPlayer.
var player = GetComponent<VideoPlayer>();
player.url = ""https://example.com/clip.mp4"";
player.Play();",
            },
            new CilboxApiEntry
            {
                GroupKey = GroupMedia,
                TitleKey = "sdk.cilbox.api.osc.title",
                SummaryKey = "sdk.cilbox.api.osc.summary",
                Requires = new[] { new CilboxApiRequirement("HVR.Basis.Comms.OSC.OscMessage") },
                Example =
@"using Basis.Shims;
using HVR.Basis.Comms.OSC;

// Needs a BasisOsc component on the same object.
const string Address = ""/avatar/parameters/test"";
BasisOsc osc;

void Start()
{
    osc = GetComponent<BasisOsc>();
    if (osc == null) return;

    // A bare name is resolved to the avatar parameter path; the out parameter
    // reports what it became.
    osc.Subscribe(Address, OnOsc, out string resolved);
}

void OnDestroy()
{
    if (osc != null) osc.Unsubscribe(Address, OnOsc);
}

void OnOsc(OscMessage message, OscData[] arguments)
{
    if (arguments.Length > 0 && arguments[0].Kind == OscDataKind.Float32)
    {
        float value = arguments[0].FloatValue;
    }
}",
            },
        };

        /// <summary>The rules that are not discoverable from a whitelist, and cost the most to learn.</summary>
        public static readonly List<CilboxApiNote> Notes = new List<CilboxApiNote>
        {
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.noLateUpdate.title",
                BodyKey = "sdk.cilbox.note.noLateUpdate.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.firstOnEnable.title",
                BodyKey = "sdk.cilbox.note.firstOnEnable.body",
                Example =
@"// Wrong — the first OnEnable fires before Start marks the proxy set up, so it
// is dropped and this subscription never happens on the first activation.
void OnEnable() { manager.Register(this); }

// Right.
void Start() { manager.Register(this); }
void OnDestroy() { manager.Unregister(this); }",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.noNetworkBehaviour.title",
                BodyKey = "sdk.cilbox.note.noNetworkBehaviour.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.delegateUnsubscribe.title",
                BodyKey = "sdk.cilbox.note.delegateUnsubscribe.body",
                Example =
@"// Every method-as-delegate makes a fresh host wrapper, so -= cannot find the
// one that was added. For a publisher that outlives the script, poll instead.
void Update()
{
    if (source.Value != lastValue)
    {
        lastValue = source.Value;
        OnValueChanged(lastValue);
    }
}",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.defaultAllow.title",
                BodyKey = "sdk.cilbox.note.defaultAllow.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.paramTypes.title",
                BodyKey = "sdk.cilbox.note.paramTypes.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.getComponentSpelling.title",
                BodyKey = "sdk.cilbox.note.getComponentSpelling.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.contentPolice.title",
                BodyKey = "sdk.cilbox.note.contentPolice.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.constFields.title",
                BodyKey = "sdk.cilbox.note.constFields.body",
            },
            new CilboxApiNote
            {
                TitleKey = "sdk.cilbox.note.remoteTick.title",
                BodyKey = "sdk.cilbox.note.remoteTick.body",
            },
        };
    }
}
#endif
