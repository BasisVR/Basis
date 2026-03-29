# Steam Lobby + Steam Transport Implementation Plan

## Goal

Add Steam as an additional networking path for Basis without removing or regressing the current LiteNetLib flow.

The target behavior is:
- `Direct` mode keeps using the current `LiteNetLib` flow.
- `Steam Lobby` mode creates or joins a Steam lobby.
- The host runs the existing Basis host/server flow, but over a Steam transport instead of LiteNetLib.
- Worlds continue to use Basis resource loading.
- Props continue to use the existing Basis resource pipeline and should work automatically once the transport works.

## Non-Goals

- Replacing the current LiteNetLib implementation.
- Rewriting Basis protocol, ownership, avatar sync, voice, or resource systems.
- Moving world selection into `BundledContentHolder.DefaultScene`.

## Architecture Direction

Steam support is added as a parallel transport, not a replacement.

### Existing path kept

- `BasisNetworkConnection -> NetworkClient -> LNLNetManager`
- `BasisNetworkServerRunner -> NetworkServer -> LNLNetManager`
- `ServersProvider` direct-connect UI

### New path added

- `BasisNetworkConnection -> Steam lobby flow -> NetworkClient -> SteamNetManager`
- `BasisNetworkServerRunner -> NetworkServer -> SteamNetManager`
- `ServersProvider` Steam lobby UI flow

## Package Layout

### Runtime

- `Runtime/Bootstrap`
  - Steam API init/shutdown
  - callback pumping
  - runtime guards

- `Runtime/Lobby`
  - lobby create/join/leave
  - lobby metadata
  - lobby state
  - invite handling

- `Runtime/Transport`
  - Steam transport implementation mirroring the current LNL wrapper style
  - `SteamNetManager`
  - `SteamNetPeer`
  - `SteamConnectionRequest`
  - transport factory

- `Runtime/Integration`
  - glue code for BasisNetworkConnection and BasisNetworkManagement
  - host session startup helpers
  - world bootstrap helpers

- `Runtime/Settings`
  - Steam settings asset
  - serializable defaults

- `Runtime/UI`
  - Steam lobby UI helpers for `ServersProvider`
  - optional reusable UI helpers if `ServersProvider` gets too large

### Editor

- `Editor`
  - optional inspectors or validation utilities

### Docs

- `Docs`
  - implementation plan
  - future test checklist

## World Loading Rule

Steam lobby must store world metadata, but world loading itself should continue through the existing Basis resource system.

Reason:
- Basis already has world loading via `RequestSceneLoad` and `LoadResourceChannel`.
- New players already receive existing resources through `BasisNetworkResourceManagement.SendOutAllResources`.
- This preserves late-join behavior automatically.

### Lobby metadata to store

- `world_url`
- `world_name`
- `host_steam_id`
- `transport=steam`
- `version`

Do not store world password in public lobby metadata.

### Host world startup

After host connection succeeds:
- validate world BEE as a `World`
- create or join the lobby
- start local server using Steam transport
- connect local client
- once connected, send the world through the normal Basis resource path using `BasisNetworkSpawnItem.RequestSceneLoad`

Recommended initial behavior:
- `Persist = true`
- `LoadStrategy = 2` for synchronized scene load

### Joiner world flow

When a client joins:
- connect through Steam transport
- complete normal Basis handshake
- receive world resource from `SendOutAllResources`
- load scene through current client-side `LoadResourceMessage`

## Props Rule

Props should remain on the existing Basis resource pipeline.

They already use:
- `RequestGameObjectLoad`
- `LoadResourceChannel`
- `BasisNetworkResourceManagement`
- late-join replay through `SendOutAllResources`

Result:
- no separate Steam-specific prop system should be built
- prop support is considered part of the transport validation matrix

## Required Existing File Changes

### Core networking

- `Packages/com.basis.framework/Networking/BasisNetworkManagement.cs`
  - add selected transport state
  - add runtime session fields for Steam lobby state and pending world

- `Packages/com.basis.framework/Networking/BasisNetworkConnection.cs`
  - add Steam create/join flow
  - add host post-connect world bootstrap
  - keep current direct path unchanged

- `Packages/com.basis.server/BasisNetworkClient/NetworkClient.cs`
  - replace hardcoded `new LNLNetManager(...)` with factory

- `Packages/com.basis.server/BasisNetworkServer/NetworkServer.cs`
  - replace hardcoded `LNLNetManager`
  - change static server type to transport abstraction

- `Packages/com.basis.server/BasisNetworkCore/BasisNetworkShell.cs`
  - add only the minimum abstraction needed for transport polling and identity

- `Packages/com.basis.server/BasisNetworkServer/BasisNetworkingReductionSystem/BasisServerReductionSystemEvents.cs`
  - remove direct dependency on `Server.manager.TriggerUpdate()`

- `Packages/com.basis.server/BasisNetworkServer/BasisServerHandleEvents.cs`
  - preserve IP checks for LiteNetLib
  - add Steam-safe identity path for connection moderation

### UI and runtime integration

- `Packages/com.basis.framework/BasisUI/Menus/Main Menu Providers/ServersProvider.cs`
  - add transport mode switch
  - add Steam lobby creation/join UI
  - add world URL/password inputs for Steam lobby creation
  - keep direct-connect UI working

- `Packages/com.basis.framework/Prefabs/BasisFramework.prefab`
  - add Steam bootstrap/settings component

### Assembly definitions

- `Packages/com.basis.framework/Basis Framework.asmdef`
- `Packages/com.basis.server/BasisNetworkClient/BasisNetworkClient.asmdef`
- `Packages/com.basis.server/BasisNetworkServer/BasisNetworkServer.asmdef`

## New Files To Create In This Package

### Bootstrap

- `Runtime/Bootstrap/BasisSteamBootstrap.cs`
- `Runtime/Bootstrap/BasisSteamRuntimeGuard.cs`

### Lobby

- `Runtime/Lobby/BasisSteamLobbyService.cs`
- `Runtime/Lobby/BasisSteamLobbyState.cs`
- `Runtime/Lobby/BasisSteamLobbyMetadata.cs`

### Transport

- `Runtime/Transport/SteamNetworkImpl.cs`
  - same style as `LNLNetworkImpl.cs`
- `Runtime/Transport/SteamTransportFactory.cs`
- `Runtime/Transport/SteamTransportIdentity.cs`

### Integration

- `Runtime/Integration/BasisSteamConnectionFlow.cs`
- `Runtime/Integration/BasisSteamWorldBootstrap.cs`
- `Runtime/Integration/BasisSteamBeeValidation.cs`

### Settings

- `Runtime/Settings/BasisSteamSettings.cs`

### UI

- `Runtime/UI/BasisSteamLobbyUiState.cs`
- `Runtime/UI/BasisSteamLobbyUiHelpers.cs`

## Implementation Order

### Milestone 1: Transport selection without behavior change

- add `TransportType`
- add transport selection fields to `BasisNetworkManagement`
- add config fields to server configuration
- add factory path
- keep default = LiteNetLib

Success condition:
- project still runs exactly as before in direct mode

### Milestone 2: Steam bootstrap and lobby skeleton

- add Steam bootstrap
- add lobby service
- add settings asset
- no actual connection yet

Success condition:
- can initialize Steam
- can create, list, join, leave lobby

### Milestone 3: Steam transport MVP

- implement Steam transport wrapper against Basis abstractions
- switch client and server to transport factory
- remove direct LiteNet-only update call

Success condition:
- host and client connect over Steam transport
- Basis handshake succeeds

### Milestone 4: World selection and validation

- add world URL/password inputs to Steam lobby create flow
- validate with shared BEE validation helper
- record public world metadata in lobby
- after host connects, call `RequestSceneLoad`

Success condition:
- host enters chosen world
- joining player receives and loads the same world

### Milestone 5: Prop verification and late-join verification

- verify networked prop spawn
- verify synchronized prop spawn
- verify prop unload
- verify late join gets existing props and existing world

Success condition:
- props work in Steam sessions with no prop-specific rewrite

### Milestone 6: UI and fallback polishing

- finish `ServersProvider` UX
- keep direct path intact
- add status messages and loading states

Success condition:
- user can choose either direct connect or Steam lobby cleanly

## Test Matrix

### Direct mode regression

- direct connect still works
- host mode with LiteNetLib still works
- direct world loading still works
- direct prop spawning still works

### Steam mode

- create lobby
- join lobby
- host-selected world loads
- late join receives existing world
- networked prop spawn works
- synchronized prop spawn works
- prop unload works
- disconnect and lobby leave work

### Edge cases

- invalid world URL
- valid BEE but not a world
- unavailable world host URL
- host quits with active world
- late join during synchronized preload

## Manual Unity Work Required

### Before coding much

- import a Steamworks wrapper package
- create a Steam settings asset
- add Steam bootstrap component to `BasisFramework.prefab`
- verify native plugin import settings for standalone
- prepare `steam_appid.txt` for local standalone testing

### During implementation

- test with two standalone builds and two Steam accounts
- test direct mode after every major Steam networking milestone
- verify Addressables only if new addressable content is introduced
- verify world scene spawn and player spawn after scene load

## Package Recommendation For Steam

Recommended starting option:
- `Facepunch.Steamworks` for Steam API + lobbies + networking sockets/relay

Why:
- smaller and cleaner API for the transport/lobby integration
- less boilerplate than `Steamworks.NET`

Fallback option:
- `Steamworks.NET` if project constraints require the official C# wrapper style

## Current Decision Summary

- Steam is an additional transport, not a replacement.
- Worlds use lobby metadata for discovery, but actual spawning goes through Basis resource loading.
- Props stay on the existing resource system and should not need a separate Steam-specific implementation.
- New code should be concentrated in this package and integrated outward with small targeted edits.
