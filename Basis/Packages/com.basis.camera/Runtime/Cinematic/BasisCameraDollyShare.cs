using UnityEngine;
using static SerializableBasis;

namespace Basis.Cinematics
{
    /// <summary>
    /// Hands a saved dolly track to the room.
    ///
    /// <para>A track is a shape somebody drew, and the whole of it fits in the share message: at
    /// most 32 points against an anchor, which is a few kilobytes of JSON. So this rides the
    /// content share rail as an inline payload rather than as a bundle, and there is nothing to
    /// build, nothing to upload and nothing to host. Sharing writes the same record the export
    /// folder already trades in, which is why receiving is <see cref="BasisCameraDollyPresets.Adopt"/>
    /// and not a second import path.</para>
    ///
    /// <para>Distinct from <see cref="BasisCameraDollyManager"/>, which mirrors the track you are
    /// laying out right now so people can watch and help. That one lives only as long as the
    /// session; this one is a thing the receiver keeps.</para>
    /// </summary>
    public static class BasisCameraDollyShare
    {
        /// <summary>The orb colour: the dolly marker's own blue, so a track share reads as camera work.</summary>
        private static readonly Color OrbColor = new Color(0.35f, 0.75f, 1f, 1f);

        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            BasisContentSharePayloadRegistry.Register(new BasisContentSharePayloadKind
            {
                Type = ContentShareType.DollyTrack,
                Name = "Dolly Track",
                Color = OrbColor,
                ShareableKind = BasisShareableKind.DollyTrack,
                Describe = Describe,
                Accept = Accept,
            });
        }

        /// <summary>
        /// Offers a saved track to the room. The player places the orb; anyone who takes it keeps
        /// the track. Returns false when there was nothing worth sending, so the panel can say so.
        /// </summary>
        public static bool Share(BasisCameraDollyPreset preset, out string error)
        {
            error = null;

            if (preset == null || preset.Count == 0)
            {
                error = "camera.dollyPreset.error.noPoints";
                return false;
            }

            string payload = JsonUtility.ToJson(preset);
            if (string.IsNullOrEmpty(payload) || payload.Length > ContentSharePayload.MaxLength)
            {
                error = "camera.dollyPreset.error.shareTooLarge";
                return false;
            }

            Register();
            BasisContentShareManager.ShareInlinePayload(payload, ContentShareType.DollyTrack);
            return true;
        }

        /// <summary>
        /// The name to show on the orb. Runs on a string another client wrote, so a payload that is
        /// not a track at all reads as no name rather than as a failure.
        /// </summary>
        private static string Describe(string payload)
        {
            BasisCameraDollyPreset preset = Parse(payload);
            return preset == null ? null : BasisCameraDollyPreset.SanitizeName(preset.name);
        }

        private static string Accept(string payload)
        {
            BasisCameraDollyPreset preset = Parse(payload);
            if (preset == null) return null;

            return BasisCameraDollyPresets.Adopt(preset, out string storedName, out string error)
                ? storedName
                : LogRefusal(error);
        }

        private static string LogRefusal(string error)
        {
            BasisDebug.LogWarning($"A shared dolly track was refused: {error}");
            return null;
        }

        /// <summary>
        /// Reads a payload back into a track. Everything past this point treats the record as
        /// repaired, so this is the one place a malformed share has to stop.
        /// </summary>
        private static BasisCameraDollyPreset Parse(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length > ContentSharePayload.MaxLength) return null;

            BasisCameraDollyPreset preset;
            try
            {
                preset = JsonUtility.FromJson<BasisCameraDollyPreset>(payload);
            }
            catch (System.Exception ex)
            {
                BasisDebug.LogWarning($"A shared dolly track could not be read: {ex.Message}");
                return null;
            }

            if (preset == null) return null;

            BasisCameraDollyPresets.Repair(preset);
            return preset.Count == 0 ? null : preset;
        }
    }
}
