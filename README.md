 ### Basis lets you set up your own Social VR and Networked VR games with ease.

<table border="0">
 <tr>
    <td><div align="center"><img src="./Basis/Images/BasisLogo.png" alt="Logo" width="160" height="160"></td>
    <td><div align="center"><h3><strong>Basis</strong></h3>
The Social VR Framework</br>
<a href="https://discord.gg/F35u3cUMqt"><strong>Join our Discord!»</strong></a></br></br>
<a href="https://github.com/BasisVR/Basis/issues/new?labels=bug&template=bug-report---.md">Report Bug</a> -
<a href="https://github.com/BasisVR/Basis/issues/new?labels=enhancement&template=feature-request---.md">Request Feature</a></div></td>
 </tr>
</table>

 ## About Basis

[Basis Philosophy](https://basisvr.org/philosophy) <- read our Philosophy here!

We are an MIT-Licensed Open-Source project with a focus on open development and full access to any optional modification desired or required.

Our goal is to help equip VR Creators, so we can accelerate the growth of VR.

<img src="./Basis/Images/Banner.png" alt="Banner" width="550" height="155">

We are actively working on Basis. If you like what you see, please consider contributing in any way you can.

 ## How you can Contribute

Do you have a suggestion for improving Basis? Please fork the repo and create a pull request! You can also open an issue with the tag “improvement”.
Not sure how to contribute, but still wanting to help out? Consider donating! We appreciate any help possible.

<noscript><a href="https://liberapay.com/dooly/donate"><img alt="Donate using Liberapay" src="https://liberapay.com/assets/widgets/donate.svg"></a></noscript> [Github Sponsor](https://github.com/sponsors/dooly123)</br>[KoFi](https://ko-fi.com/dooly)</br>

Please help shape the future of Basis and leave your mark on its foundation.

 #### Creating a Fork

1. Fork the Project
2. Optionally, [Setup CI Secrets](./CI.md).
3. Create your Feature Branch (`git checkout -b feature/ACrazyNewFeature`)
4. Commit your Changes (`git commit -m 'Add some ACrazyNewFeature'`)
5. Push to the Branch (`git push origin feature/ACrazyNewFeature`)
6. Open a Pull Request

 ## Installation

This project is currently using Unity 6 (open the project through Unity Hub to see the version)
Other Unity versions may work, but will require changes and adaptations.
Currently, OPENXR and SteamVR are supported, as well as OPENXR Quest.

As a note, command line args for basis are:

to disable booting a VR mode.
 --disable-OpenVRLoader
 --disable-OpenXRLoader

to force a VR mode from boot.
 --force-OpenXRLoader
 --force-OpenVRLoader
 
1. Make sure you have the correct Unity version installed.
2. Clone the repository
   ```sh
   git clone https://github.com/BasisVR/Basis.git
   ```
3. Open the project and make sure to load the scene Initialisation
4. Enter play!

 ## Contact

basis enquiries - developerbasis@gmail.com
Luke Dooly - [@lukedooly](https://x.com/lukedooly) - doolanl208@gmail.com

Discord:</br>
[Our Discord Community](https://discord.gg/F35u3cUMqt)</br>
[Doolys Discord Account](https://discord.com/users/170859544782700544)

Thank you to everyone who has helped Basis become something remarkable.

 ## License

Distributed under the MIT License. See [MIT License](https://opensource.org/licenses/MIT) for more information.

 ### Built With

This would not be possible without the following:
- [ULipSync](https://github.com/hecomi/uLipSync)
- [UnityJigglePhysics](https://github.com/naelstrof/UnityJigglePhysics)
- [opussharp](https://github.com/AvionBlock/OpusSharp)
- [opus](https://github.com/xiph/opus)
- [Steam Audio](https://github.com/ValveSoftware/steam-audio)
- [Unity Starter Assets - ThirdPerson](https://assetstore.unity.com/packages/essentials/starter-assets-thirdperson-updates-in-new-charactercontroller-pa-196526)
- [RNNoise](https://github.com/xiph/rnnoise?tab=BSD-3-Clause-1-ov-file)
- [RNNoise.Net](https://github.com/Yellow-Dog-Man/RNNoise.Net)
- [unity](https://unity.com/)
- [ionic icons](https://github.com/ionic-team/ionicons?ref=svgrepo.com)
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib)
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)
- [cilbox](https://github.com/cnlohr/cilbox)

## Third-Party Code and Trademarks

This project includes third-party software under the following licenses:

### Apache License 2.0
- [Steam Audio](https://github.com/ValveSoftware/steam-audio) - See `Basis/Packages/com.steam.steamaudio/LICENSE.md`
- [OpenLipSync ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT) - See `Basis/Packages/com.basisvr.openlipsync/THIRD_PARTY_NOTICES.md`

### BSD-3-Clause
- [OpenVR](https://github.com/valvesoftware/openvr) - (C) Valve Corporation. See `Basis/Packages/com.valvesoftware.unity.openvr/LICENSE.md`
- [SteamVR](https://github.com/ValveSoftware/steamvr_unity_plugin) - (C) Valve Corporation. See `Basis/Packages/com.steam.steamvr/LICENSE`

### BSD (Modified/Clear)
- [Opus Codec](https://github.com/xiph/opus) - Copyright 2001-2011 Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry, CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. See `Basis/Packages/com.avionblock.opussharp/Opus_LICENSE_PLEASE_READ.txt`

### MIT
- [uLipSync](https://github.com/hecomi/uLipSync) - Copyright 2021 hecomi. See `Basis/Packages/com.hecomi.ulipsync/LICENSE.md`
- [OpusSharp](https://github.com/AvionBlock/OpusSharp) - Copyright 2026 AvionBlock. See `Basis/Packages/com.avionblock.opussharp/LICENSE.txt`
- [URP Volumetric Fog](https://github.com/cqf2186863072/URP-Volumetric-Fog) - Copyright 2025 Cristian Qiu Felez. See `Basis/Packages/com.cqf.urpvolumetricfog/LICENSE.md`
- [RNNoise.Net](https://github.com/Yellow-Dog-Man/RNNoise.Net) - Copyright 2023 Yellow Dog Man Studios. See `Basis/Packages/com.xiph.rnnoise/LICENSE`
- [Basis Comms](https://github.com/hai-vr/basis-comms) - Copyright 2025 Hai~ and MR LUKE B DOOLAN. See `Basis/Packages/dev.hai-vr.basis.comms/LICENSE`
- [MeaMod.DNS](https://github.com/meamod/MeaMod.DNS) - Copyright 2021 James Weston. See `Basis/Packages/nuget.meamod.dns/LICENSE`
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) - Copyright 2007 James Newton-King. See `Basis/Packages/org.basisvr.newtonsoft.json/LICENSE`
- [BouncyCastle](https://github.com/bcgit/bc-csharp) - Copyright 2000-2024 The Legion of the Bouncy Castle Inc. See `Basis/Packages/org.basisvr.bouncycastle/LICENSE`
- [Base128](https://github.com/Wojmik/Base128) - See `Basis/Packages/org.basisvr.base128/LICENSE`
- [Generator.Equals](https://github.com/diegofrata/Generator.Equals) - Copyright Diego Frata. See `Basis/Packages/org.basisvr.generator.equals/LICENSE`
- [SimpleBase](https://github.com/ssg/SimpleBase) - Copyright Sedat Kapanoglu. See `Basis/Packages/org.basisvr.simplebase/LICENSE`
- [ZeroMessenger](https://github.com/Cysharp/ZeroMessenger) - Copyright 2024 Annulus Games. See `Basis/Packages/com.basis.zeromessenger/LICENSE.md`
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) - Copyright 2017 Milosz Krajewski. See `Basis/Packages/org.basisvr.k4os.compression.lz4/LICENSE`
- [UnityJigglePhysics](https://github.com/naelstrof/UnityJigglePhysics) - MIT licensed upstream
- [AudioLink](https://github.com/llealloo/vrc-udon-audio-link) - MIT licensed upstream
- [cilbox](https://github.com/cnlohr/cilbox) - MIT licensed upstream

### Other
- [HVRBasisNDMF](https://github.com/hai-vr/ndmf) - See upstream for license terms

### Trademarks

"Valve", "Steam", and the associated figurative images are trademarks and/or registered trademarks of Valve Corporation in the US and in various other jurisdictions. All rights reserved. Use of these trademarks must comply with the guidelines outlined in `Basis/Packages/com.steam.steamaudio/TRADEMARK_RIGHTS.md`.

## Basis Trademark Guidelines

"Basis", "BasisVR", "Basis Framework", and the Basis logo are marks representing the
Basis Project. Please see [TRADEMARK.md](./TRADEMARK.md) for our policies
on their usage.
