# OpusSharp

OpusSharp aims to be a cross-platform, pure and ported C# compatible version of the native opus codec/library. The core
library uses the native compiled DLL's/binaries. Windows, Android, Linux, macOS and iOS are supported. OpusSharp
compiles the opus binaries using a github actions file which is available [here](.github/workflows/OpusCompile.yml).

> [!NOTE]
> While OpusSharp.Core contains minimal pre-made decoder and encoder handlers, you can create your own as all the
> SafeHandlers and NativeOpus functions are exposed and fully documented. However to get a minimal setup working, check
> the example below.

## Encoder Example

Simple encoder example for initializing and encoding. You of course can use `short[]` or `float[]` arrays to encode as
well.

```cs
using OpusSharp.Core;

byte[] someAudioData = ...;
var frameSizeMs = 20;
var sampleRate = 48000;
var channels = 1;
var samplesPerFrame = sampleRate / (1000 / frameSizeMs) * channels; //960 samples per frame.
var encoder = new OpusEncoder(sampleRate, channels, OpusPredefinedValues.OPUS_APPLICATION_VOIP);

byte[] encodedAudio = new byte[1000] //1000 bytes for an encoded buffer should be enough according to the opus documentation.
var encodedBytes = encoder.Encode(someAudioData, samplesPerFrame, encodedAudio, encodedAudio.Length);

//We can use encodedBytes to trim any excess data from the encodedAudio buffer before sending over the network or writing to a file.
```

## Decoder Example

Simple decoder example for initializing and decoding. You of course can use `short[]` or `float[]` arrays to encode as
well.

```cs
using OpusSharp.Core;

byte[] someEncodedAudio = ...;
var frameSizeMs = 20;
var sampleRate = 48000;
var channels = 1;
var samplesPerFrame = sampleRate / (1000 / frameSizeMs) * channels; //960 samples per frame.
var decoder = new OpusDecoder(sampleRate, channels);

var decoded = new byte[1920]; //We get 1920 bytes from doing this calculation because 16/8 (16 bit audio, 1 byte is 8 bits) equals 2 multiplied by samplesPerFrame gets us bytes per frame. 16/(sizeof(byte) * 8) * samplesPerFrame
var decodedSamples = decoder.Decode(someEncodedAudio, someEncodedAudio.Length, decoded, samplesPerFrame, false);
```

## Static Usage Example

This example shows a forced usage of OpusSharp to use the statically linked opus binary.

```csharp
// On iOS OpusSharp switches to StaticNativeOpus automatically.
// You can still force the same behavior manually with use_static: true.
var encoder = new OpusEncoder(sampleRate, channels, OpusPredefinedValues.OPUS_APPLICATION_VOIP, use_static: true);
var encoder = new OpusDecoder(sampleRate, channels, use_static: true);
```

## Unity Example

For unity integration, you may want to use `Static.OpusDecoder`, `Dynamic.OpusDecoder`, `Static.OpusEncoder` or
`Dynamic.OpusEncoder` to prevent IL2CPP errors for example...

```csharp
using OpusSharp.Core;
    
IOpusEncoder encoder;
IOpusDecoder decoder;
//Decoder
#if UNITY_IOS && !UNITY_EDITOR
encoder = new Static.OpusEncoder(...);
decoder = new Static.OpusDecoder(...);
#else
encoder = new Dynamic.OpusEncoder(...);
decoder = new Dynamic.OpusDecoder(...);
```

## Basic NAudio Example

```cs
using NAudio.Wave;
using OpusSharp.Core;

var format = new WaveFormat(48000, 2);
var buffer = new BufferedWaveProvider(format) { ReadFully = true };
var encoder = new OpusEncoder(format.SampleRate, format.Channels, OpusPredefinedValues.OPUS_APPLICATION_VOIP);
var decoder = new OpusDecoder(format.SampleRate, format.Channels);
var recorder = new WaveInEvent() { BufferMilliseconds = 20, WaveFormat = format };
var player = new WaveOutEvent();
recorder.DataAvailable += Recorder_DataAvailable;

recorder.StartRecording();
player.Init(buffer);
player.Play();

void Recorder_DataAvailable(object? sender, WaveInEventArgs e)
{
    var encoded = new byte[1000];
    var encodedBytes = encoder.Encode(e.Buffer, 960, encoded, encoded.Length);
    Console.WriteLine(encodedBytes);
    var decoded = new byte[3840];
    var decodedSamples = decoder.Decode(encoded, encodedBytes, decoded, 960, false);
    Console.WriteLine(decodedSamples);
    buffer.AddSamples(decoded, 0, decoded.Length);
}

Console.ReadLine();
```

## CTL Example using OpusSharp.Core.Extensions

This CTL example shows the usage of extensions to make setting CTL's easier.

```csharp
using OpusSharp.Core;
using OpusSharp.Core.Extensions;

OpusEncoder encoder = new OpusEncoder(48000, 2, OpusPredefinedValues.OPUS_APPLICATION_AUDIO);

encoder.SetComplexity(2);
Console.WriteLine(encoder.GetComplexity());
```

## CTL Example

This example shows the raw usage of calling a CTL with no extensions, OpusSharp will directly pass the call to the opus
binary.

```cs
using OpusSharp.Core;

OpusEncoder encoder = new OpusEncoder(48000, 2, OpusPredefinedValues.OPUS_APPLICATION_VOIP);

//1 == true, 0 == false
encoder.Ctl<int>(EncoderCTL.OPUS_SET_VBR, 1); //OpusSharp already checks if an error occurred with the CTL request and will throw an OpusException if there is an error, otherwise OpusErrorCodes.OPUS_OK.

//Most setter CTL's do not require a pointer reference. All Getter CTL's require a pointer reference (for now).
```

> [!NOTE]
> While disposing of encoder's and decoder's are handled by the GC (garbage collector) since unmanaged states are
> wrapped in a SafeHandler, It is still recommended to directly call the Dispose() function due to different runtime
> environments such as unity's mono runtime (at the time of writing this) which can take an impact on performance
> depending on how many is initialized and dereferenced over time.

## Supported Devices

- ✅ Fully and natively supported.
- ❎ Can be supported but no reason to.
- ❗ Not yet available.
- ❌ Not planned, Not supported.

| Device  | x64 | x86 | arm32 | arm64 |
|---------|-----|-----|-------|-------|
| Linux   | ✅   | ✅   | ✅     | ✅     |
| Android | ✅   | ✅   | ✅     | ✅     |
| Windows | ✅   | ✅   | ✅     | ✅     |
| iOS     | ❌   | ❌   | ❌     | ✅     |
| MacOS   | ✅   | ❌   | ❌     | ✅     |
| WASM    | ✅   | ❗   | ❗     | ❗     |

## Installation

Please check [QuickStart](https://avionblock.github.io/OpusSharp/quick-start/index.html)
OR [Nuget](https://www.nuget.org/packages/OpusSharp).
For the default cross-platform experience, install [OpusSharp](https://www.nuget.org/packages/OpusSharp). It brings in
both `OpusSharp.Core` and the prebuilt native assets, including automatic iOS linking through `OpusSharp.Natives`.

If you want to manage native binaries yourself, only install the `OpusSharp.Core` package.

## API Documentation

https://avionblock.github.io/OpusSharp/api/OpusSharp.Core.html

## Opus License

https://opus-codec.org/license/
