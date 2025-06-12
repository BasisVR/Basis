# OpusSharp
OpusSharp aims to be a cross platform, pure and ported C# compatible version of the native opus codec/library. The core library uses the native compiled DLL's/binaries. Currently, Windows, Android and Linux binaries are available. iOS and MacOS are in the works. OpusSharp compiles the opus binaries using a github actions file which is available [here](.github/workflows/OpusCompile.yml).

> [!NOTE]
> While OpusSharp.Core contains minimal pre-made decoder and encoder handlers, you can create your own as all the SafeHandlers and NativeOpus functions are exposed and fully documented. However to get a minimal setup working, check the example below.

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

## CTL Example
```cs
using OpusSharp.Core;

OpusEncoder encoder = new OpusEncoder(48000, 2, OpusPredefinedValues.OPUS_APPLICATION_VOIP);

int vbr = 1; //1 == true, 0 == false
encoder.Ctl<int>(EncoderCTL.OPUS_SET_VBR, ref vbr); //OpusSharp already checks if an error occurred with the CTL request and will throw an OpusException if there is an error, otherwise OpusErrorCodes.OPUS_OK.
```

## Supported Devices

- ✅ Fully and natively supported.
- ❎ Can be supported but no reason to.
- ❗ Not yet available.
- ❌ Not planned, Not supported.

|Device     |x64|x86|arm32|arm64|
|-----------|---|---|-----|-----|
|Linux      |✅ |✅ |✅   |✅   |
|Android    |✅ |✅ |✅   |✅   |
|Windows    |✅ |✅ |✅   |✅   |
|iOS        |❗ |❗ |❗   |❗   |
|MacOS      |❗ |❗ |❗   |❗   |

## Installation
Please check [QuickStart](https://avionblock.github.io/OpusSharp/quick-start/index.html) OR [Nuget](https://www.nuget.org/packages/OpusSharp)

## API Documentation
https://avionblock.github.io/OpusSharp/api/OpusSharp.Core.html
