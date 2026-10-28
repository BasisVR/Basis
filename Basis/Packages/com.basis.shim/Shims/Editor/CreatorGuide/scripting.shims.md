Shims are the Basis side of the Cilbox sandbox: small, safe APIs that give a sandboxed script access to players, input, networking, saving, menus and more. Most live in the `Basis.Shims` namespace.

Shims come in three shapes:

- Static classes you call directly, such as `BasisPlayersShim.Count`.
- Event components you opt into by calling `GetComponent<T>()` in `Start`. Cilbox adds the component for you, and Basis calls a method with a known name on your script, such as `OnPlatformChanged`.
- Swapped types. You write the normal Unity name (`Debug`, `VideoPlayer`, `BasisAvatar`) and the sandbox serves a safe version.

| Shim | What it does | Page |
| --- | --- | --- |
| `BasisPlayersShim` | Roster, nearest player, poses | [Players](/en/docs/scripting/shims/players) |
| `BasisVelocityShim` | Launch or push the local player | [Players](/en/docs/scripting/shims/players) |
| `BasisPlayspaceInputShim` | Drive the wearer's movement from an avatar | [Players](/en/docs/scripting/shims/players) |
| `BasisSeatShim` | Is the local player seated, and where | [Players](/en/docs/scripting/shims/players) |
| `BasisAvatarSwitchShim` | Know when a player changes avatar | [Players](/en/docs/scripting/shims/players) |
| `BasisPermissionsShim` | Admin, moderator and permission checks | [Players](/en/docs/scripting/shims/players) |
| `BasisControllerInputShim` | VR controller buttons, triggers, sticks | [Input](/en/docs/scripting/shims/input) |
| `BasisDesktopInputShim` | Mouse buttons, movement and scroll | [Input](/en/docs/scripting/shims/input) |
| `BasisHapticsShim` | Controller vibration | [Input](/en/docs/scripting/shims/input) |
| `BasisInteractableShim` | Pressable objects | [Input](/en/docs/scripting/shims/input) |
| `BasisActionTrigger` | UI pointer events | [Input](/en/docs/scripting/shims/input) |
| `BasisUIExtensions` | `Toggle.AddListener` | [Input](/en/docs/scripting/shims/input) |
| `BasisPlatformEventShim` | VR / desktop switches | [Events](/en/docs/scripting/shims/events) |
| `BasisLanguageEventShim` | Language changes | [Events](/en/docs/scripting/shims/events) |
| `BasisGraphicsSettingsEventShim` | Quality changes | [Events](/en/docs/scripting/shims/events) |
| `BasisPermissionEventShim` | Local permission changes | [Events](/en/docs/scripting/shims/events) |
| `BasisJiggleEventShim` | Someone grabs or touches jiggle bones | [Events](/en/docs/scripting/shims/events) |
| `BasisBeforeRenderShim` | Late update after IK, before drawing | [Events](/en/docs/scripting/shims/events) |
| `BasisNetworkShim` | Network events, ownership | [Networking](/en/docs/scripting/shims/networking) |
| `BasisNetworkTimeShim` | Shared server clock | [Networking](/en/docs/scripting/shims/networking) |
| `BasisSharedRandomShim` | Same random numbers on every client | [Networking](/en/docs/scripting/shims/networking) |
| `BasisBinaryShim` | Fast packing of numbers and vectors | [Networking](/en/docs/scripting/shims/networking) |
| `BasisCompressionShim` | LZ4 compression | [Networking](/en/docs/scripting/shims/networking) |
| `BasisConnectionShim` | Send the player to another server | [Networking](/en/docs/scripting/shims/networking) |
| `BasisSaveShim` | Save data on the player's machine | [Data](/en/docs/scripting/shims/data) |
| `BasisServerStorageShim` | Save data on the server | [Data](/en/docs/scripting/shims/data) |
| `BasisJson` | Parse JSON | [Data](/en/docs/scripting/shims/data) |
| `BasisWebRequestShim` | HTTPS GET and POST | [Data](/en/docs/scripting/shims/data) |
| `BasisExternalLinkShim` | Open a website | [Data](/en/docs/scripting/shims/data) |
| `BasisMenuShim` | Settings tabs and menu panels | [Menus](/en/docs/scripting/shims/menus) |
| `BasisGraphicsSettingsShim` | Read quality settings | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisStatsShim` | Frame rate, bottleneck, connection | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisComputeShim` | Compute shader budget | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisVoiceRoutingShim` | Play a player's voice from an object | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `VideoPlayerShim` | `VideoPlayer` with the URL prompt | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisPlatformShim` | Platform detection | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisTransformBatchShim` | Read or write many transforms at once | [Batching](/en/docs/scripting/shims/batching) |
| `BasisTransformSyncShim` | Copy transforms to transforms every frame | [Batching](/en/docs/scripting/shims/batching) |
| `BasisBlendShapeSyncShim` | Read, write or copy many blendshapes | [Batching](/en/docs/scripting/shims/batching) |
| `BasisVectorArrayShim` | Split and join vector arrays | [Batching](/en/docs/scripting/shims/batching) |
| `BasisSphereCastShim` | Ray and sphere tests on arrays | [Batching](/en/docs/scripting/shims/batching) |
| `BasisAvatarShim` | The avatar component on avatars | [Avatar](/en/docs/scripting/shims/avatar) |
| `BasisVixxyShim` | Read and drive Vixxy menu items | [Avatar](/en/docs/scripting/shims/avatar) |
| `BasisOsc` | OSC messages | [OSC](/en/docs/scripting/osc) |
| `Debug` (`BasisDebugPropsShim`) | Logging tagged with your content | [Sandbox Rules](/en/docs/scripting/sandbox-rules) |

Which sandbox allows which shim is shown live in the Unity editor under **Basis > Creator Guide**, in the Cilbox API Reference section.
