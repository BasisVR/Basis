## **Question**:
How should I install AudioLink for newly created projects?
### **Answer**:
Use the VRChat Creator Companion (https://vcc.docs.vrchat.com/) or VRChat Package Manager. AudioLink is available under the Curated section.

## **Question**:
Will the AudioLink-enabled shaders on my avatars continue to work in worlds using AudioLink 0.3.0
### **Answer**:
Yes.

## **Question**:
Are shaders written for previous versions of AudioLink usable in projects that use AudioLink 0.3.0?
### **Answer**:
It depends:
- Shaders that include the AudioLink.cginc file to support AudioLink will have to be upgraded. We have included a tool that can do this for you automatically, you can access it via the "Update AudioLink compatible shaders" button in the new AudioLink dropdown in the top menu bar of the Unity editor.
- Shaders that use their own custom code to sample data provided by AudioLink will not have to be changed at all.
If you are unsure which of these applies to a specific shader, ask the author, they should know.

## **Question**:
Are shaders written for AudioLink 0.3.0+ usable in projects that use older versions of AudioLink?
### **Answer**:
No.

## **Question**:
Where are the AudioLink related prefabs? I can't find them anymore!
### **Answer**:
They are located in the Packages/com.llealloo.audiolink/Runtime/ folder. Note that searching in the project view will by default not include results within packages. When searching, a button near the top of the project view will appear reading In Packages. Press this to include search results within AudioLink files.

## **Question**:
Can I use AudioLink 0.3.0+ with UdonSharp v0.20.3 or older?
### **Answer**:
No, AudioLink now requires UdonSharp 1.x, which is currently only distributed via the VRChat Creator Companion (https://vcc.docs.vrchat.com/) and VRChat Package Manager.

## **Question**:
I want to use AudioLink 0.3.0+, but am not interested in VRChat at all. How do I install the standalone version?
### **Answer**:
Download the standalone unitypackage from here (https://github.com/llealloo/vrc-udon-audio-link/releases/download/0.3.0/AudioLink_0.3.0_minimal.unitypackage), and import as any other unitypackage.

## **Question**:
I am seeing a bunch of errors in the console after installing AudioLink 0.3.0. What do I do?
### **Answer**:
There is known issue where, when first importing AudioLink, error messages will be spammed in the console window. This is harmless and should automatically fix itself. Simply clear the console and proceed as normal.
If more errors occur after doing so, press the Reload SDK button under the VRChat SDK dropdown in the top menu bar of the Unity editor.
If after doing so, error messages still persist. Go Edit > Project Settings > Player > Other Settings > Scripting Define Symbols and verify that both UDONSHARP, AUDIOLINK and VRC_SDK_VRCSDK3 are present in the in the text box. If they are not, ask us for help in help 

## **Question**:
How do I upgrade my old projects to use AudioLink 0.3.0?
### **Answer**:
AudioLink 0.3.0 requires use of the VRChat Creator Companion (https://vcc.docs.vrchat.com/) or VRChat Package Manager to install. If you are already using these tools, you can just grab AudioLink straight from the curated packages section! If you have not already upgraded your project using the tools, you will have to do so before installing AudioLink. Please follow the upgrade guide here (https://github.com/llealloo/vrc-udon-audio-link##updating-projects-from-version-28-or-lower-first-time-setup-please-see-next-section). 

## **Question**:
I'm having trouble using VR Stage Lighting (VRSL) with AudioLink. Where should I ask for help?
### **Answer**:
https://vrsl.dev/

## **Question**:
I've installed AudioLink in an avatar (or standalone) project, but I'm seeing missing script references on some of the prefabs. How do I fix this?
### **Answer**:
These are hidden components added by U# automatically. The missing script references are harmless, you can just ignore them, or remove them if you want. They occur since the prefabs are shared between different distributions of AudioLink. 
