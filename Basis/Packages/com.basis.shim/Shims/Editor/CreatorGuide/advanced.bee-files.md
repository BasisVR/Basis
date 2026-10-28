A `.bee` (Basis Encrypted Experience) file is the single file every world, prop and avatar ships as. It holds a description of the content plus one Unity build per platform, all encrypted with the password you set when building.

## Layout

```
┌──────────────────────────────┐
│ Header: connector length     │  8 bytes, little endian Int64
├──────────────────────────────┤
│ Connector (encrypted JSON)   │  metadata, sections table
├──────────────────────────────┤
│ Section 1 (encrypted)        │  e.g. StandaloneWindows64
├──────────────────────────────┤
│ Section 2 (encrypted)        │  e.g. Android
├──────────────────────────────┤
│ ...                          │
└──────────────────────────────┘
```

Sections follow the connector back to back, in the order the connector lists them. The connector stores each section's length, which is enough to work out where every section starts without scanning the file.

## The connector

The connector is a JSON document (`BasisBundleConnector`) that describes the content without loading it:

| Field | Contents |
| --- | --- |
| `UniqueVersion` | A build ID. Changes every build. |
| `BasisBundleDescription` | Name, description, tags, content group and authorship (author, contact, license, credits). |
| `BasisBundleGenerated[]` | One entry per section: platform, asset mode, asset name, CRC, hash, length and graphics APIs. |
| `ImageBase64` | The thumbnail shown in menus and on content orbs. |
| `FarLodBase64` | Avatars only: a tiny stand in drawn at long distance. |
| `DateOfCreation` | Build time (UTC). |
| `Bounds` | The content's size, used to place it before it loads. |
| `MetaData` | Triangle, material and bone counts, texture memory, render pipeline, content kind (Avatar, Prop or Scene), prop spawn placement and a count of each component type. |

Because the connector is small and comes first, menus can show names, thumbnails and performance stats after downloading only the first few kilobytes.

## Sections

Each `BasisBundleGenerated` entry describes one section:

| Field | Meaning |
| --- | --- |
| `Platform` | The build target, such as `StandaloneWindows64` or `Android`. `Generic` marks the glTF fallback. |
| `AssetMode` | `Scene` for worlds, `GameObject` for avatars and props, `GLTF` for the generic fallback. |
| `AssetToLoadName` | The scene or prefab to open from the bundle. |
| `AssetBundleCRC`, `AssetBundleHash` | Integrity checks for the decrypted Unity bundle. |
| `EndByte` | The section's length in the file. |
| `AssetBundleEndByte` | Where the Unity bundle ends inside the decrypted section, when extra data follows it. |
| `GraphicsStatePayloads` | Offsets of embedded shader prewarm data per graphics API and quality level. |

A decrypted section is a Unity **AssetBundle**, optionally followed by **graphics state** data used to prewarm shaders so the content does not stutter the first time it is seen.

The **Generic** section is avatar only: an encrypted `.glb` (glTF 2.0 binary) plus avatar setup data. A client falls back to it only when the file has no section for its platform. That way an avatar still shows up on platforms it was never built for.

## Encryption

The connector and each section are encrypted separately with the content password:

- Cipher: AES with a 256 bit key.
- Key: derived from the password with PBKDF2 (SHA-1, 10,000 iterations) and a random 16 byte salt.
- Layout of each encrypted block: `salt (16 bytes) | IV (16 bytes) | ciphertext`.

Sections over a few megabytes are decrypted in parallel chunks. One decrypted section can be at most about 2 GB; the file as a whole has no such limit, because only one section is ever decrypted.

> [!warn]
> The password protects content from casual copying and lets you control who can load it. Anyone with the URL and password can decrypt it. Do not put secrets in content.

## How a client loads a BEE

1. Read the first 8 bytes (the header) with an HTTP range request.
2. Download and decrypt the connector. Menus can now show the name, thumbnail and stats.
3. Walk the sections table to find this platform's section and work out its byte range from the lengths.
4. Download only that section with a range request. Other platforms' sections never leave the server.
5. Decrypt it, check the CRC and load the AssetBundle (or build the avatar from glTF).
6. Cache the file locally. Next time the client only checks whether the content changed.

This is why one `.bee` can carry Windows, Linux and Android builds without costing Quest players the download size of the PC build.

## Hosting

- Any static host that supports **HTTP range requests** works.
- Upload the `.bee` and share its URL and password. The password is what players or your load config use to unlock it.
- Rebuilding produces a new `UniqueVersion`, which lets clients tell builds apart. Clients revalidate cached copies against the host, and replacing the file at the same URL updates it for everyone.
