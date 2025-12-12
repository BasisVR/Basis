# XProp: Cross-Engine Property Reference

## **Version v0.1.0**

Date: December 2025

## **Status:**

Draft

## Abstract

XProp is a string syntax for addressing properties in game engine scene graphs. It provides sandboxed, cross-engine references with explicit path scoping for user-generated content (UGC) platforms.

---

## 1. Introduction

### 1.1 Purpose

XProp provides:

- **Hierarchical addressing**: Reference properties in scene graph nodes by path
- **Sandboxed access**: Prevent unauthorized references outside allowed boundaries
- **Cross-engine compatibility**: Works across Blender, Unity, Godot, and others
- **Extensibility**: Application-defined facets for custom systems

### 1.2 Scope

XProp defines **syntax only**. Applications implement:

- Resolution behavior (get/set)
- Permission models
- Extension facets
- Serialization format

### 1.3 Design Principles

1. **Explicit scoping**: Path prefix determines resolution path root
2. **Minimal escaping**: Simple escape sequences only where needed
3. **Unambiguous parsing**: Left-to-right, deterministic
4. **Extensible**: Support for custom facets and types

---

## 2. Terminology

| Term | Definition |
|------|------------|
| **Context Node** | Node evaluating the reference (e.g., script's node) |
| **Scope Root** | Sandbox boundary (e.g., avatar root, mod container) |
| **Scene Root** | Global scene hierarchy root |
| **Facet** | Property category (`xform`, `mat`, or custom) |
| **Qualifier** | Facet parameters specifying slot/component |

---

## 3. Syntax

### 3.1 Structure

```txt
prefix[path]::facet[(qualifier)]<type>:property
```

### 3.2 Path Prefixes

The prefix determines where resolution starts:

| Form | Resolves From | Sandboxed | Use Case |
|------|---------------|-----------|----------|
| `./::` | Context node itself | Yes | Reference self |
| `./path::` | Context node + path | Yes | Reference children/descendants |
| `#/::` | Scope root itself | Yes | Reference sandbox boundary |
| `#/path::` | Scope root + path | Yes | Reference within sandbox |
| `/path::` | Scene root + path | No | Global reference (requires permission) |

**Examples:**

```xprop
./::xform<float3>:position              ; my own position
./Hand::xform<float3>:position          ; my child "Hand"
#/::render<bool>:visible                ; scope root visibility
#/Body/Head::xform<quat>:rotation       ; node within scope
/World/Sun::xform<float3>:rotation      ; global reference
```

### 3.3 Path Canonicalization

Paths are case-sensitive and must match node names exactly. Implementations SHOULD normalize:

- Trailing slashes: `#/Body/` -> `#/Body`
- Redundant slashes: `#/Body//Head` -> `#/Body/Head`
- Empty segments removed

Implementations MUST NOT normalize case or apply fuzzy matching.

### 3.4 Unicode in Paths

Path segments support Unicode. Use brackets `[...]` for names with special characters:

```xprop
#/[玩家]::xform<float3>:position                 ; Chinese
#/[Игрок]::xform<float3>:position               ; Cyrillic
#/[Player™]::render<bool>:visible               ; Symbols
#/[My Node 🎮]::script(Health)<float>:current  ; Emoji + spaces
```

**Rules:**

- Unquoted segments: `[A-Za-z0-9_]+` only
- Bracketed segments: Any Unicode sequence except `]` (use `~1` to escape)
- Structural elements (facets, properties, delimiters): ASCII-only

### 3.5 ABNF Grammar

```abnf
xprop          = path-part "::" facet-part ":" property-part

; === Path ===
path-part      = context-rel / scope-rel / absolute
context-rel    = "./" [hierarchy]                ; empty hierarchy = context node itself
scope-rel      = "#/" [hierarchy]                ; empty hierarchy = scope root itself
absolute       = "/" hierarchy                   ; absolute requires at least one segment

hierarchy      = segment *("/" segment)
segment        = unquoted / quoted
unquoted       = 1*name-char
quoted         = "[" 1*(safe-char / escape) "]"

; === Facet ===
facet-part     = facet [qualifier] [type-hint]
facet          = 1*LC-ALNUM                      ; lowercase ASCII only
qualifier      = "(" param *("," ?" " param) ")"
param          = *(qchar / qescape)
qchar          = %x21-27 / %x2A-2B / %x2D-7E    ; printable except whitespace,(,),comma
qescape        = "~3" / "~4" / "~5"              ; escape sequences for , ( )
; Note: ~X where X is not 3,4,5 is valid input, treated as literal "~X"

; === Type Hint ===
type-hint      = "<" type-name ">"
type-name      = ALPHA *name-char                ; must start with letter

; === Property ===
property-part  = prop-segment *("." prop-segment)
prop-segment   = prop-name [index]
prop-name      = prop-start *prop-char
prop-start     = ALPHA / "_"                     ; must start with letter or underscore
prop-char      = ALPHA / DIGIT / "_"             ; letters, digits, underscore, hyphen
index          = "[" array-index "]"
array-index    = "0" / (NON-ZERO *DIGIT)         ; no leading zeros

; === Characters ===
name-char      = ALPHA / DIGIT / "_"
LC-ALNUM       = %x61-7A / DIGIT / "_"           ; a-z, 0-9, _
safe-char      = %x20-5A / %x5C / %x5E-7E        ; printable except [ ] ~
escape         = "~0" / "~1" / "~2"              ; path: ~0=~ ~1=] ~2=[

ALPHA          = %x41-5A / %x61-7A
DIGIT          = %x30-39
NON-ZERO       = %x31-39
```

### 3.6 Limits

Implementations MUST enforce these limits:

| Limit | Value | Rationale |
|-------|-------|-----------|
| Total reference length | 1024 chars | Fits in reasonable buffers |
| Path segments | 32 | Deeper hierarchies are rare |
| Segment name length | 128 chars | Node names shouldn't be excessive |
| Property depth | 16 levels | `a.b.c...` nesting limit (including accessors) |
| Array index | 0–65535 | 16-bit range covers practical cases |
| Qualifier params | 8 | Sufficient for any facet |
| Qualifier param length | 64 chars | Component type names |

Implementations MUST reject references exceeding these limits with `LIMIT_EXCEEDED` error.

### 3.7 Facet Names

Facet names MUST be lowercase ASCII: `[a-z0-9_]+`

**Reserved facets:** `xform`, `mat`

Facets are logical identifiers, not engine type names. The lowercase restriction applies to facet names only—qualifier parameters may use any case (e.g., `script(Rigidbody)` for Unity).

### 3.8 Escape Sequences

#### Path Escapes (inside `[...]` only)

Used to include special characters in node names:

| Escape | Character | Example |
|--------|-----------|---------|
| `~0` | `~` | `[node~0name]` -> "node~name" |
| `~1` | `]` | `[bracket~1test]` -> "bracket]test" |
| `~2` | `[` | `[open~2close]` -> "open[close" |

**Strict parsing:** Unknown escapes (`~X` where X ≠ 0,1,2) cause `PARSE_ERROR`.

**Common cases:**

```xprop
#/[My Node]::xform<float3>:position          ; spaces allowed
#/[path/to/node]::render<bool>:visible       ; slashes literal (single segment)
#/[user@email]::script(Profile)<string>:id   ; special chars
```

#### Qualifier Escapes

Used to include delimiters in qualifier parameters:

| Escape | Character | Example |
|--------|-----------|---------|
| `~3` | `,` | `script(file~3v2.gd)` -> "file,v2.gd" |
| `~4` | `(` | `script(fn~4x~5)` -> "fn(x)" |
| `~5` | `)` | (same as above) |

**Lenient parsing:** Unknown escapes are literal. `my~file.gd` stays as `my~file.gd`.

### 3.9 Property Names and Array Access

Property paths use dot notation (`.`) for nested properties and brackets (`[index]`) for arrays.

#### Property Name Rules

Property names must:

- Start with letter or underscore: `[A-Za-z_]`
- Contain only: `[A-Za-z0-9_-]` (letters, digits, underscore, hyphen)
- Not contain: brackets `[]` (reserved for indexing), dots `.` (reserved for nesting)

**Valid:**

```xprop
./::script(data)<int>:position              ; simple
./::script(data)<int>:_privateField         ; underscore prefix
./::script(data)<int>:player-2_health       ; hyphens and underscores
```

**Invalid:**

```xprop
./::script(data)<int>:2player               ; starts with digit
./::script(data)<int>:-invalid              ; starts with hyphen
./::script(data)<int>:some property         ; spaces not allowed
```

#### Array Indexing

Use `[index]` immediately after property name:

```xprop
./::script(inventory)<int>:slots[0].count
./::script(data)<int>:items[42].name
```

**Rules:**

- Range: `[0-65535]` (16-bit)
- No leading zeros: `[007]` is invalid
- No multidimensional: `[0][1]` is invalid, use `[0].col[1]`

#### Bracket Disambiguation

Brackets serve different purposes in different parts of the reference:

| Location | Purpose | Example |
|----------|---------|---------|
| Before `::` (path) | Quote node names | `#/[Node Name]::...` |
| After `:` (property) | Array indexing | `...:items[0]` |

No ambiguity exists because property names cannot contain brackets.

### 3.10 Parent Traversal

Parent traversal (`..`) is **not supported**. Segments named `..` cause `PARSE_ERROR`. This prevents sandbox escapes.

```xprop
./Child/..::xform<float3>:position           ; INVALID
#/../Sibling::render<bool>:visible           ; INVALID
```

### 3.11 Qualifiers

Qualifiers provide facet-specific parameters: `(param1, param2, ...)`

**One whitespace allowed per item.** Multiple whitespaces causes `PARSE_ERROR`. Use escape sequences for special characters.

**Examples:**

| Facet | Qualifier | Example |
|-------|-----------|---------|
| `xform` | none | `./::xform<float3>:position` |
| `mat` | `(slot)` or `(slot,shader)` | `./::mat(0)<rgba>:color` |
| `script` | `(Type)` or `(Type,index)` | `./::script(Health)<float>:current` |
| `render` | none | `./::render<bool>:visible` |

### 3.12 Type Hints

Optional type annotation: `<type>`

Type names must start with a letter and contain only `[A-Za-z0-9_]`.

**Built-in types:**

| Type | Format | Accessors | Notes |
|------|--------|-----------|-------|
| `bool` | `true`/`false` | - | Boolean |
| `int` | integer | - | Signed 32-bit |
| `float` | number | - | 64-bit double |
| `string` | `"..."` | - | Unicode Sequence |
| `float2` | `[x, y]` | `.x` `.y` | 2D vector |
| `float3` | `[x, y, z]` | `.x` `.y` `.z` | 3D vector |
| `float4` | `[x, y, z, w]` | `.x` `.y` `.z` `.w` | 4D vector |
| `quat` | `[x, y, z, w]` | `.x` `.y` `.z` `.w` | Quaternion (use slerp) |
| `rgb` | `[r, g, b]` | `.r` `.g` `.b` | Color [0-1] |
| `rgba` | `[r, g, b, a]` | `.r` `.g` `.b` `.a` | Color + alpha [0-1] |

Separate types for colors and quaternions enable correct interpolation (sRGB blending for colors, slerp for quaternions, linear for vectors).

Applications may define custom types.

---

## 4. Facets

### 4.1 Reserved Facets

Four facets have standardized semantics:

| Facet | Purpose |
|-------|---------|
| `xform` | Position, rotation, scale |
| `mat` | Surface/shader properties |

#### 4.1.1 `xform`

Spatial transformation. No qualifier.

| Property | Type | Description |
|----------|------|-------------|
| `position` | float3 | Local position |
| `position.x` `.y` `.z` | float | Components |
| `rotation` | quat | Local rotation (x, y, z, w) |
| `rotation.x` `.y` `.z` `.w` | float | Quaternion components |
| `rotation_euler` | float3 | Euler angles (degrees) |
| `rotation_euler.x` `.y` `.z` | float | Pitch, yaw, roll |
| `scale` | float3 | Local scale |
| `scale.x` `.y` `.z` | float | Components |
| `scale_uniform` | float | Uniform scale factor |
| `world_position` | float3 | World-space position |
| `world_rotation` | quat | World-space rotation |

#### 4.1.2 `mat`

Material/surface properties. Qualifier: `(slot)` or `(slot, shader)`.

**Slot** is the material index (0-based). Additional qualifier param is application-defined shader interface (e.g. unity_unlit).

`urp_unlit` shader interface

| Property | Type | Description |
|----------|------|-------------|
| `color` | rgba | Base color RGBA |
| `color.r` `.g` `.b` `.a` | float | Components [0-1] |
| `emission` | bool | Enable emission |
| `emission_color` | float3 | Emission RGB |
| `emission_color.r` `.g` `.b` | float | Components |

Applications MAY support shader-specific properties via native names.

#### 4.1.3 `script`

Component/behavior properties. Qualifier: `(type)` or `(type,index)`.

- **type**: Component type identifier. May use PascalCase to match engine type names directly (e.g., `Rigidbody`, `AudioSource` in Unity), or lowercase with implementation-defined mapping.
- **index**: Instance index when multiple exist (default: 0)

Supports arbitrary nested properties:

```txt
./::script(Inventory)<int>:slots[0].item.count
./::script(QuestLog)<bool>:quests[0].objectives[2].completed
```

**Engine mapping:**

| Engine | Maps To |
|--------|---------|
| Unity | `MonoBehaviour` / `Component` (use PascalCase type names directly) |
| Unreal | `ActorComponent` |
| Godot | Script properties on Node |
| Blender | `Modifier` / `Constraint` by name |

Implementations MAY accept qualifier type names verbatim (recommended for Unity) or apply case-insensitive matching.

### 4.2 Extension Facets

Any facet name not in the reserved set is an extension:

```txt
./::audio(source,0)<float>:volume
./::physics(rigidbody)<float3>:velocity
./::anim(animator)<float>:speed
./::particles(system)<int>:maxCount
```

Applications define qualifier syntax and properties for extensions.

---

## 5. Resolution

### 5.1 Algorithm

```txt
resolve(ref, context, scopeRoot, sceneRoot, permissions):
    parsed = parse(ref)

    base = match parsed.pathType:
        ContextRelative ->
            if parsed.hierarchy is empty:
                context
            else:
                navigate(context, parsed.hierarchy)
        ScopeRelative   ->
            if parsed.hierarchy is empty:
                scopeRoot
            else:
                navigate(scopeRoot, parsed.hierarchy)
        Absolute        ->
            if not permissions.allowAbsolute:
                error(PERMISSION_DENIED)
            navigate(sceneRoot, parsed.hierarchy)

    if base is null:
        error(NODE_NOT_FOUND)

    if parsed.pathType in [ContextRelative, ScopeRelative]:
        boundary = context if ContextRelative else scopeRoot
        if not isDescendantOrSelf(base, boundary):
            error(SANDBOX_ESCAPE)

    handler = getHandler(parsed.facet)
    if handler is null:
        error(FACET_UNKNOWN)

    value = handler.resolve(base, parsed.qualifier, parsed.property)

    if parsed.typeHint and not matches(value, parsed.typeHint):
        error(TYPE_MISMATCH)

    return value

isDescendantOrSelf(node, boundary):
    ; Returns true if node is the boundary itself, or a descendant of boundary
    current = node
    while current is not null:
        if current == boundary:
            return true
        current = current.parent
    return false
```

### 5.2 Errors

| Code | Cause |
|------|-------|
| `PARSE_ERROR` | Invalid syntax, uppercase facet, unknown path escape, parent traversal, whitespace in qualifier |
| `LIMIT_EXCEEDED` | Reference exceeds size limits |
| `PERMISSION_DENIED` | Absolute path without permission |
| `SANDBOX_ESCAPE` | Path escaped allowed boundary |
| `NODE_NOT_FOUND` | Hierarchy unresolvable |
| `FACET_UNKNOWN` | Unrecognized facet |
| `QUALIFIER_INVALID` | Malformed qualifier |
| `COMPONENT_NOT_FOUND` | Component not on node |
| `PROPERTY_NOT_FOUND` | Property doesn't exist |
| `INDEX_OUT_OF_BOUNDS` | Array index invalid |
| `TYPE_MISMATCH` | Value doesn't match hint |

---

## 6. Engine Mappings

### 6.1 Unity

| XProp | C# |
|-------|-----|
| `./Child::` | `transform.Find("Child")` |
| `#/Path/Node::` | `scopeRoot.transform.Find("Path/Node")` |
| `/Path/Node::` | `GameObject.Find("/Path/Node")` |
| `./::xform<float3>:position` | `transform.localPosition` |
| `./::xform<float>:rotation.euler.y` | `transform.localEulerAngles.y` |
| `./::mat(0)<rgba>:color` | `renderer.materials[0].color` |
| `./::script(T)<float>:prop` | `GetComponent<T>().prop` |
| `./::script(T,1)<float>:prop` | `GetComponents<T>()[1].prop` |
| `./::render<bool>:visible` | `renderer.enabled` |

**Example Unity behaviours (using PascalCase type names):**

```txt
; Physics
./::script(Rigidbody)<float3>:velocity
./::script(Rigidbody)<float>:mass
./::script(Rigidbody)<bool>:useGravity

; Audio
./::script(AudioSource)<float>:volume
./::script(AudioSource)<float>:pitch
./::script(AudioSource)<bool>:mute

; Rendering
./::script(Light)<rgba>:color
./::script(Light)<float>:intensity
./::script(Camera)<float>:fieldOfView

; Animation
./::script(Animator)<float>:speed
./::script(Animator)<bool>:applyRootMotion

; UI
./::script(Slider)<float>:value
./::script(Toggle)<bool>:isOn
./::script(InputField)<string>:text
```

### 6.2 Blender

TODO

### 6.3 Godot

TODO

---

## 7. Security

### 7.1 Sandboxing

Implementations MUST:

1. Reject paths escaping their boundary
2. Require explicit permission for absolute paths
3. Validate syntax before resolution
4. Enforce size limits
5. Reject parent traversal segments (`..`)

### 7.2 Script and Extension Facet Access

For `@script` and custom extension facets, implementations MUST NOT rely solely on engine serialization/visibility attributes for access control. In untrusted UGC contexts:

- Implementations SHOULD maintain an explicit allowlist of exposed properties
- Properties not on the allowlist MUST NOT be accessible
- Allowlists SHOULD be defined per-component-type

This prevents accidental exposure of sensitive serialized fields (API keys, internal state, etc.) that happen to be marked serializable for editor purposes.

Reserved facets (`xform`, `mat`, `render`) expose well-defined property sets and do not require additional allowlisting.

---

## 9. Regular Expression

For implementations that prefer regex-based tokenization, the following pattern captures the major tokens. Note that this regex performs initial extraction only; implementations MUST still validate limits, escape sequences, and detailed syntax rules.

EDITOR NOTE: this is a wip, use carefully.

```regex
^(?:(?<ctx_rel>\.\/(?<ctx_path>(?:[^:\[\]]+|\[[^\]]*\])*))|(?<scope_rel>#\/(?<scope_path>(?:[^:\[\]]+|\[[^\]]*\])*))|(?<abs>\/(?<abs_path>(?:[^:\[\]]+|\[[^\]]*\])+)))::(?<facet>[a-z0-9_]+)(?:\((?<qualifier>[^)]*)\))?(?:<(?<type>[A-Za-z][A-Za-z0-9_]*)>)?:(?<property>.+)$
```

**Named capture groups:**

| Group | Description |
|-------|-------------|
| `ctx_rel` | Full context-relative prefix (`./...`) |
| `ctx_path` | Path portion of context-relative reference (may be empty) |
| `scope_rel` | Full scope-relative prefix (`#/...`) |
| `scope_path` | Path portion of scope-relative reference (may be empty) |
| `abs` | Full absolute prefix (`/...`) |
| `abs_path` | Path portion of absolute reference (must not be empty) |
| `facet` | Facet name (lowercase) |
| `qualifier` | Raw qualifier content (without parens, may contain escapes) |
| `type` | Type hint name |
| `property` | Property path |

**Limitations:**

- Does not validate bracketed segment escapes
- Does not validate qualifier parameter escapes  
- Does not validate array index format
- Does not enforce limits
- Does not validate property path structure
- Does not detect invalid path segments like `..` (parent traversal)
- Will match structurally-correct but semantically-invalid references

---

## 10. Examples

### 10.1 Self-Reference

```txt
./::xform<float3>:position              ; my position
./::xform<float>:scale.uniform          ; my uniform scale
./::render<bool>:visible                ; am I visible?
./::script(Health)<float>:current       ; my health (PascalCase component)
```

### 10.2 Children of Context

```txt
./Hand::xform<quat>:rotation
./Hand/Index/Tip::xform<float3>:position
./Effects/Glow::render<bool>:visible
./Mesh::mat(0)<float>:opacity
```

### 10.3 Scope-Relative (UGC Sandbox)

```txt
#/Body/Head::xform<float3>:rotation.euler
#/Body/LeftArm/Hand::mat(0)<rgba>:color
#/Scripts::script(AvatarController)<float>:speed
#/::render<bool>:visible                ; scope root itself
```

### 10.4 Absolute (Privileged)

```txt
/World/Sun::xform<float3>:rotation.euler
/UI/HUD/HealthBar::script(Slider)<float>:value
/GameManager::script(GameState)<bool>:isPaused
```

### 10.5 Complex Properties

```txt
./::script(Inventory)<int>:slots[0].count
./::script(Inventory)<string>:slots[0].item.name
./::script(Inventory)<float>:slots[0].item.stats.damage
./::script(QuestLog)<bool>:quests[0].objectives[2].done
```

### 10.6 Escaped Path Names

```txt
#/[My Node]::xform<float3>:position          ; "My Node"
#/[path/to/thing]::render<bool>:visible      ; "path/to/thing" (single segment)
#/[user@email.com]::script(Profile)<string>:id
#/[bracket~1test]::render<bool>:visible      ; "bracket]test"
#/[open~2close]::render<bool>:visible        ; "open[close"
#/[tilde~0here]::render<bool>:visible        ; "tilde~here"
```

### 10.7 Escaped Qualifier Parameters

```txt
./::script(res://scripts/player.gd)<float>:speed           ; Godot resource path (no escapes needed)
./::script(My~3Component)<int>:value                       ; Component named "My,Component"
./::script(Func~4x~5)<float>:result                        ; Component named "Func(x)"
```

### 10.8 Unity Behaviours

```txt
; Physics
./::script(Rigidbody)<float3>:velocity
./::script(Rigidbody)<float>:mass
./::script(Rigidbody)<bool>:useGravity

; Audio
./::script(AudioSource)<float>:volume
./::script(AudioSource)<float>:pitch
./::script(AudioSource)<bool>:mute

; Rendering
./::script(Light)<rgba>:color
./::script(Light)<float>:intensity
./::script(Camera)<float>:fieldOfView

; Animation
./::script(Animator)<float>:speed
./::script(Animator)<bool>:applyRootMotion

; UI
./::script(Slider)<float>:value
./::script(Toggle)<bool>:isOn
./::script(InputField)<string>:text
```

---

## Appendix A: Implementation Guidance

### A.1 Reference Caching

Implementations SHOULD cache parsed references and resolved bindings. XProp references are typically static strings that resolve to the same targets throughout a session.

### A.2 Error Handling

Implementations should distinguish between:

1. **Parse-time errors**: Detected during `parse()`, independent of scene state
2. **Resolution-time errors**: Detected during `resolve()`, dependent on scene state

Parse-time errors indicate malformed references or incompatable facets and are typically programming errors. Resolution-time errors may be transient (node not yet loaded) or permanent (node deleted).

### A.3 Type Coercion

When the resolved value type differs from the type hint, implementations MAY perform safe coercions:

| From | To | Coercion |
|------|----|----------|
| `int` | `float` | Widen |
| `float` | `int` | Truncate (with warning) |
| `float3` | `float4` | Extend with w=0 |

Implementations SHOULD NOT silently coerce between incompatible types (e.g., `string` to `float`).

### A.4 Roundtrip Normalization

Implementations MAY normalize references during roundtrip (`parse` -> `str`). The following normalizations are semantically equivalent and permitted:

- Removing unnecessary quoting: `#/[Simple]@...` -> `#/Simple@...`
- Canonicalizing escape sequences

Escaped characters inside bracketed segments are unescaped during parsing and re-escaped (if needed) during serialization. An implementation that parses `#/[test~1node]@...` will store the segment as `test]node` internally, and may serialize it back as `#/[test~1node]@...`.

---

## Appendix B: Quick Reference

```txt
┌─────────────────────────────────────────────────────────────────┐
│  XProp v0.1.0                                                   │
├─────────────────────────────────────────────────────────────────┤
│  SYNTAX                                                         │
│    prefix[path]::facet(qualifier)<type>:property[idx].sub       │
│                                                                 │
│  PATH PREFIX                                                    │
│    ./::...        context node (self)                           │
│    ./path::...    relative to context                           │
│    #/::...        scope root itself                             │
│    #/path::...    relative to scope root                        │
│    /path::...     absolute (needs permission)                   │
│                                                                 │
│  FACETS (reserved, lowercase only)                              │
│    xform                     position, rotation, scale          │
│    mat(slot)                 color, metallic, roughness...      │
│    script(Type,idx)          component properties               │
│    render                    visible, pickable...               │
│                                                                 │
│  QUALIFIER PARAMETERS                                           │
│    May use PascalCase for engine type names (e.g., Rigidbody)   │
│                                                                 │
│  PATH ESCAPES (inside [...] only)                               │
│    ~0 = ~     ~1 = ]     ~2 = [                                 │
│                                                                 │
│  QUALIFIER ESCAPES                                              │
│    ~3 = ,     ~4 = (     ~5 = )                                 │
│                                                                 │
│  PROPERTY NAMES                                                 │
│    Must start with: letter or underscore                        │
│    Can contain: letters, digits, underscores, hyphens           │
│    Cannot contain: brackets [ ] or dots .                       │
│    Examples: position, my-property, _private, player2           │
│                                                                 │
│  TYPES (must start with letter)                                 │
│    bool  int  float  string                                     │
│    float2  float3  float4  quat  rgba  rgb                      │
│                                                                 │
│  ACCESSORS                                                      │
│    Vectors: .x .y .z .w                                         │
│    Colors:  .r .g .b .a                                         │
│    Arrays:  [0] [1] [2] (no leading zeros, no multidimensional) │
│                                                                 │
│  LIMITS                                                         │
│    Total length: 1024    Path depth: 32    Segment: 128         │
│    Property depth: 16    Array index: 65535                     │
│                                                                 │
│  NOT PERMITTED                                                  │
│    Uppercase facets    Parent traversal (..)    Leading zeros   │
│    Multidimensional arrays [0][1]   Brackets in property names  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Changelog

### v0.1.0

- Initial draft
