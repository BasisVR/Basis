# AI Disclosure

Basis is developed with help from AI tools, and we'd rather say so plainly than have anyone guess. This document covers how AI is used to build Basis, what actually ships in the product, and what we ask of contributors who use AI tools themselves.

## How Basis is developed

AI coding assistants (LLM-based tools such as Claude Code) are used in Basis development for code only: implementation, refactoring, tests, debugging, and developer docs. AI is not used for creative content: the art, imagery, audio, and 3D assets in Basis are made by humans.

As a concrete example, AI was used to write many of the automated tests in Basis. Without AI assistance, a human alone would have taken months; with AI assistance, far more of the codebase is covered by tests than would have otherwise been practical.

Unmoved by that assistance is the human element of direction, review, and responsibility. Changes land because contributors and maintainers decide they should, review them, and stand behind them, and the quality bar is the same for every change regardless of how it was written. We don't tag individual commits or files as AI-assisted; this document is the project-level disclosure instead.

## What ships in Basis

**Basis does not embed generative AI.** The client and server do not invoke LLMs or other generative AI services, and Basis does not send user data (voice, motion, text, or anything else) to AI services or collect it to train AI models.

This is true today as well as a future-facing commitment: **we will never include generative image or video AI in Basis.**

Basis does ship small machine-learning models for classic signal-processing jobs, running entirely on your device, although these are very different from LLMs or generative AI.

- **RNNoise**: neural noise suppression on your own microphone input.
- **OpenLipSync**: lip-sync viseme inference from your own voice, via ONNX Runtime.
- **MediaPipe**: optional webcam-based tracking, only if you enable it.

These run locally and upload nothing.

A subtle point that may be missed: although we stand in principle against the automated production of creative content, the above commitment may be interpreted to allow machine translation as an assistive tool for communication, and depending on implementation these may involve LLMs. However, such functionality is essentially mechanical, not creative, in nature, and facilitates human-to-human communication.

## AI-assisted contributions

AI-assisted contributions are permitted, and the same requirements as [CONTRIBUTING.md](./CONTRIBUTING.md) apply. There are, however, some additional considerations to keep in mind for AI use.

- **You are the author.** Understand what you submit well enough to explain and defend it in review. "The AI wrote it" is not an answer to a review question.
- **Code within your limits.** Submitting code that you couldn't have written by hand given enough time is an easy, but dangerous shortcut. Do not use AI as a crutch to replace your understanding of what your code is doing. You need to be firmly at the wheel.
- **Review your own output before asking us to.** Don't open a PR whose contents you haven't read yourself; unreviewed AI output is easy to spot and wastes reviewer time.
- **Documentation also requires maintenance.** Submitting an intense amount of prose subjects the project to an intense amount of pressure to review, correct, and maintain it. In practice, this requires a lot of human oversight to prevent it from becoming stale, in addition to any tokens spent to create it, and context bloat from ingesting it. Contributing documentation must be done in a way that is intelligible to humans, and maintainable on a reasonable scale. Endless walls of AI-generated text are not a net positive for the project.
- **Keep reports real**. Whoever publishes a bug report must own and vouch for its quality and authenticity: AI may be used to write up bug reports or feature requests, but logs, reproduction steps, and observed behavior must be genuine, never invented or "reconstructed".
- **No AI-generated Assets.** Don't submit AI-generated art, images, video, audio, or 3D assets. Pay an artist for their time or use existing free assets. Assets that are included in the Basis repository must be permissively licensed and unencumbered by NC or ND clauses.

If a substantial part of a PR is AI-generated, a brief mention under **Notes** in the PR template is appreciated. It's not required, and it won't count against you.
