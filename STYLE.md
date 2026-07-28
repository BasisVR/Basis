# Basis style and review guide

This is the deeper companion to [CONTRIBUTING.md](./CONTRIBUTING.md). CONTRIBUTING covers the contributor flow — how to file issues, open PRs, get builds running. This file covers what reviewers actually push back on once a PR is open: the checklist explained, the recurring review themes, and the formatting baseline.

If you're new, skim [CONTRIBUTING.md](./CONTRIBUTING.md) first and come back here when you're about to open a PR.

## Formatting

- **Match the formatting of the surrounding file.** No formatter is enforced in CI. A `.editorconfig` and a CSharpier tool manifest are checked in, but the codebase doesn't currently conform to either — don't reformat existing files wholesale.
- **C# nullable annotations** aren't enforced project-wide; follow the surrounding file.
- **Comments are lean by default.** Comment when the *why* is non-obvious — workarounds, hidden invariants, performance reasons. Don't restate what the code already says.
- **Use the framework's own conventions** for new code: `BasisDebug`, `BasisLocalCameraDriver`, `BasisEventDriver`, `Try*` patterns, Addressables.

## The PR checklist, explained

The [pull request template](../.github/PULL_REQUEST_TEMPLATE.md) has a checklist that **must** be ticked before merge. Read it once before you start the work, not after — most of the items are easier to do as you go than to retrofit. Here's what each headline rule means in practice:

- **Tested locally** in the editor and (where it matters) a built player.
- **Transform reads/writes batched** — `TransformAccessArray`, or `Get/SetPositionAndRotation` so you do one matrix traversal instead of two.
- **Addressables for asset loading** — no new `Resources.Load`.
- **`TryGetComponent` not `GetComponent`**, results cached on a field, never on a per-frame path.
- **Per-frame work goes through `BasisEventDriver`**, not standalone `Update` / `LateUpdate` / `FixedUpdate`.
- **Camera access via `BasisLocalCameraDriver`** — don't roll your own discovery.
- **Logging via `BasisDebug`** with an appropriate `LogTag`. No bare `Debug.Log`. No logging at all on per-frame paths.
- **No scene-wide discovery** — `FindObjectOfType`, `GameObject.Find`, and `transform.Find` are denied. Wire dependencies in instead.
- **No allocations on hot paths** — no `new`, no LINQ, no string interpolation, no boxing, no `foreach` over interface-typed collections.
- **Hot loops are tight** — cache `.Count` / `.Length` into a local before the loop; prefer `T[]` over `List<T>` where the data is hot.
- **Jobs considered.** If the work can move to a Burst-compiled job, it should. If not, say why under **Notes**.

If a box is genuinely N/A, tick it and explain why under **Notes**. "I forgot" isn't an explanation; "this is a one-shot init path, no per-frame impact" is.

## What reviewers look for

The checklist catches the mechanical stuff. These are the recurring themes that come up in review beyond the checklist:

**Reuse existing surface before adding new.** The framework already has a lot of callbacks, events, drivers, and registries. Before introducing a new event or hook, check whether something existing fires at the right moment — `StartDevice` / `StopDevice`, `StaticCurrentMode` callbacks, `BasisEventDriver`, the network `Try*` helpers, etc. Reviewers will (rightly) push back on "I added a new callback for X" when an existing one would have served. If the existing one doesn't quite fit, name it in the PR description and explain the gap; that turns a rejection into a design discussion.

**Validate at the boundary, not at every call site.** If you're adding null checks throughout a hot path because data might be malformed, fix it where the data enters the system instead. "Could we validate this code another way leading to always having valid data?" comes up a lot.

**Put functionality on the natural owner.** A microphone-related helper belongs on the microphone driver, not floating in a UI script. Reviewers will ask you to move it.

**Use the Try-pattern when failure is expected.** `TryGetOwnershipId(out var id)` over `GetOwnershipId()` returning a sentinel; `TryGetComponent<T>(out var x)` over `GetComponent<T>()` plus a null check. The Try variants are clearer at the call site and avoid Unity's `null`-wrapper allocation on missing components.

**Hash strings once.** If you're looking up animator parameters, shader properties, or anything else by name in a loop, use `Animator.StringToHash` (etc.) once and store the int. Don't pay the lookup cost every frame.

**Public fields are fine.** Don't wrap a plain field in `{ get; set; }` for the sake of it — the accessors aren't free, and the project's house style is plain fields unless a setter actually needs logic. If you want to lock something down to `private`/`internal`, have a real reason; Basis is meant to be read and modified.

**Don't gate `.Instance` reassignment.** Singletons can be reassigned by callers; if that breaks downstream code, log a warning or throw a clear error — don't try to prevent it.

**Editor-only code should refuse to run at runtime, loudly.** A clear error message ("only use this at runtime / only use this in the editor") is much better than mysterious null-refs.

**Explain what and why.** A PR description that says "fix the thing" without describing what the thing is or why the change works will get bounced. The Summary section of the template exists for this — use it.

**Async is fine; manufactured complexity to avoid async is not.** If a server call is the natural way to get an answer, make people `await` it. Don't build elaborate state-machinery to dodge a synchronous wait.

**Be wary of fragile patterns.** Wildcard matches, reflection over Unity types, or anything that could become an "oh shit" the next time Unity adds something to a namespace — flag it and discuss before committing to it.

## Testing in depth

The PR template has a testing checklist; here's what those rows actually mean.

**Platforms.** Tick the platforms you actually built and ran on. Windows is the baseline; Android (Quest) is the second-most-trafficked target. Linux/iOS/macOS coverage is appreciated but not always required — if you can't test a platform, leave it unticked rather than guessing.

**Input modes.** VR, desktop, and mobile touch all share code paths but stress different ones. Note the headset model under **Notes** if you tested in VR.

**Critical flows.** Hot-switching desktop ↔ VR at runtime, swapping avatars, joining/leaving servers. These are common regression sources because they exercise teardown and re-init paths. If your change touches XR, avatars, networking, or input, retest these.

If you genuinely cannot test something — no Quest, no Mac, etc. — say so in the PR. Honesty is faster than a fake green tick.
