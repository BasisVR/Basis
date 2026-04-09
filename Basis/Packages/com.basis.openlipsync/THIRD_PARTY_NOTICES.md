Third-Party Software Notices
============================

This package includes or depends on the following third-party components:

## Microsoft ONNX Runtime

- Files: `Plugins/Managed/Microsoft.ML.OnnxRuntime.dll`, `Plugins/win-x64/native/onnxruntime.dll`, `Plugins/win-x64/native/onnxruntime_providers_shared.dll`
- Version: 1.x
- License: MIT
- Source: https://github.com/microsoft/onnxruntime

```
Copyright (c) Microsoft Corporation. All rights reserved.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Newtonsoft.Json (Json.NET)

- Referenced via: `Newtonsoft.Json.dll` (external dependency, not bundled)
- License: MIT
- Source: https://github.com/JamesNK/Newtonsoft.Json

```
Copyright (c) 2007 James Newton-King

MIT License (same terms as above)
```

## LibriSpeech (Training Data)

- Used for: Training the included TCN viseme model (`model.onnx.bytes`)
- Subset: train-clean-100
- License: CC BY 4.0
- Source: https://www.openslr.org/12
- Citation: V. Panayotov, G. Chen, D. Povey, and S. Khudanpur,
  "LibriSpeech: An ASR corpus based on public domain audio books,"
  in Proc. ICASSP, 2015.

## ONNX Model (`model.onnx.bytes`)

- Architecture: TCN (Temporal Convolutional Network), 5 layers, 128 channels
- Output: 15 MPEG-4 visemes
- License: Apache-2.0 (same as this package)
