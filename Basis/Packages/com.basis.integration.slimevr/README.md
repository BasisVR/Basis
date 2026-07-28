# Basis SlimeVR Integration

Connects Basis to a running [SlimeVR server](https://github.com/SlimeVR/SlimeVR-Server) over the
[SolarXR protocol](https://github.com/SlimeVR/SolarXR-Protocol) and uses the data SlimeVR already
has about your body, so you don't have to calibrate your size in Basis at all.

## What it does

- **Auto body size**: pulls SlimeVR's skeleton config (bone lengths + user height) and applies it
  to the Basis height system. `PlayerEyeHeight` comes straight from SlimeVR's user height
  (its internal user height *is* the standing floor-to-eye height), and the arm span is derived
  from the skeleton: `SHOULDERS_WIDTH + 2 x (UPPER_ARM + LOWER_ARM + HAND_Y)` — the distance
  between where the controllers sit, which is what Basis measures during a manual calibration.
  Both are persisted as the saved calibrated body size, so the very first avatar load next
  session is already at the right scale.
- **Stays in sync**: the skeleton config is re-polled, so running SlimeVR's autobone, its height
  calibration, or editing proportions in the SlimeVR GUI flows into Basis within seconds.
- **Tracker telemetry**: a datafeed provides every tracker's role, status, battery level, voltage
  and signal strength (`BasisSlimeVRBridge.Trackers` + `OnTrackersUpdated`) for HUDs and menus.
- **Resets**: SlimeVR's yaw / full / mounting resets can be triggered from inside Basis
  (`BasisSlimeVRBridge.TriggerYawReset()` etc., also exposed in Settings > Tracker Settings).
  The menu buttons run through a shared pose countdown
  (`BasisSlimeVRBridge.StartPoseCountdown`) so there is time to get into pose after pressing;
  pressing the button again cancels. The length is the **Pose Countdown** slider
  (`slimevr_pose_countdown_seconds`, default 4 s, 0 = instant). The `Trigger*` methods stay
  immediate for bindings.
- **Auto tracker roles (no calibration step)**: `BasisSlimeVRTrackerRoles` registers SlimeVR's
  convention — virtual SteamVR trackers carry their body part in their serial (`human://WAIST`,
  `human://LEFT_FOOT`, ...) — with the framework's announced-role scanner
  (`BasisAnnouncedTrackerRoles`), which forces the matching role and runs the automatic full-body
  calibration. Gated by **Auto Bind SlimeVR Trackers** in this package's settings section
  (default on); works off the SteamVR serials alone, so it functions even when the SolarXR
  connection is off. The framework's own opt-in **Trust SteamVR Tracker Roles** (off by default)
  covers roles assigned by hand in SteamVR settings for any tracker brand.

## How it connects

The transport is a user setting (`slimevr_transport`, see below):

- **WebSocket** (default) `ws://127.0.0.1:21110`: one `MessageBundle` per binary message, no
  length prefix. Works with every released server today.
- **Pipe** (server v20.1+): `\\.\pipe\SlimeVRRpc` on Windows, `$XDG_RUNTIME_DIR/SlimeVRRpc` unix
  socket on Linux. Framing: 4-byte little-endian length prefix that includes itself, then a
  flatbuffers `MessageBundle`. This is SlimeVR's forward path — websockets are slated for
  deprecation — so the default flips here once fixed servers are the norm.

A background thread owns the connection, reconnects every few seconds while the server is absent,
and marshals everything onto the main thread through `BasisDeviceManagement.mainThreadActions`.
When no SlimeVR server is installed the integration idles at zero cost beyond a periodic failed
connect.

**Server pipe bug (as of v20.1.0 / 2026-07 main):** the Windows named pipe bridge accepts
connections and reads requests, but every response it writes is a zero-byte write —
`WindowsNamedPipeRpcConnection.send` builds the length-prefixed buffer `src` and then sends
`bytes.array(), bytes.remaining()`, which is 0 after `src.put(bytes)` consumed the buffer (fix:
send `src.array(), src.remaining()`). Its reader also kills the connection on any message over
1024 bytes (fix: compare `buf.capacity()`). Both fixes are verified: a server built from patched
main serves this client over the pipe end-to-end (full skeleton config, datafeed, resets,
heartbeats — byte-identical data to the websocket). On a broken/mute pipe the client logs a hint
after 6 s pointing at the WebSocket setting, then keeps retrying the selected transport.

## Settings

Settings > Tracker Settings > SlimeVR:

- **Connect To SlimeVR** (`slimevr_enable`, default on)
- **Connection Method** (`slimevr_transport`, `"websocket"` default / `"pipe"`) — applied live;
  changing it reconnects.
- **Auto Apply Body Measurements** (`slimevr_applybodymeasurements`, default on)
- **Auto Bind SlimeVR Trackers** (`slimevr_autobind`, default on) — see auto tracker roles above.

**Trust SteamVR Tracker Roles** on the same tab is a core framework setting, not part of this
package.

Seated mode is respected: measurements received while seated are held and applied when you stand.
Manual calibration still works and simply overwrites the SlimeVR values until the next config poll.

## Debugging

`Basis > Tests And Debug > SlimeVR Debug` shows the live connection, the raw skeleton parts, the
derived measurements, all trackers with battery/signal, and reset buttons.

## Protocol code

- `Runtime/SolarXR/Generated` — C# generated from the SolarXR schema with flatc **v22.10.26**
  (the version the SolarXR repo pins). Regenerate with:
  `flatc --csharp --gen-all -o Generated -I schema schema/all.fbs`
- `Runtime/SolarXR/FlatBuffers` — the Google FlatBuffers C# runtime, same version.

See `THIRD_PARTY_NOTICES.md` for licenses (SolarXR: MIT/Apache-2.0 dual, FlatBuffers: Apache-2.0).
