this is a heavily modified fork by BasisVR.

we have included the below from the main repo, https://github.com/llealloo/audiolink

[![Discord - AudioLink Discord](https://img.shields.io/badge/Discord-AudioLink_Discord-7289da?logo=discord&logoColor=7289da)](https://discord.gg/d5wjNwZBR3) [![Website - AudioLink Website](https://img.shields.io/badge/Website-AudioLink_Website-7289da)](https://audiolink.dev/)

# AudioLink

## A repository of audio reactive prefabs for Unity, written in CSharp and HLSL, compatible with VRChat and ChilloutVR

AudioLink is a system that analyzes and processes in-world audio into many different highly reactive data streams and exposes the data to Scripts and Shaders. 

The per-frequency audio amplitude data is first read briefly into Udon using Unity's GetOutputData. It is then sent to the GPU for signal processing and buffered into a CustomRenderTexture. Then, the CustomRenderTexture is broadcast globally (called `_AudioTexture`) which can be picked up by shaders both in-world and across all avatars. 

### [Public example world](https://vrchat.com/home/launch?worldId=wrld_8554f998-d256-44b2-b16f-74aa32aac214)
### [Frequently Asked Questions](FAQ.md)
### [Documentation for shader creators](https://github.com/llealloo/vrc-udon-audio-link/tree/master/Docs)

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

## Updating projects from version 0.2.8 or lower? (...first time setup? please see next section)
1. Before upgrading your project, **MAKE A BACKUP**! The latest version of AudioLink changes many things - better safe than sorry.
2. Take note of which AudioSource you are using to feed AudioLink, this reference may be lost during upgrade.
3. If you haven't ever used VRChat Creator Companion (VCC) with your project, follow the steps below. Otherwise, skip to step 4:
   - Download and install the [VRChat Creator Companion](https://vrchat.com/download/vcc), open it up.
   - Use the "Add" option in the "Projects" tab and follow the steps shown to add your project to the VCC.
   - Open the Projects tab, select your project, press the "Migrate" button and follow the steps shown.
4. Open the Projects tab and select your project.
5. On the right side, find the AudioLink package and add it. If it doesn't show up, make sure you have the "Curated" toggle enabled in the top-right drop-down.
6. In a file browser, **without Unity open**, navigate to your projects Assets folder and delete the "AudioLink" folder and "AudioLink.meta" file.
7. Open the Project in Unity.
8. You may be prompted by the AudioLink shader upgrader to upgrade old shaders. You should do so if your project uses any custom AudioLink-enabled shaders.
9. If you were using assets from the AudioLink example scene, you'll have to import it, as it isn't imported by default. To do so, use the "AudioLink -> Open AudioLink Example Scene" in top menu of the editor.
10. If you were using AudioReactiveObject or AudioReactiveLight
   components, you may need to manually re-enable the "Audio Data" under AudioLink "experimental" settings. This feature is now considered experimental until VRChat *maybe* gives us native asynchronous readback.
11. In scene(s) containing old versions of AudioLink:
   - Delete both AudioLink and AudioLinkController prefabs from the scene.
   - Re-add AudioLink and AudioLinkController to the scene by dragging the prefabs from the Packages/com.llealloo.audiolink/Runtime folder.
   - Click the "Link all sound reactive objects to this AudioLink" button on AudioLink inspector panel.
   - Drag the AudioSource you were using previously into the AudioLink audio source parameter.
      - NOTE: If you previously used AudioLinkInput, you are welcome to continue doing so, however now in 0.2.5+ AudioLink is much smarter about inputs. Try dragging it straight into the AudioLink / audio source parameter!

## Upgrading avatar projects
1. In a file browser, delete the "Assets/AudioLink" folder and the "AudioLink.meta" file.
2. Follow the "First time setup" steps for avatar projects described below.

## Installation

### VRC
Looking to test out an avatar? See the ["For VRC Avatar Testing"](#for-vrc-avatar-testing) section. Otherwise, see the ["For VRC Worlds and other usecases"](#for-vrc-worlds-and-other-usecases) section after installation.

#### VCC (Windows Only)
1. Download and install the [VRChat Creator Companion](https://vrchat.com/download/vcc) (VCC), open it up.
2. Add your project to the VCC:
   - If you want to create a new project, use the "New" option in the "Projects" tab and follow the steps there.
   - If you want to use an existing project, use the "Add" option in the "Projects" tab and follow the steps there.
3. Open the Projects tab and select your project. If you have never used the VCC with the project, use the "Migrate" button to upgrade it.
4. On the right side, find the AudioLink package and add it. If it doesn't show up, make sure you have the "Curated" toggle enabled in the top-right drop-down.
5. At this point, the installation is done. To open your project, you can use the "Open Project" button in the VCC. 

#### Commandline (Linux/macOs/Windows)
For installing on Linux and macOS, and for Windows users who prefer the command line, we recommend using `vrc-get`. This is because it is open-source and, at the time of writing, `vpm` has unresolved bug(s) that might stop it from working on some systems.

##### vrc-get
1. Install [vrc-get](https://github.com/anatawa12/vrc-get?tab=readme-ov-file#installation)
2. Open a console and `cd /path/to/your/project`
3. Run `vrc-get i audiolink`

##### vpm
1. Install [vpm](https://vcc.docs.vrchat.com/vpm/cli/#installation--updating/) and follow their setup instructions
2. Run `vpm add package com.llealloo.audiolink -p /path/to/your/unity/project`

### General (non VRC, including CVR)
1. Download and Import the latest **UnityPackage** AudioLink Release at https://github.com/llealloo/vrc-udon-audio-link/releases.
2. Use the "Tools/AudioLink/Add AudioLink Prefab to Scene" menu item. It should work out of the box.

## Getting started
After installation, to use AudioLink:
1. If you want to view the example scene, use the "AudioLink/Open AudioLink Example Scene" button in the top menu of the editor or use the "Tools/AudioLink/Add AudioLink Prefab to Scene" menu item.

### For VRC Avatar Testing
2. Under AudioLinkAvatar/AudioLinkInput, add a music track to the AudioClip in the AudioSource.
3. Enter playmode to test your avatar.

### For VRC Worlds and other usecases 
2. Click the "Link all sound reactive objects..." button on the AudioLink MonoBehaviour to link everything up.

## Compatible tools / assets
- [Silent Cel Shading Shader](https://gitlab.com/s-ilent/SCSS) by Silent
- [Mochies Unity Shaders](https://github.com/MochiesCode/Mochies-Unity-Shaders/releases) by Mochie
- [Fire Lite](https://discord.gg/24W435s) by Rollthered
- [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by AcChosen
- [Poiyomi Shader](https://poiyomi.com/) by Poiyomi
- [orels1 AudioLink Shader](https://github.com/orels1/orels1-AudioLink-Shader) by orels1
- [VRC Things](https://github.com/PiMaker/VRChatUnityThings) by \_pi\_
- [June Screen FX](https://www.luka.moe/june) by luka
- [ShaderForge-AudioLink](https://github.com/lethanan/ShaderForge-AudioLink) by lethanan
- [AudioLink-USharpVideo-Adapter](https://github.com/Blabzillaweasel/AudioLink-USharpVideo-Adapter/) by Blabz
- [ProTV](https://gitlab.com/techanon/protv) by ArchiTechAnon

## Thank you
- phosphenolic for the math wizardry, conceptual programming, debugging, design help and emotional support!!!
- [cnlohr](https://github.con/cnlohr) for the help with the new DFT spectrogram and helping to port AudioLink to 100% shader code
- [lox9973](https://gitlab.com/lox9973) for autocorrelator functionality and the inspirational & tangential math help with signal processing
- [Texelsaur](https://github.com/jaquadro) for the AudioLinkMiniPlayer and support!
- [Pema](https://github.com/pema99) for the help with strengthening the codebase and inspiration!
- [3](https://github.com/float3) for joining the AudioLink team, helping maintain the codebase, and being instrumental in getting version 0.3.0 out.
- [Merlin](https://github.com/merlinvr) for making UdonSharp and offering many many pointers along the way. Thank you Merlin!
- [Orels1](https://github.com/orels1) for all of the great help with MaterialPropertyBlocks & shaders and the auto configurator script for easy AV3 local testing
- [Xiexe](https://github.com/Xiexe/) for the help developing and testing
- [Thryrallo](https://github.com/thryrallo) for the help setting up local AV3 testing functionality
- [CyanLaser](https://github.com/CyanLaser/) for making CyanEmu
- [Lyuma](https://github.com/lyuma/) for helping in many ways and being super nice!
- [ACIIL](https://github.com/aciil) for the named texture check in AudioLink.cginc
- [fuopy](https://github.com/fuopy) for being awesome and reflecting great vibes back into this project
- Colonel Cthulu for incepting the idea to make the audio data visible to avatars
- jackiepi for math wizardry, emotional support and inspiration
- Barry and OM3 for stoking my fire!
- [Lamp](https://soundcloud.com/lampdx) for the awesome example music and inspiration. Follow them!! https://soundcloud.com/lampdx
- [Shelter](https://sheltervr.club/), [Loner](https://loneronline.com/), [Rizumu](https://x.com/rizumuvr), and all of the other dance communities in VRChat for making this
- [rrazgriz](https://github.com/rrazgriz) for coming up with and implementing yt-dlp support for editor testing
- [LucHeart](https://github.com/lucheart) and [DomNomNom](https://github.com/DomNomNomVR) for maintaing CVR forks of AudioLink, and letting us adopt their work
- [Rollthered](https://linktr.ee/Rollthered) for providing us with music for demo purposes.
- [fundale](https://github.com/fundale/) for figuring out WebGL support
- all other [contributors](https://github.com/llealloo/vrc-udon-audio-link/graphs/contributors) and our community for their help and support
