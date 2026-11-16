A Basis package is an ordinary Unity package with two extra concerns. Content police removes its components from loaded content unless they are approved, and Cilbox scripts cannot call its types unless they are whitelisted. A package can declare both for itself, and Basis picks the declarations up when the package is installed. Nobody has to edit the lists in `com.basis.sdk`.

## Installing packages

The Basis Package Manager clones each package into `.basisdev/<package id>` inside the Unity project and adds a `file:` line for it to `Packages/manifest.json`. It also records the package in `Packages/basispackages.json`:

```json
{
  "format": 1,
  "packages": {
    "com.basis.landiscovery": {
      "version": "0.1.0",
      "url": "https://github.com/Toys0125/LANDiscovery.git",
      "ref": "v0.1.0",
      "commit": "5ff091537bad13e91c5bf9e2cde1fc9e3b018d74"
    }
  }
}
```

Commit `basispackages.json` with the project. Plenty of people keep `.basisdev` out of git, and with the file in the repo a fresh checkout shows each package as missing and the Package Manager can clone it again at the recorded commit. The commit is the upstream one from install time. Local edits to a clone never update it, because a teammate cannot fetch a commit that only exists on your machine.

## Approving components for content police

Content police works from the selector assets described in [Allowed Components](/en/docs/scripting/allowed-components). A package adds its own types by shipping a JSON file in a `Resources/BasisContentPolice/` folder:

```text
com.example.lights/
  Runtime/
    Resources/
      BasisContentPolice/
        com.example.lights.json
```

```json
{
  "World": ["Example.Lights.DmxLight", "Example.Lights.DmxController"],
  "Prop": ["Example.Lights.DmxLight"]
}
```

The keys are `Avatar`, `Prop`, `World` and `System`, one per selector, and any of them can be left out. Each entry is the full type name with its namespace. Matching is exact, as it is for the rest of the allowlist. The selector inspector shows the file name as the source of each type, so name the file after the package id.

Once the file is in a package, these happen without any further steps:

- At runtime every selector with **Include Package Approvals** ticked merges the package lists into its allowlist. All four selectors in `com.basis.sdk/Settings` have it ticked.
- In the editor, Basis adds any missing package types to those selectors' saved lists whenever a file in a `BasisContentPolice` folder is imported, moved or deleted, and after every script reload. It logs the types it added.
- The selector inspector shows package approved types ticked and greyed out, with the file they came from.
- The SDK inspectors' content police preview treats them as allowed.

The editor sync only ever adds. Removing a package leaves its type names in the selector assets until you untick them.

> [!info]
> Approvals only apply in clients built with the package. Content loaded by a client without it is policed by that client's lists, so a component from your package is removed there like any other unknown type.

## Exposing types to Cilbox

A package makes its own shim callable from scripts with two attributes from `Basis.Scripts.BasisSdk.Cilbox`. The package's assembly has to reference the Basis SDK assembly to use them.

```csharp
using Basis.Scripts.BasisSdk.Cilbox;

[BasisCilboxAllow(BasisCilboxBoxes.World | BasisCilboxBoxes.Prop)]
public static class ExampleLightsShim
{
    public static int ChannelCount => 512;
    public static void SetChannel(int channel, float value) { }

    [BasisCilboxBlock(BasisCilboxBoxes.Prop)]
    public static void ResetAll() { }

    [BasisCilboxAllow(BasisCilboxBoxes.World)]
    public static float MasterLevel;
}

[BasisCilboxAllow(BasisCilboxBoxes.All, AllFields = true)]
public struct ExampleLightState
{
    public int Channel;
    public float Value;
}
```

`BasisCilboxBoxes` picks the sandboxes: `World` (the scene sandbox), `Prop`, `Avatar`, or `All`. The rules follow the box whitelists:

- On a type, every public method becomes callable in the listed boxes.
- Fields stay denied. Put the attribute on the field, or set `AllFields = true` on the type.
- Nested types need their own attribute unless the outer one sets `Nested = true`.
- `[BasisCilboxBlock]` holds back a method, property or constructor in the listed boxes. Whitelists match by name, so it blocks every overload with that name, and on a property it blocks both accessors.
- Hard denied types (`IntPtr`, `Marshal`, network and settings internals) stay denied whatever the attribute says.

The first time a sandbox checks a type, Basis scans every loaded assembly that references the Basis SDK for these attributes, once per session. Content cannot add entries this way. Scripts in content are interpreted, and only assemblies compiled into the client carry attributes.

If your shim is a component that sits in content, it also needs a content police entry from the section above.

## Keeping members through IL2CPP

`BasisCilboxAllowAttribute` derives from `PreserveAttribute`, and IL2CPP keeps the type. A member that only scripts call can still be stripped, since nothing compiled into the client references it. Ship a `link.xml` in the package to keep it:

```xml
<linker>
  <assembly fullname="Example.Lights">
    <type fullname="ExampleLightsShim" preserve="all" />
  </assembly>
</linker>
```

Unity only reads `link.xml` files under `Assets`. Before every player build, Basis collects the `link.xml` files in `Packages/` and in local and git packages and folds them into `Assets/Basis/link.xml`. **Basis > Build > Update Link XML** runs the same step by hand.

## Checking a package

- Open a selector asset in `com.basis.sdk/Settings` and search for your types. Package types show greyed out with your file name.
- Put a component from the package in a test world and look at the SDK inspector's content police preview.
- Call your shim from a `[Cilboxable]` script and enter play mode. If the attribute was not picked up, the call falls outside the whitelist and Basis refuses it or raises the script permission prompt, depending on the player's Script Permissions settings.
