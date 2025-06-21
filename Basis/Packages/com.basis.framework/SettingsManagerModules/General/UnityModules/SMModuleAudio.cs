using System;
using UnityEngine;
using UnityEngine.Audio;

namespace BattlePhaze.SettingsManager.Intergrations
{
    public class SMModuleAudio : SettingsManagerOption
    {
        public AudioMixer Mixer;
        public AudioMixerGroup WorldDefaultMixer;
        public static SMModuleAudio Instance;
        public void Awake()
        {
           Instance = this;
        }
        /// <summary>
        /// 0 to 1 rest or 0 to 100
        /// </summary>
        public static Action<float> MainVolume;
        public static Action<float> MenusVolume;
        public static Action<float> WorldVolume;
        public static Action<float> PlayerVolume;
        public static float ActiveMainVolume;
        public static float ActiveMenusVolume;
        public static float ActiveWorldVolume;
        public static float ActivePlayerVolume;
        public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
        {
            if (NameReturn(0, Option))
            {
                if (SliderReadOption(Option, Manager, out float Value))
                {
                    ActiveMainVolume = Value / 100;
                    MainVolume?.Invoke(ActiveMainVolume);
                    AudioListener.volume = ActiveMainVolume;
                }
            }
            if (NameReturn(1, Option))
            {
                if (SliderReadOption(Option, Manager, out float Value))
                {
                    ActiveMenusVolume = Value;
                    MenusVolume?.Invoke(ActiveMenusVolume);
                    ChangeVolume(Value - 80, Option.Name);
                }
            }
            if (NameReturn(2, Option))
            {
                if (SliderReadOption(Option, Manager, out float Value))
                {
                    ActiveWorldVolume = Value;
                    WorldVolume?.Invoke(ActiveWorldVolume);
                    ChangeVolume(Value - 80, Option.Name);
                }
            }
            if (NameReturn(3, Option))
            {
                if (SliderReadOption(Option, Manager, out float Value))
                {
                    ActivePlayerVolume = Value;
                    PlayerVolume?.Invoke(ActivePlayerVolume);
                    ChangeVolume(Value - 80, Option.Name);
                }
            }
        }
        public void ChangeVolume(float Value, string Name)
        {
            BasisDebug.Log(Name + "set to" + Value)
                ;
            Mixer.SetFloat(Name, Value);
        }
    }
}
