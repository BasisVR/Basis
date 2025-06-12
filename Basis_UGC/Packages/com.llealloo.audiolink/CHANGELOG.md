# Changelog

## 2.0.0 - September 8th, 2024
### New features
- Added the ability to adjust how the AudioLink controller is synced. You can sync every part of it, none of it, or everything except the gain and power controls. (fundale)
- Added support for dual mono audio sources, for cases where you want to supply the left and right channel from separate sources. (fundale)
- Added a utility script for driving blend shapes with AudioLink - AudioReactiveBlendshapes. Just add the script to a GameObject that has a SkinnedMeshRenderer to use. (fundale)
- Added rudimentary support for using AudioLink with the WebGL build target. (fundale, hill)

### Changes
- Lowered the default volume for the AudioLink avatar prefab a bit. (pema)
- Reduces the network traffic incurred by syncing AudioLink state a bit. (Happyrobot33)
- Deprecated `ThemeColorController.customThemeColors` as the behavior has changed. Please use `ThemeColorController.SetCustomThemeColors` and `ThemeColorController.GetCustomThemeColors` instead. This is a (minor) breaking change.

### Bugfixes
- Fixed a bug where the color chord theme color toggle on the controller wasn't properly synced. (pema)

## 1.4.0 - May 10th, 2024
### New features
- Added support for exposing a global video texture (_Udon_VideoTex) via the ytdlpPlayer avatar testing prefab. This functionality intended for testing avatar shaders that use a global video texture. The global video texture is **not** something provided by AudioLink outside of in-editor testing - this must be done by a video player. (techanon)

### Changes
- We've changed the default song in the example scene, and the avatar testing prefab, to ["Shibuya"](https://www.youtube.com/watch?v=vGXyAKy-X6s) by [Rollthered](https://linktr.ee/Rollthered). We remain forever thankful to [Lamp](https://twitter.com/LampDX) for letting us previously use their unreleased track, "Sludge Bath".
- Reduced the sizes of various icon textures used by AudioLink. (Teeh)
- Changed the controller to use MSDF-based textures for the icons. (Vistanz)
- Improved the logic that searches for a yt-dlp excecutable in ytdlpPlayer avatar testing prefab. It should more consistently find the executable accross platforms now. Additionally, the ability to override which executable is used has been added under `Tools > AudioLink > Select Custom YTDL Location`. (techanon)
- The "Upgrade AudioLink compatible shaders" popup will no longer display on first import, as shader authors have had plenty of time to upgrade their shaders at this point. It can still be run manually via the `Tools > AudioLink > Update AudioLink Compatible Shaders` menu item. (pema)

### Bugfixes
- Fixed an issue where the AudioLink texture was flipped when using OpenGL. (fundale)
- Fixed an issue where the ytdlpPlayer avatar testing prefab was selecting an incompatible format at certain resolution settings, resulting in the video failing to play. (pema)
- Fixed an issue where the ytdlpPlayer avatar testing prefab restarted the video about ~3 seconds in. (techanon)
- Fixed an issue where the AudioLink controller was z-fighting on mobile platforms. (pema)
- Fixed issue #299, where modifying the settings on the AudioLink prefab wouldn't properly apply to the control. (pema)

## 1.3.0 - February 18th, 2024
### Changes
- Deprecated various static properties in AudioLink.DataAPI in favor of static functions to work around a miscompilation bug in UdonSharp.
- Deprecated the underused "AudioLink extra packages", which contains a single "AudioLinkZone" script. The script is now in the main package.

### Bugfixes
- Fixed an issue where an exception would be thrown when leaving a world with AudioLink enabled. (@ShingenPizza)
- Fixed a bug where theme colors would reset when someone joins the instance. (Teeh, orels1, pema)

## 1.2.1 - December 9th, 2023
### Changes
- Enabled Async GPU readbacks on mobile platforms, including Quest. This let's you access data via Udon much more cheaply. To facilitate this, AudioLink now requires Unity 2022.3 or newer. (pema)
- Moved the AudioLink menu in the top menu bar into the "Tools" submenu, to bring it in line with other similar packages. (techanon)
- Updated the "Add AudioLink Prefab to Scene" button to check for the existence of a prefab before adding a new one. (techanon)
- Made the controller call `AudioLink.AudioLinkEnable` and `AudioLink.AudioLinkDisable` instead of toggling the AudioLink GameObject. (Nestorboy)

### Bugfixes
- Fixed normals being incorrectly flipped on the logo of the AudioLink controller. (llealloo)
- Fixed a bug where shader ID's used by AudioLink weren't properly initialized in some cases. (Nestorboy)
- Updated the shader used for the AudioLink controller logo to support single pass stereo instanced rendering. (Nestorboy)
- Fix an issue where shader ID's were initialized too late when AudioLink API was used too early in the script execution cycle. (pema)

## 1.2.0 - October 9th, 2023
### Changes
- **Important for shader developers:** Deprecated AudioLinkGetVersion() and replaced it with AudioLinkGetVersionMajor() and AudioLinkGetVersionMinor(). For this current release, major will read 1.0f, and minor will read 2.0f. Unfortunately, the recent 1.0.0 and 1.1.0 versions will be detected as 0 on the major version due to an oversight. These should be the only versions in existence with an incorrect version number. For all other versions, these 2 functions are backwards compatible. Any shaders going forward should use these 2 functions, or read directly from green (major version) and alpha (minor version) of the pixel at `ALPASS_GENERALVU`.

## 1.1.0 - October 8th, 2023
### Changes
- Made the logo on the new AudioLink controller audio reactive.
- Changed the recently added [C# Data API](https://github.com/llealloo/vrc-udon-audio-link/blob/master/Packages/com.llealloo.audiolink/Runtime/Scripts/AudioLink.DataAPI.cs) to not use Vector2Int. This type does not work correctly in UdonSharp.

## 1.0.0 - September 29th, 2023
### New features
- Added a new AudioLink Controller, with a completely revamped design. The old controller is still included, if you don't want to change. (Thanks to everyone who helped, including Pema, Lea, Teeh, Sacred, TechAnon and more)
- Added automatic gain adjustment. It's enabled by default, but you can disable it on the AudioLink controller. (Thanks, cnlohr)
- Added Async GPU Readback, you can toggle it on the AudioLink prefab. This means you can read data from AudioLink in Udon, without having to pay a heavy performance penalty! Note that this feature only works on PC. Quest (and other mobile platforms) will continue to use the old slow synchronous readbacks, which we advise against.
- Added [a new C# API](https://github.com/llealloo/vrc-udon-audio-link/blob/master/Packages/com.llealloo.audiolink/Runtime/Scripts/AudioLink.DataAPI.cs) for reading audio data in Udon, to better accomodate the new Async Readbacks.
- Added Media Playback States, which provide information about the currently playing track. ([See Docs](https://github.com/llealloo/vrc-udon-audio-link/tree/master/Docs)). (Thanks, fundale)
- Added Media States Udon API [VideoPlayer API](https://github.com/llealloo/vrc-udon-audio-link/tree/master/Docs/PlayerAPI.md), to control the aforementioned Playback States.  (Thanks, fundale)
- Added an AudioLink prefab for ChilloutVR.
### Changes
- We have switched to using [Semantic Versioning](https://semver.org/), marking this release as 1.0.0. This may break shaders written for very old versions of AudioLink. The version will now read as `1.00f` to AudioLink shaders.
- The "VRCAudioLink" namespace has been renamed to "AudioLink". This is a breaking change. We've added a new scripting define, `AUDIOLINK_V1`, which will be present for future versions of AudioLink, to aid with backwards compatability.
- Removed the dependency on the VPM package resolver.
### Improvements
- Made yt-dlp integration resolve URLs asynchronously.
- Improved the yt-dlp integration UI in a number of ways. (Thanks, rRazgriz)
- Improved the "Add Audiolink Prefab to Scene" button.
- Improved logging, with less meaningless warning spam.
- Cache the local player in AudioLink scripts for slightly improved performance. (Thanks, Nessie)
- Fixed null reference exceptions being fired when a player leaves the world. (Thanks, Nessie)
- Fixed an issue where theme colors would reset to the defaults on world start (https://github.com/llealloo/vrc-udon-audio-link/issues/281).

## 0.3.2 - March 12th, 2023
### New features
- Added integration with ytdlp for easy testing of AudioLink shaders in avatar projects. The AudioLinkAvatar prefab now has an UI that lets you paste in a YouTube link, the audio of which will be used to drive AudioLink. (Thanks, rRazgriz)
- A global shader keyword, "AUDIOLINK_IMPORTED" will now be set automatically when AudioLink is imported.
- Added a button for quickly adding AudioLink prefabs to the current scene. Available under "AudioLink -> Add AudioLink Prefab to Scene".
- Added the ability to get network time from the AudioLinkTime node
- Added methods to enable/disable the AL texture through the AL behaviour and also made it do that when you enable/disable the GO itself
### Changes
- Add FAQ.md containing frequently asked questions and answers.
- Add issue templates. Submit issues with them [here](https://github.com/llealloo/vrc-udon-audio-link/issues/new/choose).
### Bugfixes
- Ask User to save current scenes before opening the example scene
- Fix some broken amplify nodes

## 0.3.1 - November 26th, 2022
### Changes
- Updated the AudioLink package to work in both world and avatar projects, by removing the dependency on UdonSharp. The first time you attempt to import AudioLink into a world project, a popup will appear prompting you to install UdonSharp.

## 0.3.0 - November 11th, 2022
### New features
- AudioLink is now a curated package! As such, it can be installed with [VRChat Creator Companion](https://vcc.docs.vrchat.com/). This is new the recommended way to install AudioLink. ~~For avatar projects and non-VRChat use cases, there is a regular old UnityPackage you can use.~~ Please see the readme for [a guide on how to update your projects](/README.md#updating-projects-from-version-28-or-lower-first-time-setup-please-see-next-section). We recommend you follow this guide, as the update is a breaking change.
- Since the folder structure has changed, custom shaders may need to be upgraded. A tool has been included to automatically upgrade shaders where needed. This tool is accessible from "AudioLink -> Update AudioLink compatible shaders" in the top menu of the editor. (Thanks, 3)
- Added Global Strings - text which can be read from AudioLink compatible shaders. Use this to retrieve the name of the local player and master in a shader, or to feed custom string data through. Info on usage is in Documentation. See `AudioLinkGetGlobalStringChar(uint stringIndex, uint charIndex)` in AudioLink.cginc for details.
- Included Amplify nodes for using AudioLink have been overhauled. They are now in their own "AudioLink" category, and handle including the relevant files automatically. Also, links to documentation are added. (Thanks, Nestorboy)
- Added a function, `AudioLinkGetAudioSourcePosition()`, for getting the worldspace position of the audio source being used to feed AudioLink. (Thanks, 3)
- Various shaders have been updated to supported Single Pass Stereo Instanced rendering, for future compatability. (Thanks, 3)
- The included video player, AudioLinkMiniPlayer, has been updated to support LTCGI. (Thanks, Texelsaur)
- The example AudioLink scene can now be quickly accessed using the "AudioLink -> Open AudioLink Example Scene" button in the top menu of the editor.
- The live AudioLink demo world has gotten an overhaul, and has a bunch of new cool things to play with.
### Changes
- The GrabPass utilized by AudioLink has been removed! We use the new `VRCShader.SetGlobalTexture` to expose a globally available texture now. This is an improvement to stability and performance. Existing shaders should still be compatible with new versions of AudioLink.
- The camera used by AudioLink to provide audio data to udon (using the Experimental Audio Readback feature) will now be disabled when it isn't needed, which should improve performance a bit.
- When using an AudioLinkController, the values selected in the AudioLink inspector are now respected, instead of the controller overriding them on initialization. (Thanks, 3)
- Be consistent with swizzling in AudioLink shaders, use 'xyzw' everywhere.
- Removed uses of SetProgramVariable and SendCustomEvent throughout AudioLink code. (Thanks, 3)
- Use VRCShader.PropertyToID instead of strings for better performance. (Thanks, 3)
### Bugfixes
- Issue [191](https://github.com/llealloo/vrc-udon-audio-link/issues/191) and [186](https://github.com/llealloo/vrc-udon-audio-link/issues/186) have been fixed. (Thanks, 3)
- Remove some old profiling code that could have a very slight impact on performance. (Thanks, 3)
- Fix the "Link all sound reactive objects to this AudioLink" button, which was broken when using UdonSharp 1.x. (Thanks, 3)
- Fix some minor inconsistencies in documentation.

## 0.2.8 - May 14th, 2022
### New features
- AudioLink theme colors are now configurable via the AudioLink controller with a slick color-picker GUI. (Thanks, DomNomNom)
- Added the ability to get the time since Unix epoch in days, and milliseconds since 12:00 AM UTC. Additionally, a helper function, `AudioLinkGetTimeOfDay()` has been added, which lets you easily get the current hours, minutes and seconds of the time of day in UTC.
- An editor scripting define, `AUDIOLINK`, which will be automatically added when AudioLink is included. (Thanks, Float3)
- AudioLink can now compile without VRCSDK and UDON, for use outside of VRChat. This kind of usecase is still experimental at best, though. (Thanks, Float3 and yewnyx)
- Added a few new helper methods to sample various notes of the DFT. (Thanks, Float3)
### Changes
- AudioLink theme colors have been cleaned up, including a new demo in the example scene, and the ability to change the colors in realtime in the editor. (Thanks, DomNomNom)
- Changed a few default settings on the AudioLink controller to be more responsive. (Thanks, DomNomNom)
- Changed folder structure to put less clutter into user projects.
### Bugfixes
- Fix vertical UV flip of the AudioLink texture on Quest. (Thanks, Shadowriver)
- Fix error when using "Link all sound reactive objects to this AudioLink" button. (Thanks, Nestorboy)
- Add a header guard to `AudioLink.cginc` to prevent duplicate includes. (Thanks, PiMaker)
- Fix various warnings in shader code. (Thanks, Float3)
- Fix NaN-propagation issue in the included video player. (Thanks, Texelsaur)
- Add a player validity check to the included video player. (Thanks, Texelsaur)
- Use `Networking.LocalPlayer.isInstanceOwner` instead of `Networking.IsInstanceOwner`, which is broken. (Thanks, techanon)
- The logos on the AudioLink controller were using point filtering, which was changed to bilinear. (Thanks, DomNomNom)

## 0.2.7 - December 1st, 2021
### New features
- Make AudioLink framerate-invariant, instead of assuming a specific framerate. Features that rely on timing have been updated to reflect this change.
- Add helper functions `AudioLinkGetChronoTime`, `AudioLinkGetChronoTimeNormalized`, `AudioLinkGetChronoTimeInterval` to more easily sample chronotensity values. `AudioLinkGetChronoTime(index, band)` functions as a more-or-less drop-in replacement for `_Time.y`.
- Move `ALPASS_CCCOLORS` section from `(24,22)` to `(25,22)` to avoid confusion. Code that uses the define should continue to work fine.
### Bugfixes
- Fix a nasty bug where mirrors would sometimes causing AudioLink to stop functioning when observed from specific angles.
- Fix erroneous timing code for filtered VU and ColorChord.
- Fix some issues in the documentation.
- Version number was wrong last release. It is fixed now.

## 0.2.6 - August 10th, 2021
### New features (big thanks to @cnlohr and @pema99)
- Chronotensity feature provides timing information to shaders which changes in reaction to audio
- ColorChord index colors, a new way to get audio reactive colors from ColorChord
- Globally configurable theme colors
- Filtered VU, smoothly filtered versions of VU data
- Amplify nodes and example shaders for above features
- Added `AudioLinkGetAmplitudeAtFrequency` and `AudioLinkGetAmplitudeAtNote` functions for easily sampling specific parts of the audio spectrum corresponding to certain frequencies or semitones
### Changes
- UnU sliders (thanks Texelsaur)
- Various improvements to included video player, now with a resync button (thanks again, Texelsaur)
- Recursive / nesting support for AudioReactiveSurfaceArray prefab
### Bugfixes
- Fixed certain parts of filtered 4band data always being zero (thanks DomNomNom)

## 0.2.5 - June 7th, 2021
### Breaking changes
#### AudioLink.cginc
- Renamed `AudioLinkAvailableNonSurface` to `AudioLinkAvailable` - It supports surface shaders now
- Renamed `ETOTALBINS` to `AUDIOLINK_ETOTALBINS`
- Renamed `ALDecodeDataAsUInt` to `AudioLinkDecodeDataAsUInt`
- Renamed `ALDecodeDataAsSeconds` to `AudioLinkDecodeDataAsSeconds`
- Renamed `Remap` to `AudioLinkRemap`
- Renamed `HSVtoRGB` to `AudioLinkHSVtoRGB`
- Renamed `CCtoRGB` to `AudioLinkCCtoRGB`
- Renamed `GetSelfPixelData` to `AudioLinkGetSelfPixelData`
### New features
- Added a shader function get the version of AudioLink currently running in the world, `AudioLinkGetVersion`.
- Handling of 3D audio sources by Xiexe
- Amplify templates for Lit & Unlit
- Amplify functions for use in the above templates
- Left/right VU meter data (instead of just left)
- Left/right Waveform data (instead of just left)

### Changes
- Refactoring, restyling, and renaming across entire codebase
- AudioReactiveSurface (Amplify shader) converted to built-in AudioLink.cginc functions
- AudioLink shader menu reorganized
- Removed an extern call from AudioLink.cs update loop
- Reuse of internal sample arrays
### Bugfixes
- Audio values clamped to prevent overflow
- AVPro log spamming & mono output bugfix (mainly a problem if using VRChat w/ a headset)
