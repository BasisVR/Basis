# Security Policy

Basis accepts data from parties it cannot vouch for: peers in a session, avatars and worlds
loaded at runtime, media streams, and whatever a self-hosted server relays. Security research
against that surface is valuable to us, and we would rather hear about a problem early than read
about it later.

## Reporting a vulnerability

**Please don't open a public issue, and please don't post details in Discord.**

Two private channels, either is fine:

- **GitHub private vulnerability reporting.** Use the *Report a vulnerability* button under the
  [Security tab](https://github.com/BasisVR/Basis/security/advisories/new). This is the preferred
  route, since it keeps the report, the discussion and the resulting advisory in one place.
- **Email `developerbasis@gmail.com`** if you would rather not use GitHub, or if the report needs
  attachments GitHub won't take. If you need to send material encrypted, say so in your first
  message and we'll arrange a channel.

### What to include

The more of the following you can provide, the faster a fix can land:

- What the issue is, and what an attacker gains from it.
- Steps to reproduce, or a proof-of-concept. A short clip or a packet capture is often clearer
  than prose.
- Which component is affected: Unity client, headless/network server, SDK, or a specific package
  under `Basis/Packages/`.
- The commit or release tag you tested against, plus platform and headset where relevant.
- Anything you already know about mitigations or a suggested fix.

Partial findings are welcome. If you have identified a suspicious code path but haven't driven it
to a crash, send it with that caveat attached; a file and line reference still gives us somewhere
to start.

### What happens next

We aim to acknowledge a report within a few working days, and to follow up with an initial
assessment and a rough timeline once we have reproduced it. If you have had no response after a
week, please chase us — a message in Discord asking for a status update, with no detail attached,
is fine.

We will tell you when the fix is merged so you can confirm that it addresses the finding, and
we'll say so if our assessment of severity differs from yours, along with the reasoning.

## Scope

Anything this repository ships is in scope:

- **The Unity client** (`Basis/`) and the packages under `Basis/Packages/` that we maintain.
- **The network server** (`Basis Server/`), including the REST API, the headless build and the
  Docker images.
- **The SDK and content pipeline**, covering avatar and world bundles, the loading path and the
  scripting sandbox.
- **The media stack**, from remote URL handling through demuxing and decoding to the native
  plugins.
- **The networking protocol itself**: message handling, authentication, ownership and moderation
  controls.

A few points are worth stating outright, since they cover the cases reporters most often
self-reject:

- **A malicious peer is part of the threat model.** "This needs a modified client" is not a
  disqualifier. Anyone can run one, and much of why the server and the client's own validation
  exist is to cope with that. Anything a peer can do to another user's session, or to a server,
  counts.
- **Malicious content is in scope too.** Avatars and worlds arrive from parties the user has no
  reason to trust. Crashing, hanging or escaping the sandbox on the machine of someone who merely
  looked at an avatar is a security issue, not a content-quality one.
- **So is a server relaying something it shouldn't.** Anything that lets one user reach past an
  instance's moderation or permission controls belongs here.

### Generally out of scope

Not an absolute list, and a strong write-up can move something off it, but as a rule these aren't
tracked as vulnerabilities:

- Findings in third-party packages we vendor rather than write. Those are best reported upstream
  first; do tell us as well, so we can pull the fix through or pin around it.
- Deployments where an operator has deliberately turned protections off, or exposed a headless
  server with authentication disabled.
- Scanner output with no working reproduction against a current build.
- Attacks needing physical access to a machine, or an account already trusted on that machine.
- Social engineering of maintainers or community members.
- Missing hardening on community-run infrastructure that isn't part of this repository.

## Supported versions

Basis ships as a rolling weekly package release (`vpm-YYYY-Wnn.n`) cut from the `developer`
branch, and that is where fixes land. We do not currently back-port security fixes to older tags.
If you run Basis in production, whether that is a public instance or a build you distribute,
tracking recent releases is the practical way to stay patched; if you maintain a long-lived fork,
watching `developer` for security commits is worth doing alongside it.

## Coordinated disclosure

We would ask that you hold off publishing until a fix is available and operators have had a
reasonable window to deploy it. Ninety days from the initial report is a sensible default. If a
fix looks like taking longer than that, we would rather agree a revised timeline with you than
leave the report open indefinitely.

When a fix ships we will publish a GitHub Security Advisory. We are happy to credit you under
whatever name or handle you prefer, or to omit attribution entirely.

## Testing responsibly

Please test against your own instance. The server runs locally and in Docker, so standing one up
for research is straightforward, and it keeps other people's sessions out of the work.

We would ask you not to test against community or third-party instances without the operator's
permission, not to access or exfiltrate data belonging to others, and to avoid anything that
would degrade a live session for the people in it. Research conducted within those lines is
welcome, and we will not pursue anyone acting in good faith.

## Running an instance

If you host Basis, the controls under `Basis Server/BasisNetworkServer/Security/` (allow lists,
ban lists, permissions, resource limits) are the ones to review before opening an instance to the
public. Configuration questions are welcome in [Discord](https://discord.gg/F35u3cUMqt); that is
not a disclosure channel, but it is a good place for deployment advice.
