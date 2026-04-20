# OpenVR XR SDK Package

The purpose of this package is to provide OpenVR rendering to Unity XR. This package provides the necessary sdk libraries for users to build Applications that work with the OpenVR runtime. The OpenVR XR Plugin gives you access to rendering on all major VR devices through one interface. Explicit support for: HTC Vive, HTC Vive Cosmos, Oculus Rift, Oculus Rift S, Oculus Quest (link), Windows Mixed Reality, and Valve Index. Other SteamVR compatible devices are supported though may have inaccurate or incomplete features.

# Documentation

There is some brief documentation included with this plugin at /Documentation~/com.valvesoftware.unity.openvr.md

# Input

As part of our commitment to [OpenXR](https://store.steampowered.com/newshub/app/250820/view/2396425843528787269) we will begin targeting the OpenXR API in future versions of our game engine plugins.

For now, to get access to controllers and other forms of input you will need to install the latest SteamVR Unity Plugin. This can be found on the Unity Asset Store (https://assetstore.unity.com/packages/tools/integration/steamvr-plugin-32647) or GitHub (https://github.com/ValveSoftware/steamvr_unity_plugin/releases).


## Known Issues:
* Display Provider
  * In some circumstances the unity console will show errors on start saying "screen position out of view frustrum". This should not impact visuals.
  * OpenVR Mirror View Mode (default) can cause black screens in the game view. Please send us bug reports if this happens.
  * OpenVR Mirror View Mode requires use of Linear Color Space (Project Settings > Player > Other Settings > (Rendering) Color Space)
  * Linux - Vulkan - Multipass will crash.
  * Linux - OpenGL - Single Pass Instanced will crash.



## Bug reports:
* For bug reports please create an issue on our github (https://github.com/ValveSoftware/unity-xr-plugin/issues) and include the following information
  * Detailed steps of what you were doing at the time
  * Your editor or build log (editor log location: %LOCALAPPDATA%\Unity\Editor\Editor.log)
  * A SteamVR System report DIRECTLY AFTER encountering the issue. (SteamVR interface -> Menu -> Create System Report -> Save to file)


## QuickStart

### Installation
* Download the installer: https://github.com/ValveSoftware/unity-xr-plugin/releases/tag/installer
* Open your unity project and then open/import the unitypackage
* Open the XR Management UI (Edit Menu -> Project Settings -> XR Plugin Management)
* Click the checkbox next to OpenVR Loader - or in older versions - Under Plugin Providers hit the + icon and add “Open VR Loader”


### Standalone (no input)
* Add a cube to the scene (scale to 0.1)
* Add a Camera component to the cube
* Add TrackedPoseDriver to the cube
 *	Main Camera: Under Tracked Pose Driver:
    * For Device select: “Generic XR Device”
    * For Pose Source select: “Center Eye - HMD Reference”
* Hit play and you should see a tracked camera


### SteamVR Input System:
* Install SteamVR Unity Plugin v2.6.1+ from the Asset Store (https://assetstore.unity.com/packages/tools/integration/steamvr-plugin-32647) or GitHub (https://github.com/ValveSoftware/steamvr_unity_plugin/releases/)
* It will install the OpenVR XR API package automatically for 2020.1+ for 2019.3/4 you’ll need to add it with the instructions above.
* Open the SteamVR Input window (Window -> SteamVR Input)
* Accept the default json
* Click Save and Generate
* Open the Interactions_Example scene (Assets/SteamVR/InteractionSystem/Samples/Interaction_Example.unity)
* Hit play, verify that you can see your hands and teleport around


