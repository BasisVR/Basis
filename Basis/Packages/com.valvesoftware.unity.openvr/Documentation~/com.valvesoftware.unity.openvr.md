# About

The purpose of this package is to provide OpenVR rendering to Unity XR. This package provides the necessary sdk libraries for users to build Applications that work with the OpenVR runtime. The OpenVR XR Plugin gives you access to rendering on all major VR devices through one interface. Explicit support for: HTC Vive, HTC Vive Cosmos, Oculus Rift, Oculus Rift S, Oculus Quest (link), Windows Mixed Reality, and Valve Index. Other SteamVR compatible devices are supported though may have inaccurate or incomplete features.

## Subsystems

### Display 
The display subsystem provides rendering support for the XR Plugin. It currently supports DirectX 11 and Vulcan.

### Input 

* **SteamVR Input**
To use the full power of SteamVR we recommend also downloading our SteamVR for Unity plugin. It is in beta [on our github releases page.](https://github.com/ValveSoftware/steamvr_unity_plugin/releases/tag/2.6.0b1) This plugin can run alongside the OpenVR XR plugin. However, you will not be able to query Unity's input functions while using SteamVR Input. The two systems are currently incompatible and you must chose to use one or the other.

## XR Management

To use our XR Management support open the XR Management settings page (Project Settings -> XR Plugin Management) and click the checkbox next to OpenVR Loader. Or for older versions of the XR Management UI: click the '+' icon under Plugin Providers, then select Open VR Loader.

* **Settings** 
 * **Application Type** - This gives you the option between creating a Scene app (most games / applications) and an Overlay app. Overlay apps can run alongside Scene apps providing tools to the user. For more information on Overlay Apps see [this documentation page here](https://github.com/ValveSoftware/openvr/wiki/IVROverlay_Overview)
 * **Stereo Rendering Mode** - Currently supported modes are Multi Pass (render each eye independently) and Single Pass Instanced (render both eyes at once). For more information on the types of rendering modes see [Unity's documentation page here](https://docs.unity3d.com/Manual/SinglePassStereoRendering.html)
 * **Mirror View Mode** - This is a setting you can change at runtime that controls what view is shown in the desktop window. Please note that selecting these options does not trigger a separate full render of the scene. Options are None, Left or Right eye, or OpenVR's view. OpenVR's view will show you the same thing you can see out of the SteamVR VR View. This is usually a blend between the left and right eye as well as any SteamVR overlays.
