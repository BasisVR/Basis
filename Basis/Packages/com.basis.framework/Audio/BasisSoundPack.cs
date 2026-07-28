using UnityEngine;

namespace Basis.Scripts.Audio
{
    public enum BasisUISoundEvent
    {
        Hover,
        Press,
        Grab,
        Chat,
        MicMute,
        MicUnmute,
        CameraShutter,
        CameraCountdownTick,
    }

    [CreateAssetMenu(fileName = "BasisSoundPack", menuName = "Basis/Sound Pack", order = 400)]
    public class BasisSoundPack : ScriptableObject
    {
        public AudioClip Hover;
        public AudioClip Press;
        public AudioClip Grab;
        public AudioClip Chat;
        public AudioClip MicMute;
        public AudioClip MicUnmute;
        public AudioClip CameraShutter;
        public AudioClip CameraCountdownTick;

        public AudioClip Get(BasisUISoundEvent soundEvent)
        {
            switch (soundEvent)
            {
                case BasisUISoundEvent.Hover: return Hover;
                case BasisUISoundEvent.Press: return Press;
                case BasisUISoundEvent.Grab: return Grab;
                case BasisUISoundEvent.Chat: return Chat;
                case BasisUISoundEvent.MicMute: return MicMute;
                case BasisUISoundEvent.MicUnmute: return MicUnmute;
                case BasisUISoundEvent.CameraShutter: return CameraShutter;
                case BasisUISoundEvent.CameraCountdownTick: return CameraCountdownTick;
                default: return null;
            }
        }
    }
}
