# Basis Server packages

Server packages add code to the Basis Server build. A package's server code is compiled straight
into one of the server's own assemblies, the same way an `.asmref` folder is compiled into an
existing assembly in Unity, so one package can carry a feature (a network transport, for example)
for both the Unity client and the server.

## Files

| File | Committed | What it is |
|---|---|---|
| `manifest.json` | yes | The packages this server uses: package id to source. |
| `packages-lock.props` | yes | Generated from the manifest. Pins each git package to a commit and tells MSBuild which folders compile into which assembly. |
| `packages-local.props` | no | This machine's folder links (`server-link`). |
| `<id>/` | no | The package clones. |

The Basis Package Manager writes all of these: the Server tab, or `basispm server-install`,
`server-update`, `server-remove`, `server-restore`, `server-link` and `server-unlink`. After editing
`manifest.json` by hand, run `basispm --project <repo> server-restore` to regenerate the lock.

`dotnet restore`, `build` and `publish` clone any package folder that is missing, at the commit in
the lock, so a fresh checkout, CI and the Docker build need nothing but git. Only https, http, git
and ssh remotes are cloned; pass `-p:BasisServerPackagesGitProtocols=<list>` to change that. A build
that starts with a package missing (for example `--no-restore`) stops with an error rather than
building the server without it.

## Sources

```json
{
  "dependencies": {
    "com.example.transport": "https://github.com/example/BasisExampleTransport.git#v1.0.0",
    "com.example.tools": "https://github.com/example/ExampleTools.git?path=Packages/com.example.tools",
    "com.example.local": "file:../../Basis/Packages/com.example.local"
  }
}
```

`#ref` is a branch, tag or commit and `?path=` is the package's folder inside the repository.
`file:` paths are relative to this folder; a `file:` package outside `Basis Server` is not part of
the Docker build context, so an image that needs a package installs it from git.

## Writing a package

A server package is a Unity package folder (`package.json` with a `name` and `version`) whose
server code compiles into one of these assemblies:

| Assembly | Built into |
|---|---|
| `BasisNetworkCore` | the client and the server |
| `BasisNetworkServer` | the server (standalone, and Unity host mode) |
| `BasisNetworkClient` | the client (Unity, and the load test client) |
| `BasisNetworkConsole` | the standalone server executable only |
| `BasisServerTests` | the server test project, so a package's tests run with `dotnet test` |

Package code becomes part of that assembly, so it can use the assembly's `internal` members.
Without a `basisServer` section in `package.json`, the Package Manager finds the modules itself:

- every `.asmref` that references `BasisNetworkCore`, `BasisNetworkServer` or `BasisNetworkClient`
  (by name or GUID): its folder compiles into that assembly on the server, exactly as Unity compiles it;
- every `Server~/<assembly>/` folder: server-only code that Unity never sees.

Or list the modules explicitly:

```json
"basisServer": {
  "modules": [
    { "path": "Runtime/Network", "assembly": "BasisNetworkCore" },
    { "path": "Server~/Commands", "assembly": "BasisNetworkConsole" }
  ],
  "nuget": { "Some.Library": "1.2.3" }
}
```

Inside a module, folders with their own `.asmdef` or `.asmref`, and folders whose names end in `~`
or start with `.`, are left out, matching Unity. Shared code has to build as C# 9 for Unity and for
both netstandard2.1 and net10.0 on the server.

## Entry point

```csharp
using Basis.Network.Core;

[assembly: BasisNetworkPackage(typeof(ExampleTransportPackage))]

public sealed class ExampleTransportPackage : IBasisNetworkPackage
{
    public void Initialize()
    {
        BasisNetworkStackRegistry.Register("example", "Example", (listener, configuration) => new ExampleNetManager(listener, configuration));
        BasisNetworkStackRegistry.RegisterParser("example", new ExampleConnectionTargetParser());
        BasisTransportConfigStore.RegisterType("example", typeof(ExampleTransportConfig));
    }
}
```

Each entry runs once per process:

- standalone server: entries in `BasisNetworkCore`, `BasisNetworkServer` and `BasisNetworkConsole`,
  before `config.xml` loads, so a transport config type registered here gets its
  `config/transports/<id>.xml`;
- Unity: entries in `BasisNetworkCore`, `BasisNetworkClient` and `BasisNetworkServer`, at
  `SubsystemRegistration`;
- load test client: entries in `BasisNetworkCore` and `BasisNetworkClient`.

Tests do not run entries: call the package's registration from the tests that need it.

A failing entry is logged and the rest still run. `/packages` in the server console (or
`basisctl /packages`) lists the packages built into the server and the entries that ran.

## Transport packages

A network transport is a `NetManager` (with its `NetPeer` and `ConnectionRequest`) whose code
compiles into `BasisNetworkCore`. The WebSocket transport, `com.basis.transport.websocket`, is a
complete example: it is a separate package, and Basis does not name it anywhere. Its entry point
registers the transport with these hooks:

| Hook | What it is for |
|---|---|
| `BasisNetworkStackRegistry.Register(id, displayName, factory)` | The stack id servers list in `NetworkStackId`, and the factory that creates the transport. |
| `RegisterParser(id, parser)` | Turns a saved server address into host, port and password for this transport. |
| `RegisterProbe(id, probe)` | The server-list ping (name, player count, round trip) over this transport. |
| `RegisterAddressMatcher(id, matcher)` | Claims addresses this transport owns (for example `wss://...`), so the Servers panel and web join links pick it without the player choosing a stack. |
| `RegisterPump(action)` / `RegisterTick(id, action)` | Work the client runs every frame, for transports that poll on the main thread. |
| `RegisterIntroducerFactory(id, factory)` | Peer-to-peer introduction, for transports that support direct connections. |
| `ReplaceFactory(id, factory)` | Stands in for another stack, for example on a platform where that stack cannot run. |
| `BasisTransportConfigStore.RegisterType(id, type)` | The transport's settings file, `config/transports/<id>.xml`. |
| `BasisConfigXmlDocs.Register(type, header, fields)` | The comments written into that file. |

And these interfaces, which the server and client check for:

| Interface | Implemented by | What it is for |
|---|---|---|
| `IBasisSharedPeerIds` | the `NetManager` | Takes player ids from the shared allocator, so a server running several transports never gives two players the same id. |
| `IBasisTransportHealth` | the `NetManager` | Extra fields for this transport's entry under `transports` on `/health`. |
| `IBasisTransportTimeouts` | the config type | The disconnect timeout the client's connection watchdog uses. |

`NetPeer.StackId` names the peer's transport and `NetPeer.SupportsDirectConnect` says whether it can
take part in peer-to-peer sessions. Unity-only code (for example a browser channel and its `.jslib`)
goes in a folder with its own `.asmdef` that references `BasisNetworkCore`; it is never built into
the server, and it can register itself with `[RuntimeInitializeOnLoadMethod]`.
