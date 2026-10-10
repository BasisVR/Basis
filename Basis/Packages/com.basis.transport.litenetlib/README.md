# Basis LiteNetLib Transport

The UDP network transport for Basis, built on [LiteNetLib](https://github.com/RevenantX/LiteNetLib).
It adds the `litenetlib` network stack, which is Basis's default, plus the UDP server-list probe and
NAT-punched direct connections between players.

Basis installs this package by default, but Basis code does not depend on it. It is a Basis server
package, so the same folder is compiled into the Unity client and into the server.

| Folder | Built into |
|---|---|
| `Runtime/` | `BasisNetworkCore` (the client and the server), through `BasisNetworkCore.asmref` |
| `Runtime/LiteNetLib/` | the LiteNetLib library itself, compiled into the same assembly |
| `Server~/BasisServerTests/` | the server test project, so its tests run with `dotnet test` |

## Installed by default

Basis keeps a copy in each half of the repository, so the Unity project and the server each work
without the other:

- `Basis Server/Packages/com.basis.transport.litenetlib`: the server installs it through
  `Basis Server/Packages/manifest.json`
  (`"com.basis.transport.litenetlib": "file:com.basis.transport.litenetlib"`), and
  `packages-lock.props` compiles `Runtime/` into `BasisNetworkCore`. Because it is inside
  `Basis Server`, the Docker image build has it too.
- `Basis/Packages/com.basis.transport.litenetlib`: an embedded Unity package, so a checkout without
  `Basis Server` still has it.

Edit the copy in `Basis Server/Packages`; `ExportSourceFiles.ps1` mirrors it into `Basis/Packages`
(everything but `Server~`).

## Taking it out

Delete both copies and remove the server manifest's entry (or run
`basispm server-remove com.basis.transport.litenetlib`). Basis still builds and runs: nothing is
registered for the `litenetlib` stack, so servers and clients need another transport package, and
plain `host:port` addresses still parse. To move it to its own repository, delete both copies and
install it by git URL in both manifests.

## What it registers

`BasisLiteNetLibTransport` (the package entry point) registers, for the `litenetlib` stack:

- the transport (`LNLNetManager`), its `host:port#password` address parser and its settings file,
  `config/transports/litenetlib.xml`, with the comments written into it;
- the server-list probe (`BasisLiteNetLibProbe`), an unconnected UDP ping;
- direct connections: the server's NAT introducer (`LNLPeerIntroducer`) and the client's punch and
  connect socket (`BasisLiteNetLibP2PSocket`), encrypted per endpoint by `BasisCryptoLayer`;
- the server's load-control hooks (`IBasisTransportScaling`): peer update worker pool, send socket
  growth under pressure, and the queue bounds reported on `/health`.

In a web build it registers nothing: browsers have no UDP, and the WebSocket transport package
serves the `litenetlib` stack id there instead. In the Unity editor and players it always uses
managed sockets.

## Tests

`Server~/BasisServerTests/` holds the LiteNetLib tests (merge framing and soak tests, the voice
priority queue, socket buffer warnings, the direct-connection packet layer) and the tests for this
package's registration, settings file and load-control hooks. They run with the server suite
whenever the package is installed.

## License

The Basis code in this package is MIT licensed (see `LICENSE`). LiteNetLib is MIT licensed by
Ruslan Pyrch; see `THIRD-PARTY-NOTICES.md`.
