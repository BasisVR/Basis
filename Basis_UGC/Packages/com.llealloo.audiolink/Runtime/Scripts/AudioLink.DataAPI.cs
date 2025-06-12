using System;
using UnityEngine;

namespace AudioLink
{
    public partial class AudioLink
    {
        #region Passes
        /// <remarks>Corresponds to ALPASS_DFT in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassDft() => new Vector2(0, 4);
        /// <remarks>Corresponds to ALPASS_WAVEFORM in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassWaveform() => new Vector2(0, 6);
        /// <remarks>Corresponds to ALPASS_AUDIOLINK in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioLink() => new Vector2(0, 0);
        /// <remarks>Corresponds to ALPASS_AUDIOBASS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioBass() => new Vector2(0, 0);
        /// <remarks>Corresponds to ALPASS_AUDIOLOWMIDS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioLowMids() => new Vector2(0, 1);
        /// <remarks>Corresponds to ALPASS_AUDIOHIGHMIDS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioHighMids() => new Vector2(0, 2);
        /// <remarks>Corresponds to ALPASS_AUDIOTREBLE in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioTreble() => new Vector2(0, 3);
        /// <remarks>Corresponds to ALPASS_AUDIOLINKHISTORY in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAudioLinkHistory() => new Vector2(1, 0);
        /// <remarks>Corresponds to ALPASS_GENERALVU in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVU() => new Vector2(0, 22);
        /// <remarks>Corresponds to ALPASS_GENERALVU_INSTANCE_TIME in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUInstanceTime() => new Vector2(2, 22);
        /// <remarks>Corresponds to ALPASS_GENERALVU_LOCAL_TIME in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVULocalTime() => new Vector2(3, 22);
        /// <remarks>Corresponds to ALPASS_GENERALVU_NETWORK_TIME in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUNetworkTime() => new Vector2(4, 22);
        /// <remarks>Corresponds to ALPASS_GENERALVU_PLAYERINFO in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUPlayerInfo() => new Vector2(6, 22);
        /// <remarks>Corresponds to ALPASS_THEME_COLOR0 in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassThemeColor0() => new Vector2(0, 23);
        /// <remarks>Corresponds to ALPASS_THEME_COLOR1 in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassThemeColor1() => new Vector2(1, 23);
        /// <remarks>Corresponds to ALPASS_THEME_COLOR2 in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassThemeColor2() => new Vector2(2, 23);
        /// <remarks>Corresponds to ALPASS_THEME_COLOR3 in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassThemeColor3() => new Vector2(3, 23);
        /// <remarks>Corresponds to ALPASS_GENERALVU_UNIX_DAYS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUUnixDays() => new Vector2(5, 23);
        /// <remarks>Corresponds to ALPASS_GENERALVU_UNIX_SECONDS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUUnixSeconds() => new Vector2(6, 23);
        /// <remarks>Corresponds to ALPASS_GENERALVU_SOURCE_POS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGeneralVUSourcePos() => new Vector2(7, 23);
        /// <remarks>Corresponds to ALPASS_MEDIASTATE in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassMediaState() => new Vector2(5, 22);

        /// <remarks>Corresponds to ALPASS_CCINTERNAL in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassCcInternal() => new Vector2(12, 22);
        /// <remarks>Corresponds to ALPASS_CCCOLORS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassCcColors() => new Vector2(25, 22);
        /// <remarks>Corresponds to ALPASS_CCSTRIP in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassCcStrip() => new Vector2(0, 24);
        /// <remarks>Corresponds to ALPASS_CCLIGHTS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassCcLights() => new Vector2(0, 25);
        /// <remarks>Corresponds to ALPASS_AUTOCORRELATOR in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassAutoCorrelator() => new Vector2(0, 27);
        /// <remarks>Corresponds to ALPASS_FILTEREDAUDIOLINK in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassFilteredAudioLink() => new Vector2(0, 28);
        /// <remarks>Corresponds to ALPASS_CHRONOTENSITY in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassChronotensity() => new Vector2(16, 28);
        /// <remarks>Corresponds to ALPASS_FILTEREDVU in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassFilteredVU() => new Vector2(24, 28);
        /// <remarks>Corresponds to ALPASS_FILTEREDVU_INTENSITY in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassFilteredVUIntensity() => new Vector2(24, 28);
        /// <remarks>Corresponds to ALPASS_FILTEREDVU_MARKER in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassFilteredVUMarker() => new Vector2(24, 29);
        /// <remarks>Corresponds to ALPASS_GLOBAL_STRINGS in AudioLink.cginc.</remarks>
        public static Vector2 GetALPassGlobalStrings() => new Vector2(40, 28);
        #endregion

        #region Deprecated
        [Obsolete("Use the AudioLink.ALPassDft() function instead.")] public static Vector2 ALPassDft => new Vector2(0, 4);
        [Obsolete("Use the AudioLink.ALPassWaveform() function instead.")] public static Vector2 ALPassWaveform => new Vector2(0, 6);
        [Obsolete("Use the AudioLink.ALPassAudioLink() function instead.")] public static Vector2 ALPassAudioLink => new Vector2(0, 0);
        [Obsolete("Use the AudioLink.ALPassAudioBass() function instead.")] public static Vector2 ALPassAudioBass => new Vector2(0, 0);
        [Obsolete("Use the AudioLink.ALPassAudioLowMids() function instead.")] public static Vector2 ALPassAudioLowMids => new Vector2(0, 1);
        [Obsolete("Use the AudioLink.ALPassAudioHighMids() function instead.")] public static Vector2 ALPassAudioHighMids => new Vector2(0, 2);
        [Obsolete("Use the AudioLink.ALPassAudioTreble() function instead.")] public static Vector2 ALPassAudioTreble => new Vector2(0, 3);
        [Obsolete("Use the AudioLink.ALPassAudioLinkHistory() function instead.")] public static Vector2 ALPassAudioLinkHistory => new Vector2(1, 0);
        [Obsolete("Use the AudioLink.ALPassGeneralVU() function instead.")] public static Vector2 ALPassGeneralVU => new Vector2(0, 22);
        [Obsolete("Use the AudioLink.ALPassGeneralVUInstanceTime() function instead.")] public static Vector2 ALPassGeneralVUInstanceTime => new Vector2(2, 22);
        [Obsolete("Use the AudioLink.ALPassGeneralVULocalTime() function instead.")] public static Vector2 ALPassGeneralVULocalTime => new Vector2(3, 22);
        [Obsolete("Use the AudioLink.ALPassGeneralVUNetworkTime() function instead.")] public static Vector2 ALPassGeneralVUNetworkTime => new Vector2(4, 22);
        [Obsolete("Use the AudioLink.ALPassGeneralVUPlayerInfo() function instead.")] public static Vector2 ALPassGeneralVUPlayerInfo => new Vector2(6, 22);
        [Obsolete("Use the AudioLink.ALPassThemeColor0() function instead.")] public static Vector2 ALPassThemeColor0 => new Vector2(0, 23);
        [Obsolete("Use the AudioLink.ALPassThemeColor1() function instead.")] public static Vector2 ALPassThemeColor1 => new Vector2(1, 23);
        [Obsolete("Use the AudioLink.ALPassThemeColor2() function instead.")] public static Vector2 ALPassThemeColor2 => new Vector2(2, 23);
        [Obsolete("Use the AudioLink.ALPassThemeColor3() function instead.")] public static Vector2 ALPassThemeColor3 => new Vector2(3, 23);
        [Obsolete("Use the AudioLink.ALPassGeneralVUUnixDays() function instead.")] public static Vector2 ALPassGeneralVUUnixDays => new Vector2(5, 23);
        [Obsolete("Use the AudioLink.ALPassGeneralVUUnixSeconds() function instead.")] public static Vector2 ALPassGeneralVUUnixSeconds => new Vector2(6, 23);
        [Obsolete("Use the AudioLink.ALPassGeneralVUSourcePos() function instead.")] public static Vector2 ALPassGeneralVUSourcePos => new Vector2(7, 23);
        [Obsolete("Use the AudioLink.ALPassMediaState() function instead.")] public static Vector2 ALPassMediaState => new Vector2(5, 22);
        [Obsolete("Use the AudioLink.ALPassCcInternal() function instead.")] public static Vector2 ALPassCcInternal => new Vector2(12, 22);
        [Obsolete("Use the AudioLink.ALPassCcColors() function instead.")] public static Vector2 ALPassCcColors => new Vector2(25, 22);
        [Obsolete("Use the AudioLink.ALPassCcStrip() function instead.")] public static Vector2 ALPassCcStrip => new Vector2(0, 24);
        [Obsolete("Use the AudioLink.ALPassCcLights() function instead.")] public static Vector2 ALPassCcLights => new Vector2(0, 25);
        [Obsolete("Use the AudioLink.ALPassAutoCorrelator() function instead.")] public static Vector2 ALPassAutoCorrelator => new Vector2(0, 27);
        [Obsolete("Use the AudioLink.ALPassFilteredAudioLink() function instead.")] public static Vector2 ALPassFilteredAudioLink => new Vector2(0, 28);
        [Obsolete("Use the AudioLink.ALPassChronotensity() function instead.")] public static Vector2 ALPassChronotensity => new Vector2(16, 28);
        [Obsolete("Use the AudioLink.ALPassFilteredVU() function instead.")] public static Vector2 ALPassFilteredVU => new Vector2(24, 28);
        [Obsolete("Use the AudioLink.ALPassFilteredVUIntensity() function instead.")] public static Vector2 ALPassFilteredVUIntensity => new Vector2(24, 28);
        [Obsolete("Use the AudioLink.ALPassFilteredVUMarker() function instead.")] public static Vector2 ALPassFilteredVUMarker => new Vector2(24, 29);
        [Obsolete("Use the AudioLink.ALPassGlobalStrings() function instead.")] public static Vector2 ALPassGlobalStrings => new Vector2(40, 28);
        #endregion

        #region Constants
        /// <remarks>Corresponds to AUDIOLINK_EXPBINS in AudioLink.cginc.</remarks>
        public const int AudioLinkExpBins = 24;
        /// <remarks>Corresponds to AUDIOLINK_EXPOCT in AudioLink.cginc.</remarks>
        public const int AudioLinkExpOct = 10;
        /// <remarks>Corresponds to AUDIOLINK_ETOTALBINS in AudioLink.cginc.</remarks>
        public const int AudioLinkETotalBins = (AudioLinkExpBins * AudioLinkExpOct);
        /// <remarks>Corresponds to AUDIOLINK_WIDTH in AudioLink.cginc.</remarks>
        public const int AudioLinkWidth = 128;
        public const int AudioLinkHeight = 64;
        /// <remarks>Corresponds to AUDIOLINK_SPS in AudioLink.cginc.</remarks>
        public const int AudioLinkSps = 48000;
        /// <remarks>Corresponds to AUDIOLINK_ROOTNOTE in AudioLink.cginc.</remarks>
        public const int AudioLinkRootNote = 0;
        /// <remarks>Corresponds to AUDIOLINK_4BAND_FREQFLOOR in AudioLink.cginc.</remarks>
        public const float AudioLink4BandFreqFloor = 0.123f;
        /// <remarks>Corresponds to AUDIOLINK_4BAND_FREQCEILING in AudioLink.cginc.</remarks>
        public const int AudioLink4BandFreqCeiling = 1;
        /// <remarks>Corresponds to AUDIOLINK_BOTTOM_FREQUENCY in AudioLink.cginc.</remarks>
        public const float AudioLinkBottomFrequency = 13.75f;
        /// <remarks>Corresponds to AUDIOLINK_BASE_AMPLITUDE in AudioLink.cginc.</remarks>
        public const float AudioLinkBaseAmplitude = 2.5f;
        /// <remarks>Corresponds to AUDIOLINK_DELAY_COEFFICIENT_MIN in AudioLink.cginc.</remarks>
        public const float AudioLinkDelayCoefficientMin = 0.3f;
        /// <remarks>Corresponds to AUDIOLINK_DELAY_COEFFICIENT_MAX in AudioLink.cginc.</remarks>
        public const float AudioLinkDelayCoefficientMax = 0.9f;
        /// <remarks>Corresponds to AUDIOLINK_DFT_Q in AudioLink.cginc.</remarks>
        public const float AudioLinkDftQ = 4.0f;
        /// <remarks>Corresponds to AUDIOLINK_TREBLE_CORRECTION in AudioLink.cginc.</remarks>
        public const float AudioLinkTrebleCorrection = 5.0f;
        /// <remarks>Corresponds to AUDIOLINK_4BAND_TARGET_RATE in AudioLink.cginc.</remarks>
        public const float AudioLink4BandTargetRate = 90.0f;

        /// <remarks>Corresponds to COLORCHORD_EMAXBIN in AudioLink.cginc.</remarks>
        public const int ColorChordEMaxBin = 192;
        /// <remarks>Corresponds to COLORCHORD_NOTE_CLOSEST in AudioLink.cginc.</remarks>
        public const float ColorChordNoteClosest = 3.0f;
        /// <remarks>Corresponds to COLORCHORD_NEW_NOTE_GAIN in AudioLink.cginc.</remarks>
        public const float ColorChordNewNoteGain = 8.0f;
        /// <remarks>Corresponds to COLORCHORD_MAX_NOTES in AudioLink.cginc.</remarks>
        public const int ColorChordMaxNotes = 10;
        #endregion

        #region API
        /// <summary>
        /// Check if AudioLink data is available to Udon. If this is returning false,
        /// you may need to enable readbacks on the AudioLink prefab. 
        /// </summary>
        /// <remarks>Corresponds to AudioLinkIsAvailable() in AudioLink.cginc.</remarks>
        public bool AudioDataIsAvailable()
        {
            return audioDataToggle && audioData != null && audioData.Length > 0;
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture.
        /// </summary>
        /// <param name="x">The x coordinate of the pixel to read.</param>
        /// <param name="y">The y coordinate of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns>
        /// <remarks>Corresponds to AudioLinkData() in AudioLink.cginc.</remarks>
        public Vector4 GetDataAtPixel(int x, int y)
        {
            return audioData[y * AudioLinkWidth + x];
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns> 
        /// <remarks>Corresponds to AudioLinkData() in AudioLink.cginc.</remarks>
        public Vector4 GetDataAtPixel(Vector2 position)
        {
            return GetDataAtPixel((int)position.x, (int)position.y);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, interpolating values when given fractional positions.
        /// </summary>
        /// <param name="x">The x coordinate of the pixel to read.</param>
        /// <param name="y">The y coordinate of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns> 
        /// <remarks>Corresponds to AudioLinkLerp() in AudioLink.cginc.</remarks>
        public Vector4 LerpAudioDataAtPixel(float x, float y)
        {
            return Vector4.Lerp(GetDataAtPixel((int)x, (int)y), GetDataAtPixel((int)x + 1, (int)y), x % 1.0f);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, interpolating values when given fractional positions.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns>
        /// <remarks>Corresponds to AudioLinkLerp() in AudioLink.cginc.</remarks>
        public Vector4 LerpAudioDataAtPixel(Vector2 position)
        {
            return LerpAudioDataAtPixel(position.x, position.y);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, wrapping around to the next row if the index is out of bounds.
        /// </summary>
        /// <param name="x">The x coordinate of the pixel to read.</param>
        /// <param name="y">The y coordinate of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns> 
        /// <remarks>Corresponds to AudioLinkDataMultiline() in AudioLink.cginc.</remarks>
        public Vector4 GetDataAtPixelMultiline(int x, int y)
        {
            return GetDataAtPixel(x % AudioLinkWidth, y + x / AudioLinkWidth);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, wrapping around to the next row if the index is out of bounds.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns>
        /// <remarks>Corresponds to AudioLinkDataMultiline() in AudioLink.cginc.</remarks>
        public Vector4 GetDataAtPixelMultiline(Vector2 position)
        {
            return GetDataAtPixelMultiline((int)position.x, (int)position.y);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, wrapping around to the next row if the index is out of bounds,
        /// and interpolating values when given fractional positions.
        /// </summary>
        /// <param name="x">The x coordinate of the pixel to read.</param>
        /// <param name="y">The y coordinate of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns>
        /// <remarks>Corresponds to AudioLinkLerpMultiline() in AudioLink.cginc.</remarks>
        public Vector4 LerpAudioDataAtPixelMultiline(float x, float y)
        {
            return Vector4.Lerp(GetDataAtPixelMultiline((int)x, (int)y), GetDataAtPixelMultiline((int)x + 1, (int)y), x % 1.0f);
        }

        /// <summary>
        /// Read a pixel from the AudioLink texture, wrapping around to the next row if the index is out of bounds,
        /// and interpolating values when given fractional positions.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates.</returns>
        public Vector4 LerpAudioDataAtPixelMultiline(Vector2 position)
        {
            return LerpAudioDataAtPixelMultiline(position.x, position.y);
        }

        /// <summary>
        /// Decode a pixel in the AudioLink texture as an unsigned integer.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates, decoded as an unsigned integer.</returns>
        /// <remarks>Corresponds to AudioLinkDecodeDataAsUInt() in AudioLink.cginc.</remarks>
        public uint DecodeDataAsUInt(Vector2 position)
        {
            Vector4 rpx = GetDataAtPixel(position);
            return (uint)rpx.x + (uint)rpx.y * 1024 + (uint)rpx.z * 1048576 + (uint)rpx.w * 1073741824;
        }

        /// <summary>
        /// Decode a pixel in the AudioLink texture as a float representing seconds.
        /// This will truncate time to every 134,217.728 seconds (~1.5 days of an instance being up) to prevent floating point aliasing.
        /// if your code will alias sooner, you will need to use a different function. It should be safe to use this on all times.
        /// </summary>
        /// <param name="position">The coordinates of the pixel to read.</param>
        /// <returns>The value of the pixel at the given coordinates, decoded as a float representing seconds.</returns>
        /// <remarks>Corresponds to AudioLinkDecodeDataAsSeconds() in AudioLink.cginc.</remarks>
        public float DecodeDataAsSeconds(Vector2 position)
        {
            int time = (int)DecodeDataAsUInt(position) & 0x7ffffff;
            return (float)(time / 1000) + (float)(time % 1000) / 1000.0f;
        }

        /// <summary>
        /// Sample the amplitude of a given frequency in the DFT, supports frequencies in [13.75; 14080].
        /// </summary>
        /// <param name="hertz">The frequency to sample in range [13.75; 14080].</param>
        /// <returns>The amplitude of the given frequency.</returns>
        /// <remarks>Corresponds to AudioLinkGetAmplitudeAtFrequency() in AudioLink.cginc.</remarks> 
        public Vector4 GetAmplitudeAtFrequency(float hertz)
        {
            float note = AudioLinkExpBins * Mathf.Log(hertz / AudioLinkBottomFrequency, 2);
            return LerpAudioDataAtPixelMultiline(GetALPassDft() + new Vector2(note, 0));
        }

        /// <summary>
        /// Sample the amplitude of a given quartertone in an octave. Octave is in [0; 9] while quarter is [0; 23].
        /// </summary>
        /// <param name="octave">The octave to sample in range [0; 9].</param>
        /// <param name="quarter">The quartertone to sample in range [0; 23].</param>
        /// <returns>The amplitude of the given quartertone.</returns>
        /// <remarks>Corresponds to AudioLinkGetAmplitudeAtQuarterNote() in AudioLink.cginc.</remarks>
        public Vector4 GetAmplitudeAtQuarterNote(float octave, float quarter)
        {
            return LerpAudioDataAtPixelMultiline(GetALPassDft() + new Vector2(octave * AudioLinkExpBins + quarter, 0));
        }

        /// <summary>
        /// Sample the amplitude of a given semitone in an octave. Octave is in [0; 9] while note is [0; 11].
        /// </summary>
        /// <param name="octave">The octave to sample in range [0; 9].</param>
        /// <param name="note">The semitone to sample in range [0; 11].</param>
        /// <returns>The amplitude of the given semitone.</returns>
        /// <remarks>Corresponds to AudioLinkGetAmplitudeAtNote() in AudioLink.cginc.</remarks>
        public Vector4 GetAmplitudeAtNote(float octave, float note)
        {
            float quarter = note * 2.0f;
            return GetAmplitudeAtQuarterNote(octave, quarter);
        }

        /// <summary>
        /// Sample the amplitude of a given quartertone across all octaves. Quarter is [0; 23].
        /// </summary>
        /// <param name="quarter">The quartertone to sample in range [0; 23].</param>
        /// <returns>The amplitude of the given quartertone across all octaves.</returns>
        /// <remarks>Corresponds to AudioLinkGetAmplitudesAtQuarterNote() in AudioLink.cginc.</remarks>
        public Vector4 GetAmplitudesAtQuarterNote(float quarter)
        {
            Vector4 amplitude = Color.clear;
            for (int i = 0; i < AudioLinkExpOct; i++)
            {
                amplitude += GetAmplitudeAtQuarterNote(i, quarter);
            }
            return amplitude;
        }

        /// <summary>
        /// Sample the amplitude of a given semitone across all octaves. Note is [0; 11].
        /// </summary>
        /// <param name="note">The semitone to sample in range [0; 11].</param>
        /// <returns>The amplitude of the given semitone across all octaves.</returns>
        /// <remarks>Corresponds to AudioLinkGetAmplitudesAtNote() in AudioLink.cginc.</remarks>
        public Vector4 GetAmplitudesAtNote(float note)
        {
            float quarter = note * 2.0f;
            return GetAmplitudesAtQuarterNote(quarter);
        }

        /// <summary>
        /// Get a reasonable drop-in replacement time value for _Time.y with the
        /// given chronotensity index [0; 7] and AudioLink band [0; 3].
        /// </summary>
        /// <param name="band">The AudioLink band to sample in range [0; 3].</param>
        /// <param name="index">The chronotensity index to sample in range [0; 7].</param>
        /// <returns>A reasonable drop-in replacement time value for _Time.y.</returns>
        /// <remarks>Corresponds to AudioLinkGetChronoTime() in AudioLink.cginc.</remarks>
        public float GetChronoTime(uint index, uint band)
        {
            return (DecodeDataAsUInt(GetALPassChronotensity() + new Vector2((int)index, (int)band))) / 100000.0f;
        }

        /// <summary>
        /// Get a chronotensity value in the interval [0; 1], modulated by the speed input, 
        /// with the given chronotensity index [0; 7] and AudioLink band [0; 3].
        /// </summary>
        /// <param name="band">The AudioLink band to sample in range [0; 3].</param>
        /// <param name="index">The chronotensity index to sample in range [0; 7].</param>
        /// <param name="speed">The speed to modulate the chronotensity value by.</param>
        /// <returns>A chronotensity value in the interval [0; 1].</returns>
        /// <remarks>Corresponds to AudioLinkGetChronoTime() in AudioLink.cginc.</remarks>
        public float GetChronoTimeNormalized(uint index, uint band, float speed)
        {
            return (GetChronoTime(index, band) * speed) % 1.0f;
        }

        /// <summary>
        /// Get a chronotensity value in the interval [0; interval], modulated by the speed input,
        /// with the given chronotensity index [0; 7] and AudioLink band [0; 3].
        /// </summary>
        /// <param name="band">The AudioLink band to sample in range [0; 3].</param>
        /// <param name="index">The chronotensity index to sample in range [0; 7].</param>
        /// <param name="speed">The speed to modulate the chronotensity value by.</param>
        /// <param name="interval">The interval to modulate the chronotensity value by.</param>
        /// <returns>A chronotensity value in the interval [0; interval].</returns>
        /// <remarks>Corresponds to AudioLinkGetChronoTimeInterval() in AudioLink.cginc.</remarks>
        public float GetChronoTimeInterval(uint index, uint band, float speed, float interval)
        {
            return GetChronoTimeNormalized(index, band, speed) * interval;
        }

        /// <summary>
        /// Get the first synced custom string.
        /// </summary>
        /// <returns>The first synced custom string.</returns>
        /// <remarks>Corresponds to AudioLinkGetCustomString1Char() in AudioLink.cginc.</remarks>
        public string GetCustomString1()
        {
            return customString1;
        }

        /// <summary>
        /// Get the second synced custom string.
        /// </summary>
        /// <returns>The second synced custom string.</returns>
        /// <remarks>Corresponds to AudioLinkGetCustomString1Char() in AudioLink.cginc.</remarks>
        public string GetCustomString2()
        {
            return customString2;
        }
        #endregion
    }
}
