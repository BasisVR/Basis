# Steam Setup And Testing

## Current Stage

This patch is currently at a working MVP stage:

- Steam lobby create/join works
- Steam invite join works
- host/leave/rejoin loop works
- world BEE bootstrap works
- late join world sync works
- voice, movement, and avatar changes were validated in Steam mode
- existing LiteNetLib flow is still intended to remain available as a separate path

## Unity Setup

### Steam wrapper

The project is currently wired against `Facepunch.Steamworks`.

Expected location:
- `Assets/Plugins/Facepunch.Steamworks/`

### Steam settings asset

Create:
- `Assets/Resources/BasisSteamSettings.asset`

Menu path:
- `Create -> Basis -> Steam Settings`

Recommended fields:
- `AppId`: your Steam App ID
- `UseRelayByDefault`: `true`
- `RelayVirtualPort`: `0`
- `CreateFriendsOnlyByDefault`: optional, based on your preferred default
- `EnableTransportTrace`: keep `false` for normal use, enable only for debugging

### Standalone test requirement

For local standalone testing outside Steam packaging:
- place `steam_appid.txt` next to the built `.exe`
- write the target App ID into that file

## Main User Flows

### Host via Steam lobby

1. Open `Steam Lobbies`
2. Enter:
   - `Username`
   - `Lobby Name`
   - `World BEE URL`
   - `World Password`
3. Choose:
   - `Use Relay`
   - `Friends Only`
   - `Private Lobby`
4. Press `Create Steam Lobby`

Expected result:
- lobby is created
- local host session starts
- world loads for the host
- current session panel becomes active

### Join via Steam lobby list

1. Open `Steam Lobbies`
2. Press `Refresh Lobbies`
3. Select a lobby from the dropdown
4. Press `Join Lobby`

Expected result:
- client joins the Steam lobby
- Basis session connects over Steam transport
- world auto-loads for the joiner

### Join via Steam invite

1. Host creates a Steam lobby
2. Host presses `Invite Friends`
3. Remote player accepts invite in Steam overlay

Expected result:
- the client auto-joins the invited Steam lobby
- Basis session connect starts automatically
- world auto-loads

### Leave flow

Host or joiner:
1. Open `Steam Lobbies`
2. Press `Disconnect And Leave Lobby`

Expected result:
- active Basis session disconnects
- Steam lobby state clears
- menu returns to create/join state
- no disconnect popup for intentional leave

## Regression Checklist

### Steam mode

- create lobby
- join from list
- join from invite
- leave and rejoin
- host leave
- host create again after leaving
- voice transmission
- movement sync
- avatar swap sync
- world BEE late join sync

### Existing direct mode

Before upstreaming, retest the original LiteNetLib path:

- direct connect
- host mode
- disconnect / reconnect
- direct world load
- prop load/unload

## Debugging

### Transport trace

Only enable transport trace when needed:
- set `EnableTransportTrace = true` in `BasisSteamSettings.asset`

Trace file:
- `AppData/LocalLow/Basis Unity/Basis Unity/BasisSteamTransport.log`

Keep it disabled for demos, normal builds, and PR cleanup.

## Recommended Pre-Commit Pass

1. Confirm project compiles from a clean Unity reopen
2. Confirm Steam mode flows above
3. Confirm direct/LiteNetLib mode still works
4. Check that `BasisSteamSettings.asset` is configured correctly for the target environment
5. Keep verbose transport tracing disabled unless actively debugging
