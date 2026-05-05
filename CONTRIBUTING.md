# Contributing to Basis

Thanks for thinking about contributing! Basis is an MIT-licensed open-source social VR framework, and it only gets better when other people pile in. This document covers how to report bugs, request features, and get a pull request through review without too much back-and-forth.

If you only read one other thing before contributing, read [PHILOSOPHY.md](./PHILOSOPHY.md). It frames what Basis is trying to be, which makes design conversations a lot easier.

## Quick links

- [Discord](https://discord.gg/F35u3cUMqt) — the fastest way to ask questions, share work-in-progress, or check whether someone's already tackling something.
- [README.md](./README.md) — project overview, install steps, command-line flags.
- [PHILOSOPHY.md](./PHILOSOPHY.md) — what Basis is for and who it's aimed at.
- [CI.md](./CI.md) — what you need to set up if you're forking and want CI to build.
- [Pull request template](./.github/PULL_REQUEST_TEMPLATE.md) — the merge checklist. Read this before you open a PR.
- [Issue templates](./.github/ISSUE_TEMPLATE/) — bug report and feature request forms.

## Ways to help

You don't have to write code:

- **File good bugs.** A reproducible bug report with logs is genuinely valuable — it's often the difference between "we'll get to it eventually" and "fixed this evening."
- **Suggest features** with the use case attached. "I want to do X and I can't because Y" beats a bare wishlist.
- **Test pre-release builds** and report what breaks on your hardware. Headset/OS coverage is always thin.
- **Write docs and samples.** If something tripped you up, write down the fix — that helps the next person.
- **Donate.** [Liberapay](https://liberapay.com/dooly/donate) · [GitHub Sponsors](https://github.com/sponsors/dooly123) · [Ko‑fi](https://ko-fi.com/dooly).

## Reporting bugs

Open a bug report via [the bug template](./.github/ISSUE_TEMPLATE/1-bug.yml). Please fill in all of the required fields — they're required because reviewers genuinely need them:

- **What happened** vs **what you expected** — reviewer time goes a lot further when you separate observation from interpretation.
- **Steps to reproduce** — even an approximate sequence beats none.
- **Build Info** — copy this from `Settings → Developer → Copy Build Info`. It tells us your platform, build, XR loader, and a bunch of other context in one paste.
- **Log files** — the relevant snippet, not the whole file. If you're not sure what's relevant, include a few seconds before and after the failure.
- **Screenshots / video** — for anything visual or behavioural, a clip is worth a thousand words.

Search [open issues](https://github.com/BasisVR/Basis/issues) before filing — there's a decent chance someone's already on it, and adding a reproduction step to an existing thread is more useful than a duplicate.

## Requesting features

Use [the feature request template](./.github/ISSUE_TEMPLATE/2-feature-request.yml). The template asks for the **problem**, your **preferred solution**, and **alternatives you considered** — the alternatives field matters more than people expect; it's where you show you've thought about trade-offs, and it often surfaces the simpler answer.

Big design changes are easier to land if you talk them through on Discord first. A short conversation up front saves a long PR rewrite later.

## Reporting security issues

If you find a vulnerability, **please don't open a public issue.** Email `developerbasis@gmail.com` with details and a reproduction. Coordinated disclosure means the fix can land before the exploit goes wide.

## Pull requests

### Workflow

1. **Fork** the repo on GitHub.
2. **Branch from `developer`.** `developer` is the default branch — `main` is not what you want.
3. Pick a descriptive branch name. `feat/third-person-camera`, `fix/audio-mute-on-rejoin`, `docs/contributing` — anything that reads cleanly. There's no enforced format, but conventional-style prefixes line up with our commit messages.
4. Push to your fork and open a PR back into `BasisVR/Basis:developer`.

### Commit messages

We encourage the use of [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) style for the summary line:

```
<type>[optional scope]: <description>
```

Common types: `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `chore`, `ci`. Scope is optional but useful (`feat(sdk):`, `fix(face-tracking):`, `feat(networking):`). Keep the summary under ~72 characters; put the rest in the body. If a commit fixes an issue, reference it (`Closes #123`).

Squash trivial fixup commits before opening the PR, but keep meaningfully separate changes as separate commits — it makes the diff easier to review.

### Before opening the PR

The [pull request template](./.github/PULL_REQUEST_TEMPLATE.md) has a checklist that **must** be ticked before merge. Read it once before you start the work, not after — most of the items are easier to do as you go than to retrofit. The headline rules:

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

### What reviewers look for

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

### Tone in PRs and review

Reviews on Basis are friendly and direct. If a reviewer pushes back on a design choice and points at existing surface, the path of least resistance — and usually the right one — is to rewrite to use it. Save the architectural debate for cases where you have concrete evidence the existing surface won't work. PRs that argue elegance over getting merged tend to stall.

If you're the reviewer, lead with what's good before what needs work, and link to the existing surface or call site you're pointing at. "There's a callback for this on `BasisXRManagement` already" is more useful than "you don't need this."

## Testing

The PR template has a testing checklist; here's what those rows actually mean.

**Platforms.** Tick the platforms you actually built and ran on. Windows is the baseline; Android (Quest) is the second-most-trafficked target. Linux/iOS/macOS coverage is appreciated but not always required — if you can't test a platform, leave it unticked rather than guessing.

**Input modes.** VR, desktop, and mobile touch all share code paths but stress different ones. Note the headset model under **Notes** if you tested in VR.

**Critical flows.** Hot-switching desktop ↔ VR at runtime, swapping avatars, joining/leaving servers. These are common regression sources because they exercise teardown and re-init paths. If your change touches XR, avatars, networking, or input, retest these.

If you genuinely cannot test something — no Quest, no Mac, etc. — say so in the PR. Honesty is faster than a fake green tick.

## Style

- **`.editorconfig`** sets the formatting baseline. Most IDEs pick it up automatically. CSharpier is configured via `.csharpierignore`.
- **Comments are lean by default.** Comment when the *why* is non-obvious — workarounds, hidden invariants, performance reasons. Don't restate what the code already says.
- **Use the framework's own conventions** for new code: `BasisDebug`, `BasisLocalCameraDriver`, `BasisEventDriver`, `Try*` patterns, Addressables.
- **C# nullable annotations** aren't enforced project-wide; follow the surrounding file.

## Merge cadence

The maintainer triages and merges PRs. Some practical things to know:

- Bigger or higher-risk changes may sit until after a Unity LTS bump or a stable release window. That's not a rejection — it's normal.
- Some PRs get merged so the team can dogfood them for a week and decide afterwards whether they're better or worse. Don't be alarmed if a merge happens with "let's try it and see" attached.
- If a PR has been quiet for a while, a polite ping in Discord is fine.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](https://opensource.org/licenses/MIT), the same license that covers the rest of the project. See [LICENSE](./LICENSE) and [TRADEMARK.md](./TRADEMARK.md).

## Thanks

Basis exists because people show up and contribute. Whether that's a typo fix, a major feature, a bug report with a beautiful repro, or just helping someone in Discord — it all counts. Thanks for being here.
