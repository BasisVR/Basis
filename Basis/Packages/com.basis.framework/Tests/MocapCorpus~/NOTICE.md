# Mocap corpus — third-party data

Ground truth for `BasisMocapAccuracyTests` (is the solved pose RIGHT?) and `BasisMocapMotionQualityTests`
(is the solved MOTION right?). Not shipped in player builds and not imported by Unity: the trailing `~` on
the folder name keeps it out of the asset database entirely.

**Licensing is recorded in `Packages/com.basis.framework/THIRD_PARTY_NOTICES.md`** — the package-root file
the rest of the repo uses for this. The short version is repeated at the bottom of this file so nobody has
to go looking, but the notices file is the authority.

## CMU Graphics Lab Motion Capture Database

- Source: http://mocap.cs.cmu.edu/
- BVH conversion: Bruce Hahne (cgspeed), via https://github.com/una-dinosauria/cmu-mocap
- Files: `02_01.bvh` (general motion), `07_01.bvh` (walk), `09_01.bvh` (run)
- Files, added: a filename is the CMU `subject_trial` — `141_20.bvh` is subject 141, trial 20, and the CMU
  index describes every trial. Picked for the poses a VR user actually holds, idle first:
  - `77_02.bvh` (standing still), `113_21.bvh` (standing still, long quiet hold), `141_20.bvh` (waiting —
    restless idle: weight shifts and small steps in place, no net travel)
  - `141_15.bvh` (range of motion — a full sweep of every joint through its limits)
  - `143_11.bvh` (walk up, bend, pick up box), `143_26.bvh` (washing a window — sustained arm reach),
    `26_09.bvh` (bend over, pick up), `69_70.bvh` (walk to object, squat, pick up, set down)
  - `69_16.bvh` (turn in place), `77_05.bvh` (look around with a flashlight)
  - `07_04.bvh` (slow walk), `132_18.bvh` (fast walk), `16_11.bvh` (walk, veering left — curved path)
  - `143_18.bvh` (sit down and get up), `141_17.bvh` (sit on a stool)
  - `143_25.bvh` (waving)
  - `143_17.bvh` (walk up stairs and step over)

## License

CMU places no restrictions on the use of this data, including commercial use. From the database's own terms:

> This data is free for use in research and commercial projects worldwide. You may include this data in
> commercially-released products.

No attribution is required. The database was created with funding from **NSF EIA-0196217**, and CMU asks
that work using it carry this acknowledgement, which we do:

> The data used in this project was obtained from mocap.cs.cmu.edu. The database was created with funding
> from NSF EIA-0196217.

The cgspeed BVH conversion adds no restrictions of its own — it is a reformatting of the same data.

## ⚠️ Fidelity limits — read before trusting a number that comes out of here

The corpus is REAL HUMAN MOTION, but it is not a VR user, and two differences have already produced
misleading measurements:

1. **A mocap hand is not a controller.** These clips carry a hand-BONE rotation from a marker-fitted
   skeleton. A VR controller's rotation is a GRIP convention. Anything in the IK that reads the hand's
   *rotation* (the chicken-wing elbow flare does) is therefore being fed a convention it was not designed
   for, and a result that hinges on it must be confirmed in a headset before it is believed. This is not
   hypothetical: measured against this corpus the flare engages ~0.5 on average and 0.89 while STANDING
   STILL, which is either a real bug or exactly this artefact — and the corpus alone cannot tell you which.

2. **The mocap has its own noise floor**, ~0.2% of limb length above 8 Hz (about 1.2 mm on an adult arm),
   which is the optical rig, not the human. Any jitter metric taken from here is an EXCESS over that floor,
   never an absolute. Differentiating the raw signal measures the rig; low-pass first
   (`BasisMotionSignal.LowPass`) or the number is fiction.

What the corpus IS unimpeachable for: joint POSITIONS and their time-derivatives. Those are anatomy, and
they transfer.

## `posture/` — the pelvis corpus (44 clips)

A SECOND corpus, in a subfolder, and the subfolder is the point: the tests above use a non-recursive
`Directory.GetFiles`, so these clips are invisible to them and **the arm/knee numbers quoted all over this
project stay byte-identical**. Mixing them in would have silently re-based every measurement in the IK suite
to buy nothing — the arm and the knee do not care what the pelvis is doing.

Used by `BasisPostureCorpusTests`, `BasisPelvisPostureModelTests` and `BasisSpineBendOverGroundTruthTests` to
fit and validate `BasisPelvisPostureModel` — where the pelvis goes when a VR user's head gets low.

Chosen from the CMU index BY DESCRIPTION to span the one axis that matters, because a low head is **two**
different bodies and the old rig could not tell them apart:

- **SQUAT / SIT — the pelvis rides the head down** (measured coupling 0.78–0.99)
  `13_29` (squats), `75_17`/`75_18`/`75_19`/`75_20` (sits, graded by seat height — the cleanest signal in
  the database), `13_01`/`13_04`/`13_05`, `14_27`/`14_29`/`14_31`, `15_10`, `74_07`/`74_08` (lifting),
  `139_16` (getting up off the ground), `77_09` (ducking)
- **WAIST-BEND — the pelvis stays high and the spine folds** (measured coupling 0.02–0.14)
  `64_26`…`64_30` (picking a ball off the floor — the purest waist-bends here), `13_23`/`14_16` (sweeping),
  `14_13` (mopping), `26_10`/`26_11` (bend + lift), `02_06`, `80_08`, `77_06`/`77_07`/`77_08`
- **UPRIGHT ANCHORS — and these are the most important files in the folder**
  `08_01`, `08_02`, `12_02`, `17_01`, `35_01`, `49_02`, `56_07`. Without clips of a human doing *nothing*,
  a fit has never seen "standing" and will cheerfully drop the pelvis of a user who is simply stood there.
  (The model is also fitted *through the origin* for the same reason, so zero head drop gives exactly zero
  pelvis drop as an algebraic identity rather than as a hope. Belt and braces: this failure would be worse
  than the bug it fixes.)

Same CMU source, same conversion, same licence as above — the licence covers the whole database, not a
selection from it.

### ⚠ One fidelity limit specific to this corpus

`82_05` (sitting on the ground) and `139_16` (getting up off the ground) reach poses where the "support
base" — the midpoint of the feet — stops meaning anything, because the legs are stretched out in front and
the feet are nowhere near under the body. Those frames sit outside the model's fit domain
(`BasisPelvisPostureModel.MaxDrop` / `MaxLean`) and the runtime clamps into it, so they inform the fit at
their edges without poisoning it. A standing VR user cannot reach them.

## `dynamic/` — the arms-up / dynamic corpus (29 clips)

A THIRD corpus, same subfolder trick as `posture/`: the accuracy tests use a non-recursive `Directory.GetFiles`,
so these clips are invisible to them and **every arm/knee number quoted in this project stays byte-identical**.

The root corpus is curated for "the poses a VR user actually holds" — idle-first, walking, pick-ups — and is
deliberately LIGHT on arms overhead, full extension, and fast motion. That is exactly the regime that exercises
the arm's full-extension cap, the elbow-anatomy ceiling, and the pole-flip handling, so this corpus supplies it.
Used by `BasisDynamicCorpusTests`. Chosen from the CMU index BY DESCRIPTION (and screened so every clip passes
the anatomical `Validate`, which rules out cartwheels / handstands / floor work — they invert the skeleton):

- **MODERN DANCE — arms overhead, full-extension reaches, back-bends, spins**
  `05_02` (expressive arms, pirouette), `05_10`/`05_12`/`05_20` (arabesque, arms held high, bending back),
  `49_09` (arms held overhead — the elbow is ABOVE the shoulder on **82%** of frames, the anatomy-guard stress
  test the root corpus never had), `49_12` (arching arms — elbow-above-shoulder 73%)
- **SOCIAL / STYLE DANCE** `60_01`/`60_05`/`60_12` (salsa), `93_02` (a range-of-motion sweep),
  `93_03`/`93_08` (Charleston), `94_01`/`94_03` (Indian dance), `20_01` (chicken dance — elbows out and up)
- **THROWS & BALL SPORTS — overhead reaches and fast swings** `33_02` (football throw/catch — overhead 26%),
  `06_14` (basketball dribble + shot — overhead 18%)
- **CALISTHENICS / ROM** `42_01` (stretch: rotate head, shoulders, arms, legs through their limits),
  `14_06`/`14_20` (jumping jacks, reach up, stretches), `13_30` (jumping jacks, side twists), `118_01` (jumps)
- **MARTIAL ARTS / COMBAT — extended, fast arms** `14_01`/`13_17` (boxing), `135_01` (Bassai kata),
  `135_04` (front kick), `144_07` (blocks), `02_05` (punch/strike), `02_07` (swordplay)

Why these and not the whole database: they maximise arms-up / full-extension / fast-swing content per clip
(measured — hand-above-shoulder up to 82% of frames, reach>0.97 up to 60%), which is what the walking corpus
lacks. Two very long clips (`12_04` tai-chi 148 s, `15_04` 188 s) were screened out to keep the test snappy;
add them back the same way if you want them. Same CMU source, conversion and licence as above.

## Adding more clips

Drop any `.bvh` into this folder and the tests pick it up automatically. The loader expects the standard
Biovision hierarchy with CMU/Biovision joint names (`LeftArm`, `LeftForeArm`, `LeftUpLeg`, …); see the name map
in `BasisBvhLoader`. Every clip is checked for anatomical sanity (left hand on the left, knees bending forward)
before any measurement is taken from it, so a bad file fails loudly rather than quietly skewing the numbers.
