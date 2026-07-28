# KlakSyphon

![screenshot](https://i.imgur.com/eputm6Am.jpg)

**KlakSyphon** is a Unity plugin that lets Unity send and receive video streams
through the [Syphon] system.

[Syphon]: http://syphon.v002.info

## System Requirements

- Unity 2022.3 or later
- macOS system with Metal graphics API support

## How to Install

Install the KlakSyphon package (`jp.keijiro.klak.syphon`) from the "Keijiro"
scoped registry in Package Manager. Follow [these instructions] to add the
registry to your project.

[these instructions]:
  https://gist.github.com/keijiro/f8c7e8ff29bfe63d86b888901b82644c

## Syphon Server Component

![Syphon Server](https://github.com/user-attachments/assets/87d76625-72c2-4764-9c76-d522bb815b42)

Use the **Syphon Server** component to send a video stream. It provides three
capture methods:

- **Game View**: Captures the content of the Game View.
- **Camera**: Captures a specified camera.
- **Texture**: Captures a 2D texture or a Render Texture.

The Camera capture method is available only on URP and HDRP—you can't use it on
the built-in render pipeline.

The **KeepAlpha** property controls whether the alpha channel is preserved or
cleared. Enable [alpha output] when using HDRP. On URP, select the Texture
capture method to output alpha.

[alpha output]:
  https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@12.0/manual/Alpha-Output.html

## Syphon Client Component

![Syphon Client](https://github.com/user-attachments/assets/0cf494ef-6df9-4dbc-899e-a6874fcf6093)

Use the **Syphon Client** component to receive a video stream. It stores
incoming frames in the Target Texture and overrides the material property set
in the Target Renderer.

You can also access the received texture via the `SyphonClient.Texture`
property.

## Scripting Interface

Enumerate available Syphon servers with the `SyphonServerDirectory` class; see
the [SourceSelector example] for details.

[SourceSelector example]:
  https://github.com/keijiro/KlakSyphon/blob/main/Assets/Scripts/SourceSelector.cs

You can create Syphon servers or clients at runtime, but you must assign the
`SyphonResources` asset (which holds references to package assets) after
instantiation.
