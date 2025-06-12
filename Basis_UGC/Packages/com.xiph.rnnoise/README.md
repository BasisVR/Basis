# RNNoise.NET
.NET Wrapper for the RNNoise supression library:
- https://jmvalin.ca/demo/rnnoise/
- https://github.com/xiph/rnnoise

This is currently provided as is without too many whistles and bells. It will handle processing varying buffer sizes for you, with internal buffers to hold the data temporarily (the library always processes fixed frame sizes of 480 samples).

I recommend using my fork of the rnnoise library for compile the shared libraries. It has build scripts for Windows, Linux and Android:
https://github.com/Yellow-Dog-Man/rnnoise
