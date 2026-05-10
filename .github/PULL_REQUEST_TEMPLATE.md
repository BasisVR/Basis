## Summary
<!-- What does this PR change and why? Link related issues. -->

## Required checks
All boxes below must be ticked before this PR can merge. If a check is genuinely N/A, tick it anyway and explain under **Notes**.

<!-- required-checks-start -->
<!-- Tick the boxes in place — do not edit the line text. The pr-checklist workflow parses this block; per-PR context goes under Notes. -->
- [ ] **Tested** — I built and ran this locally. The change works in the editor and (where relevant) in a built player.
- [ ] **Transform access is combined and limited** — In hot paths, transform reads/writes go through `TransformAccessArray` or are otherwise batched. I have not added per-frame `transform.position` / `transform.rotation` / `transform.localPosition` calls inside loops. Whenever I need both position and rotation, I use the combined APIs — `SetPositionAndRotation` / `SetLocalPositionAndRotation` for writes, `GetPositionAndRotation` / `GetLocalPositionAndRotation` for reads — instead of two separate property accesses; the combined call does one local-to-world matrix traversal instead of two.
- [ ] **Addressables used for asset/memory loading** — Any new asset loads go through Addressables. No new `Resources.Load`, no direct asset references that pull large content into memory on scene load.
- [ ] **No new `GetComponent` / `AddComponent` where avoidable** — Where unavoidable, the result is cached on a field, and any `GetComponent<T>` is replaced with `TryGetComponent<T>(out var x)` — bare `GetComponent` will be denied. `TryGetComponent` is the modern API (Unity 2019.2+) and skips the Editor-only GC allocation `GetComponent` causes when a component is missing: Unity wraps the `null` return in a managed "fake null" object so its overloaded `==` operator can still detect destroyed C++ objects, and constructing that wrapper allocates; `TryGetComponent` returns a `bool` plus `out` parameter and never builds the wrapper. None of these calls run inside `Update`, `LateUpdate`, `FixedUpdate`, jobs, or other per-frame code paths.
- [ ] **Per-frame work is scheduled through `BasisEventDriver`** — Any new per-frame work hooks into `BasisEventDriver` rather than adding standalone `Update` / `LateUpdate` / `FixedUpdate` callbacks on a MonoBehaviour.
- [ ] **Considered jobification** — I asked whether this work can be moved to a Unity Job (Burst-compiled where possible). If it can, it is. If it cannot, the reason is in **Notes**.
- [ ] **No needless `{ get; set; }` properties or access lockdowns** — Public fields are fine; Basis is meant to be read and modified freely, so don't wall things off `private`/`internal` without a real reason. Don't wrap a field in `{ get; set; }` when the accessors do nothing — property accessors have a real performance cost vs direct field access, and the lead maintainer prefers plain fields (or a method / setter-only property when only the setter needs logic) over a noop-getter pair. For `.Instance` singletons, callers reassigning `Type.Instance` is allowed; if that would break your code, log a warning or throw — don't block the assignment. Locking down access is not your call.
- [ ] **Camera access goes through `BasisLocalCameraDriver`** — Code that needs the local camera (transform, projection, rig data, etc.) pulls it from `BasisLocalCameraDriver` rather than looking one up itself. Don't roll a separate camera discovery path.
- [ ] **Logging uses `BasisDebug`** — All new logging calls go through `BasisDebug.Log` / `BasisDebug.LogWarning` / `BasisDebug.LogError` (with an appropriate `LogTag`) instead of `UnityEngine.Debug.Log` / `Debug.LogWarning` / `Debug.LogError`. `BasisDebug` routes through Basis's tagged, color-coded logger and respects the project-wide `LoggingDisabled` toggle so logging can be killed at runtime; bare `Debug.Log` calls bypass that and will be denied.
- [ ] **No scene-wide discovery for dependencies** — New code is architected so it does not need `FindObjectOfType` / `FindObjectsOfType` / `GameObject.Find` / `FindGameObjectsWithTag` to locate what it depends on. References are wired in — registered through an existing manager/driver, injected at init, or passed in by the caller — rather than discovered by scanning the scene at runtime. If a scene scan is genuinely unavoidable, justify it under **Notes**.
- [ ] **No allocations in hot paths** — Per-frame code (Update / LateUpdate / FixedUpdate, simulation loops, jobs, anything called once per frame or more) does not allocate. No `new` on reference types, no LINQ, no `string` concatenation/interpolation, no boxing, no `foreach` over interface-typed collections. Allocate once at init and reuse the buffer.
- [ ] **No debugging in hot paths** — No log calls of any kind on per-frame paths, including `BasisDebug`. Hot-path logging floods the console and incurs cost on every frame regardless of whether the message is filtered out downstream. If a hot-path log is needed while iterating, gate it behind `#if UNITY_EDITOR` and remove (or leave gated) before merge.
- [ ] **Hot-path collection access is optimized** — Cache `.Count` (lists) / `.Length` (arrays) into a local `int` before the loop instead of re-reading the property each iteration. Prefer `T[]` (with a separate length int when the array is over-sized) over `List<T>` where the data is hot — Unity's mono BCL doesn't expose `CollectionsMarshal.AsSpan(List<T>)`, so a list can't be fed into `Span<T>` / unsafe paths cleanly. Where the perf justifies it, drop into `Span<T>` / `ref` locals / `Unsafe.As` / `unsafe` pointer code to skip bounds checks and copies, and call out the invariants you're relying on under **Notes** so reviewers can sanity-check them.
<!-- required-checks-end -->

## Testing details
Tick the platforms you actually tested on. Leave the rest unticked — these are informational and do not block merge.

- [ ] Windows
- [ ] Linux
- [ ] Android
- [ ] iOS
- [ ] macOS

Input / control mode coverage:

- [ ] Tested in VR (note headset under **Notes**)
- [ ] Tested in desktop / non-VR mode
- [ ] Tested with phone controls (mobile touch input)
- [ ] N/A — change does not touch player/XR/input code

Where applicable, confirm these flows still work after your changes:

- [ ] Hot-switching (desktop ↔ VR mode swap at runtime)
- [ ] Avatar swapping
- [ ] Server swapping (joining / leaving / changing servers)
- [ ] N/A — change does not touch any of the above

## Notes
<!-- Optional context for reviewers. Headset model, why a required box is N/A, anything else worth knowing. -->
