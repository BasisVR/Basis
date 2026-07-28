# Basis Sound Pack

Optional sound-effect pack for the Basis framework. It supplies distinct sounds for
hover, press, grab (picking an object up), chat notify, and the camera countdown
tick, so different actions no longer share one clip. Mic mute/unmute and the camera
shutter deliberately keep the framework's original clips, and the menu makes no
sound. The clips are sourced from Kenney's CC0 "Interface Sounds" pack
(https://kenney.nl/assets/interface-sounds).

Two of these were outright reuse bugs before the pack: a chat message played the
button-press clip, and grabbing an object played the UI *hover* clip. The chat and
grab sounds are mixed deliberately soft so they invite rather than interrupt.

## How it works

The framework resolves each sound through `BasisUISounds.Resolve(event, legacy)`:

1. If this package is present, `Resources/BasisSoundPack.asset` is loaded and its
   clip for the event is used.
2. If the package is absent, or a clip is left unassigned, the framework falls
   back to its original built-in clip. Nothing breaks without this package.

## Replacing sounds

Every clip is an ordinary `.wav` in `Sounds/`. Swap in your own CC0/licensed
audio and reassign it on `Resources/BasisSoundPack.asset`.

## License

All audio in this package is derived from Kenney's CC0 "Interface Sounds" pack
and remains under CC0-1.0 (public domain). See `LICENSE.md`.
