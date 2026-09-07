# Third-Party Notices

This package (`com.basis.framework`, MIT) redistributes the third-party data below. Everything
listed here is **test-only** and is **not shipped in player builds**.

## CMU Graphics Lab Motion Capture Database

- Project: http://mocap.cs.cmu.edu/
- Redistributed in: `Tests/MocapCorpus~/*.bvh` (20 clips, ~10 MB)
- License: **unrestricted, including commercial use** (see terms below)

Human motion-capture recordings used as ground truth by `BasisMocapAccuracyTests` and
`BasisMocapMotionQualityTests` — the corpus is what lets those suites ask "does the solver put the
elbow where a *real human's* elbow actually was, and did it get there the way a human would",
rather than merely "is this pose self-consistent".

The database's own terms, verbatim:

> This data is free for use in research and commercial projects worldwide. You may include this
> data in commercially-released products.

No attribution is required. It is given anyway. The database was created with funding from
**NSF EIA-0196217**, and CMU asks that work using it include the following acknowledgement, which
we reproduce here:

> The data used in this project was obtained from mocap.cs.cmu.edu. The database was created with
> funding from NSF EIA-0196217.

### BVH conversion

The original CMU release is ASF/AMC. The `.bvh` files here are the widely-used conversion by
**Bruce Hahne (cgspeed)**, retrieved from the mirror at https://github.com/una-dinosauria/cmu-mocap.
The conversion carries no additional restrictions beyond CMU's; it is a reformatting of the same
data. Joint naming follows that conversion (`LeftArm`, `LeftForeArm`, `LeftUpLeg`, …), which is
what `BasisBvhLoader`'s name map expects.

### Why the folder is named `MocapCorpus~`

The trailing `~` keeps Unity from importing the folder at all: no `.meta` files are generated, the
`.bvh` files never enter the asset database, and nothing can accidentally end up in a build. The
data is read from disk by the editor-only test code and nowhere else.

## Not redistributed

Nothing else in this package vendors third-party code or data. Optional integrations that pull in
third-party dependencies carry their own `THIRD_PARTY_NOTICES.md` in their own package
(`com.basis.mediapipe`, `com.basis.integration.slimevr`).
