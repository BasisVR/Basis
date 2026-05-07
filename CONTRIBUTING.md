# Contributing to Basis

Thanks for thinking about contributing! Basis is an MIT-licensed open-source social VR framework, and it only gets better when other people pile in. This document covers how to report bugs, request features, and get a pull request through review without too much back-and-forth.

If you only read one other thing before contributing, read [PHILOSOPHY.md](./PHILOSOPHY.md). It frames what Basis is trying to be, which makes design conversations a lot easier.

## Quick links

- [Discord](https://discord.gg/F35u3cUMqt) — the fastest way to ask questions, share work-in-progress, or check whether someone's already tackling something.
- [README.md](./README.md) — project overview, install steps, command-line flags.
- [PHILOSOPHY.md](./PHILOSOPHY.md) — what Basis is for and who it's aimed at.
- [STYLE.md](./STYLE.md) — formatting, the PR checklist explained, and what reviewers push back on. Read this before you open a PR.
- [CI.md](./CI.md) — what you need to set up if you're forking and want CI to build.
- [Pull request template](./.github/PULL_REQUEST_TEMPLATE.md) — the merge checklist.
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

## Building locally

The Unity client lives at `Basis/`. Open *that* folder in Unity Hub, not the repo root.

1. **Install Unity 6.** The exact editor version is in `Basis/ProjectSettings/ProjectVersion.txt`; Unity Hub will prompt you to install it if you open the project without it.
2. **Clone the repo:**
   ```sh
   git clone https://github.com/BasisVR/Basis.git
   ```
3. **Open `Basis/`** in Unity Hub. First import takes a while — the project pulls a lot of packages.
4. **Load the boot scene** at `Packages/com.basis.framework/Scenes/initialization.unity`. This is the entry point for both editor play mode and player builds; loading any other scene first will misbehave.
5. **Press play.** For a player build, use **File → Build Settings** with `initialization.unity` as the only enabled scene.

Useful command-line flags for built players (also documented in [README.md](./README.md)):

```
--disable-OpenVRLoader   --disable-OpenXRLoader   # boot in desktop mode
--force-OpenXRLoader     --force-OpenVRLoader     # force a specific VR loader
```

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

The [pull request template](./.github/PULL_REQUEST_TEMPLATE.md) has a checklist that **must** be ticked before merge. The headline items cover hot-path allocations, transform batching, Addressables, `BasisEventDriver`, logging, and platform/input coverage — read [STYLE.md](./STYLE.md) for what each item means and the recurring review themes that aren't on the checklist.

If a box is genuinely N/A, tick it and explain why under **Notes**.

### Tone in PRs and review

Reviews on Basis are friendly and direct. If a reviewer pushes back on a design choice and points at existing surface, the path of least resistance — and usually the right one — is to rewrite to use it. Save the architectural debate for cases where you have concrete evidence the existing surface won't work. PRs that argue elegance over getting merged tend to stall.

If you're the reviewer, lead with what's good before what needs work, and link to the existing surface or call site you're pointing at. "There's a callback for this on `BasisXRManagement` already" is more useful than "you don't need this."

## Merge cadence

The maintainer triages and merges PRs. Some practical things to know:

- Bigger or higher-risk changes may sit until after a Unity LTS bump or a stable release window. That's not a rejection — it's normal.
- Some PRs get merged so the team can dogfood them for a week and decide afterwards whether they're better or worse. Don't be alarmed if a merge happens with "let's try it and see" attached.
- If a PR has been quiet for a while, a polite ping in Discord is fine.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](https://opensource.org/licenses/MIT), the same license that covers the rest of the project. See [LICENSE](./LICENSE) and [TRADEMARK.md](./TRADEMARK.md).

## Thanks

Basis exists because people show up and contribute. Whether that's a typo fix, a major feature, a bug report with a beautiful repro, or just helping someone in Discord — it all counts. Thanks for being here.
