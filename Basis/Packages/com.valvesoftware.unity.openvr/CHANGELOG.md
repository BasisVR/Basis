# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.2.2] - 2024-03-21
### Changed 
- Updated to SteamVR SDK 2.2.3

## [1.2.1] - 2024-01-11
### Changed 
- Updated to SteamVR SDK 2.0.10

## [1.2.0] - 2023-08-24
### Added 
- Added limited OpenGL support (multipass only)
### Changed 
- Updated to SteamVR SDK 1.26.7
- Fix for unity projects set to binary serialization
- Fix for unnecessary preinit #80
- Fix for incorrect reporting of eye position #86
- Fix for broken depth textures in some situations #110
- Fix for multipass not working on some render pipelines 

## [1.1.5] - 2021-11-08
### Changed 
- Fixed issue with unicode paths / project names not working #108

## [1.1.4] - 2021-01-07
### Changed 
- Refixed issue with general builds crashing #52

## [1.1.3] - 2020-12-31
### Changed 
- Fixed issue with x86 builds crashing
- Fixed issue with general builds crashing #52
- Fixed issues with dll metadata not being correct for some platforms. #33 #71

## [1.1.1] - 2020-12-11
### Added 
- Added Refresh Rate info to HMD
- Added User Presence feature to HMD
### Changed 
- Fixed a bug when autoenabling the loader
- Moved common stats to the default accessors
- Fixed events not always being forwarded when using the SteamVR Unity Plugin
- Fixed depth texture support
- Cleaned up device layouts to stop suggesting we support them.
- Fixed support for overlay applications. They were trying to use scene-app-only APIs
- Closing xr settings window when open to avoid data corruption
- Fixed graphicsAdapterId reporting (laptops should use the correct graphics card)

## [1.0.1] - 2020-08-03
### Changed 
- Updating version of legacyinputhelpers

## [1.0.0-preview.11] - 2020-07-28
### Changed 
- Misc fixes to support linux builds. Linux is still WIP but these fixes affect the windows builds too.

## [1.0.0-preview.10] - 2020-07-22
### Changed 
- Fixing gitignore for some pdb meta files

## [1.0.0-preview.9] - 2020-07-22
### Changed 
- Statically linking vs debug runtime so people without vs can use the debug dlls

## [1.0.0-preview.8] - 2020-07-22
### Changed 
- Adding supporting debug dlls

## [1.0.0-preview.7] - 2020-07-21
### Changed 
- Requiring version 3.2.13 of the XR Management package due to critical errors in previous versions.

## [1.0.0-preview.6] - 2020-07-17
### Changed 
- Adding some subsystem failure cases to TrackingOrigin queries
- Switching to debug dlls. If you would like release DLLs either wait for v1.0 or build them from source.

## [1.0.0-preview.5] - 2020-07-15
### Changed 
- Fixed 32bit builds
- Fixed UWP builds
- Fixed IL2CPP builds
- Fixed some issues with Mirror View Mode. Now defaulting to Right Eye instead of OpenVR.
- Fixed issue where SteamVR Beta was required.

## [1.0.0-preview.4] - 2020-06-24
### Changed
- Switched from legacy input (Unity XR) to SteamVR Input via the SteamVR Unity Plugin for controller state processing
- Renamed package from com.valve.openvr to com.valvesoftware.unity.openvr
- Fixed some pathing issues with action manifests
- Fixed some rendering issues

## [1.0.0-preview.3] - 2020-06-24
### Added 
- Added NPM support
- Added some extra logging
- Added support for Unity 2019.4

### Changed
- Renamed package from com.valve.openvr to com.valvesoftware.unity.openvr
- Fixed some pathing issues with action manifests
- Fixed some rendering issues

## Removed
- Legacy Input support. To get input for now you'll need to use the SteamVR Unity Plugin.

## [1.0.0-preview.2] - 2020-04-29
### Added 
- Fixed some of the layouts, a fix for a black screen issue

## [1.0.0-preview.1] - 2020-04-29
### Added 
- Initial beta release