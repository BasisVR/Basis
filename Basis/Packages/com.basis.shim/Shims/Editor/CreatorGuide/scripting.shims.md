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
| `BasisLockShim` | Lock movement, looking or crouching | [Players](/en/docs/scripting/shims/players) |
| `BasisEyeTrackingShim` | Where the local player is looking | [Players](/en/docs/scripting/shims/players) |
| `BasisControllerInputShim` | VR controller buttons, triggers, sticks | [Input](/en/docs/scripting/shims/input) |
| `BasisDesktopInputShim` | Mouse buttons, movement and scroll | [Input](/en/docs/scripting/shims/input) |
| `BasisHapticsShim` | Controller vibration | [Input](/en/docs/scripting/shims/input) |
| `BasisInteractableShim` | Pressable objects | [Input](/en/docs/scripting/shims/input) |
| `BasisActionTrigger` | UI pointer events | [Input](/en/docs/scripting/shims/input) |
| `BasisUIExtensions` | `Toggle.AddListener` | [Input](/en/docs/scripting/shims/input) |
| `BasisInputShim` | Bind keys, mouse, gamepad and controls as slots | [Input](/en/docs/scripting/shims/input) |
| `BasisLocalInputShim` | Mouse and controller reads for props and avatars | [Input](/en/docs/scripting/shims/input) |
| `BasisKeyboardShim` | Ask the player to type some text | [Input](/en/docs/scripting/shims/input) |
| `BasisHandBlockShim` | Stop a hand pointing, grabbing or touching | [Input](/en/docs/scripting/shims/input) |
| `BasisGrabShim` | What the local player's hands are holding | [Input](/en/docs/scripting/shims/input) |
| `BasisPlatformEventShim` | VR / desktop switches | [Events](/en/docs/scripting/shims/events) |
| `BasisLanguageEventShim` | Language changes | [Events](/en/docs/scripting/shims/events) |
| `BasisGraphicsSettingsEventShim` | Quality changes | [Events](/en/docs/scripting/shims/events) |
| `BasisPermissionEventShim` | Local permission changes | [Events](/en/docs/scripting/shims/events) |
| `BasisJiggleEventShim` | Someone grabs or touches jiggle bones | [Events](/en/docs/scripting/shims/events) |
| `BasisBeforeRenderShim` | Late update after IK, before drawing | [Events](/en/docs/scripting/shims/events) |
| `BasisContactEventShim` | Contact receivers touched and released | [Events](/en/docs/scripting/shims/events) |
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
| `BasisUrl` | A web address set in the inspector, read only | [Data](/en/docs/scripting/shims/data) |
| `BasisMenuShim` | Settings tabs and menu panels | [Menus](/en/docs/scripting/shims/menus) |
| `BasisSettingRequestShim` | Ask the player to open one of their settings | [Menus](/en/docs/scripting/shims/menus) |
| `BasisSettingFavouritesShim` | Read and drive the player's favourite settings | [Menus](/en/docs/scripting/shims/menus) |
| `BasisGraphicsSettingsShim` | Read quality settings | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisStatsShim` | Frame rate, bottleneck, connection | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisComputeShim` | Compute shader budget | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisVoiceRoutingShim` | Play a player's voice from an object | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `VideoPlayerShim` | `VideoPlayer` with the URL prompt | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisPlatformShim` | Platform detection | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisCameraShim` | `Camera.Render` on your own cameras, with a per frame budget | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisSteamAudioShim` | Steam Audio on your own audio sources, plus world acoustic geometry | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisRemotePlayerAudioShim` | Override Steam Audio on a remote player's voice | [Rendering and Audio](/en/docs/scripting/shims/rendering-and-audio) |
| `BasisSpawnShim` | Choose where players arrive and respawn | [Spawning](/en/docs/scripting/shims/spawning) |
| `Instantiate` (`BasisCilboxInstantiateShim`) | `Instantiate` with the clone cleaned before it turns on | [Spawning](/en/docs/scripting/shims/spawning) |
| `BasisModelShim` | Build models from GLB, glTF, OBJ, STL or PLY bytes | [Spawning](/en/docs/scripting/shims/spawning) |
| `BasisScriptBus` | Messages and published values between content on this client | [Script Bus](/en/docs/scripting/shims/script-bus) |
| `BasisScriptTarget`, `BasisScriptMessage`, `BasisScriptReference` | Handles to content, messages and single scripts | [Script Bus](/en/docs/scripting/shims/script-bus) |
| `BasisSharedTransform`, `BasisSharedTexture` | Share a transform or texture with other content | [Script Bus](/en/docs/scripting/shims/script-bus) |
| `BasisTransformBatchShim` | Read or write many transforms at once | [Batching](/en/docs/scripting/shims/batching) |
| `BasisTransformSyncShim` | Copy transforms to transforms every frame | [Batching](/en/docs/scripting/shims/batching) |
| `BasisBlendShapeSyncShim` | Read, write or copy many blendshapes | [Batching](/en/docs/scripting/shims/batching) |
| `BasisVectorArrayShim` | Split and join vector arrays | [Batching](/en/docs/scripting/shims/batching) |
| `BasisSphereCastShim` | Ray and sphere tests on arrays | [Batching](/en/docs/scripting/shims/batching) |
| `BasisAvatarShim` | The avatar component on avatars | [Avatar](/en/docs/scripting/shims/avatar) |
| `BasisVixxyShim` | Read and drive Vixxy menu items | [Avatar](/en/docs/scripting/shims/avatar) |
| `BasisPoseOverrideShim` | Pose the local player's bones and IK targets | [Pose](/en/docs/scripting/shims/pose) |
| `BasisRemotePoseShim` | Pose other players' avatars on this client | [Pose](/en/docs/scripting/shims/pose) |
| `BasisOsc` | OSC messages | [OSC](/en/docs/scripting/osc) |
| `Debug` (`BasisDebugPropsShim`) | Logging tagged with your content | [Sandbox Rules](/en/docs/scripting/sandbox-rules) |

Which sandbox allows which shim is shown live in the Unity editor under **Basis > Creator Guide**, in the Cilbox API Reference section.
