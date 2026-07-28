using Basis.BasisUI;
using UnityEngine;

namespace Basis.Scripts.Audio
{
    public static class BasisUISounds
    {
        private static BasisSoundPack _pack;
        private static bool _resolved;

        public static BasisSoundPack Pack
        {
            get
            {
                if (!_resolved)
                {
                    _pack = Resources.Load<BasisSoundPack>("BasisSoundPack");
                    _resolved = true;
                }
                return _pack;
            }
        }

        public static void Reload()
        {
            _resolved = false;
            _pack = null;
        }

        public static bool IsEnabled(BasisUISoundEvent soundEvent)
        {
            switch (soundEvent)
            {
                case BasisUISoundEvent.Hover: return BasisSettingsDefaults.SoundHover.RawValue;
                case BasisUISoundEvent.Press: return BasisSettingsDefaults.SoundPress.RawValue;
                case BasisUISoundEvent.Grab: return BasisSettingsDefaults.SoundGrab.RawValue;
                case BasisUISoundEvent.Chat: return BasisSettingsDefaults.SoundChat.RawValue;
                case BasisUISoundEvent.MicMute:
                case BasisUISoundEvent.MicUnmute: return BasisSettingsDefaults.SoundMicrophone.RawValue;
                case BasisUISoundEvent.CameraShutter:
                case BasisUISoundEvent.CameraCountdownTick: return BasisSettingsDefaults.SoundCamera.RawValue;
                default: return true;
            }
        }

        public static AudioClip Resolve(BasisUISoundEvent soundEvent, AudioClip fallback)
        {
            BasisSoundPack pack = Pack;
            if (pack != null)
            {
                AudioClip clip = pack.Get(soundEvent);
                if (clip != null)
                {
                    return clip;
                }
            }
            return fallback;
        }

        public static void PlayAt(BasisUISoundEvent soundEvent, AudioClip fallback, Vector3 position, float volume)
        {
            if (!IsEnabled(soundEvent))
            {
                return;
            }
            AudioClip clip = Resolve(soundEvent, fallback);
            if (clip != null)
            {
                AudioSource.PlayClipAtPoint(clip, position, volume);
            }
        }
    }
}
