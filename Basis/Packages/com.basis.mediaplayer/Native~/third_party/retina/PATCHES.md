# Vendored retina 0.4.19

A copy of the `retina` crate (MIT OR Apache-2.0, see README.md), applied through
`[patch.crates-io]` in the workspace root. The benches and lockfile are
dropped. The source matches the crates.io release apart from these changes:

- `src/client/rtp.rs`, `InorderParser::new`: an all-zero SSRC in the SETUP or
  PLAY response is treated as unstated rather than pinned. Some servers
  (VRCDN's RTSP edge among them) advertise `ssrc=00000000` while the RTP stream
  carries a real SSRC, and pinning the placeholder rejects every packet.
- `src/codec/aac.rs`, `pull`: a stream that never sets the RTP marker bit still
  gets one access unit per packet, where upstream failed the session. RFC 3640
  wants the marker set, but mediamtx omits it on these packets and common
  clients accept them. mediamtx also splits an access unit across packets whose
  AU-headers each state only their own packet's size, marking only the last;
  once a stream has been seen to set the marker, such packets are gathered until
  it arrives (at most 8,191 bytes).
- `src/codec/aac.rs`, `push`: the body moved to `push_inner`, and the public
  entry point discards any reassembly in progress when it returns an error.
  Upstream keeps the fragment state across a refusal, which is harmless in
  retina's own receive loop because a refusal ends the session. A UDP loop that
  treats refusals as loss and keeps feeding the depacketizer would otherwise
  append the next packet to a prefix already rejected and emit a truncated
  access unit. After a refusal the depacketizer is `Idle`, carries the loss
  count forward and marks the next access unit as damaged: one completed by
  reassembly at that marker is dropped, one arriving whole in a single packet
  is kept. Nothing is emitted with damage in it, so a consumer sees a gap.
  `media-rtsp/tests/aac_reassembly.rs` covers both cases.
- `src/client/mod.rs`, `Session<Playing>`: `take_udp_sockets(i)` hands over a
  stream's connected RTP and RTCP sockets (the session stops polling them;
  keepalives and the control connection carry on), and `take_depacketizer(i)`
  hands over the stream's depacketizer with its SDP parameters applied.
  Retina's UDP receive has no reorder buffer and sends no RTCP receiver
  reports, which servers end sessions over, so the engine does both itself.
  `initial_rtptime(i)` (with `Timeline::start`) reads the PLAY response's
  `RTP-Info` `rtptime` as the initial timestamp policy applied it, so the
  engine's UDP receive zeroes each stream where the server says, as retina's
  own receive does.
- `src/client/mod.rs`, `SessionOptions::udp_peer_validator` and `setup()`: an
  optional callback that checks the UDP peer address before the sockets
  connect or send hole-punch packets. The server controls the `Transport`
  header's `source` parameter, so without it a hostile RTSP server could aim
  UDP traffic at any address, internal ones included.
- `src/lib.rs`, `UdpPair::for_ip`: the even/odd port bind loop retries on
  `PermissionDenied` as well as `AddrInUse`. Windows reports ports in its
  excluded ranges (`netsh interface ipv4 show excludedportrange`) as
  `WSAEACCES`, so without the retry a few percent of UDP set-ups fail on a
  stock Windows machine.
- `src/tokio.rs`, `Connection::from_stream` and `Codec::decode`, and
  `src/rtsp/parse.rs`, `Parser::feed_inner`: an RTSP message is capped at
  1 MiB, head and body together (`MAX_MESSAGE_BYTES`). Upstream builds the
  connection's parser with no limit and reserves the peer's `Content-Length`
  up front, so one reply header could ask for any amount of memory; a head of
  endless header lines, or one line that never ends, grew without limit too.
  `media-rtsp/tests/rtsp_message_bounds.rs` covers all three.
- `src/codec/h264.rs`, `push_inner`: one access unit may gather at most
  16 MiB of RTP payload (`MAX_AU_PAYLOAD_BYTES`), 65,536 NAL units and
  65,536 payload pieces (`MAX_AU_NALS`, `MAX_AU_PIECES`); past any of them
  the push is refused and the access unit dropped. Upstream closes an
  access unit only on the marker bit or a new timestamp, so a sender that
  sent neither grew it without limit, and one-byte NAL units or fragments
  built millions of entries inside the payload ceiling. The counts clear
  one slice per macroblock at level 5.2 (36,864) with every SPS and PPS
  beside them. The H.265 depacketizer has the same shape and is left
  alone: the engine sets up no H.265 stream. Covered by
  `media-rtsp/tests/h264_access_unit_bound.rs`.
- `src/codec/h265/nal.rs`, `Sps::from_bits` and `ScalingListData::from_bits`:
  the reads of `palette_max_size` and `scaling_list_delta_coef` propagate
  their errors. Upstream dropped them, so an SPS with an unreadable code in
  either place parsed on from the wrong bit position and was accepted.
  Covered by `media-rtsp/tests/h265_sps_refusal.rs`.
- `src/lib.rs`: `#![deny(unsafe_code, unsafe_op_in_unsafe_fn)]`. The crate
  is outside the workspace, so the workspace lint table and the gate's
  clippy run never reach it; this makes rustc refuse any `unsafe` that does
  not carry its own `#[allow(unsafe_code)]`. Three of upstream's four
  `unsafe` sites are replaced with safe code (`client/mod.rs`, `poll_udp`:
  the receive buffer is an array of `MaybeUninit`; `codec/h265/nal.rs`:
  `UnitType` converts through a table and a cast rather than
  `transmute`). The one left, `CaseInsensitive::new` in `src/rtsp/msg.rs`,
  carries the allow.
- `src/client/mod.rs`, `SessionOptions::connect_addrs`, `RtspConnection::connect`
  and `src/client/teardown.rs`: optional addresses that `DESCRIBE` dials in
  order instead of resolving the URL's host, starting the next when one
  fails or has gone 250 ms unanswered and keeping the first to connect
  (`CONNECT_STAGGER` in `src/tokio.rs`, after RFC 8305), so silent
  addresses cannot use up the caller's deadline; a `TEARDOWN`'s fresh
  connections dial the one `DESCRIBE` reached. The engine resolves and vets
  the host itself, and upstream's own lookup was a second answer the
  vetting never saw; a `TEARDOWN` also followed the server's `Content-Base`
  to whatever host it named. Covered by
  `media-rtsp/tests/rtsp_pinned_address.rs`.
- `src/client/mod.rs`, `Session` drop: a dropped session retries `TEARDOWN`
  in the background for at most two minutes (`MAX_TEARDOWN_WINDOW`), not for
  the whole session timeout the server states, which may be decades.

Each is a candidate for an upstream report or pull request to
scottlamb/retina. The copy can go once a release carries them.
