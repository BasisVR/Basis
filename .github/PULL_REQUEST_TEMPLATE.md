## Summary
<!-- What does this PR change and why? Link related issues. -->

## Required checks
All boxes below must be ticked before this PR can merge. If a check is genuinely N/A, tick it anyway and explain under **Notes**.

<!-- required-checks-start -->
- [ ] **Tested** — I built and ran this locally. The change works in the editor and (where relevant) in a built player.
- [ ] **Transform access is combined and limited** — In hot paths, transform reads/writes go through `TransformAccessArray` or are otherwise batched. I have not added per-frame `transform.position` / `transform.rotation` / `transform.localPosition` calls inside loops.
- [ ] **Addressables used for asset/memory loading** — Any new asset loads go through Addressables. No new `Resources.Load`, no direct asset references that pull large content into memory on scene load.
- [ ] **No new `GetComponent` / `AddComponent` where avoidable** — Where unavoidable, the result is cached on a field. None of these calls run inside `Update`, `LateUpdate`, `FixedUpdate`, jobs, or other per-frame code paths.
- [ ] **Per-frame work is scheduled through `BasisEventDriver`** — Any new per-frame work hooks into `BasisEventDriver` rather than adding standalone `Update` / `LateUpdate` / `FixedUpdate` callbacks on a MonoBehaviour.
- [ ] **Considered jobification** — I asked whether this work can be moved to a Unity Job (Burst-compiled where possible). If it can, it is. If it cannot, the reason is in **Notes**.
- [ ] **No needless `{ get; set; }` properties or access lockdowns** — Public fields are fine; Basis is meant to be read and modified freely, so don't wall things off `private`/`internal` without a real reason. Don't wrap a field in `{ get; set; }` when the accessors do nothing — property accessors have a real performance cost vs direct field access, and the lead maintainer prefers plain fields (or a method / setter-only property when only the setter needs logic) over a noop-getter pair. For `.Instance` singletons, callers reassigning `Type.Instance` is allowed; if that would break your code, log a warning or throw — don't block the assignment. Locking down access is not your call.
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
