using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using HVR.Basis.Comms;
using UnityEngine;

namespace HVR.Vixxy
{
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy")]
    [AddComponentMenu("HVR.Basis/HVR Vixxy Menu Item")]
    public class HVRVixxyMenuItem : MonoBehaviour, IHVRInitializable
    {
        [SerializeField] [Multiline] internal string title;
        [SerializeField] internal HVRVixxyTitleSelection titleSelection = HVRVixxyTitleSelection.UseObjectName;
        [SerializeField] internal HVRVixxyControlPresentation presentation;
        [SerializeField] internal Texture2D icon;

        [SerializeField] internal HVRVixxyControl control;

        [SerializeField] internal HVRVixxyRememberScope remember = HVRVixxyRememberScope.RememberInThisAvatar;
        [SerializeField] internal string rememberTag = "";

        private float _value;
        private HVRAvatarComms _comms;

        public HVRVixxyControl Control => control;
        public HVRVixxyControlPresentation Presentation => presentation;

        private bool TryResolveActualControl(out HVRVixxyControl result)
        {
            var controlsOnThis = GetComponents<HVRVixxyControl>(); // This may return 0 elements.
            if (controlsOnThis.Length == 1)
            {
                result = controlsOnThis[0];
                return true;
            }

            if (control != null)
            {
                result = control;
                return true;
            }

            result = null;
            return false;
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _comms = HVRCommsUtil.GetComms(this);

            if (!isWearer) return;

            control = TryResolveActualControl(out var actualControl) ? actualControl : null;

            _value = control != null ? control.defaultValue : 0f;

            if (control != null && isActiveAndEnabled) StartCoroutine(RestoreNextFrame());
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            if (!isWearer) return;
        }

        private IEnumerator RestoreNextFrame()
        {
            yield return null;
            if (control == null) yield break;

            var saved = 0f;
            var found = TryResolvePersistenceKey(out var key) && HVRVixxyPersistentStore.TryGet(key, out saved);
            if (found)
            {
                _value = saved;
                var addressId = control.IsInitialized ? control.AddressId : HVRAddress.AddressToId(control.CalculateAddress());
                _comms.VariableStore.SubmitOrDefineDefaultValue(addressId, saved);
            }
        }

        private bool TryResolvePersistenceKey(out string key)
        {
            key = null;
            if (control == null) return false;

            var address = control.IsInitialized ? control.Address : control.CalculateAddress();

            switch (remember)
            {
                case HVRVixxyRememberScope.RememberInThisAvatar:
                    if (string.IsNullOrEmpty(BasisLocalPlayer.CurrentAvatarUniqueID)) return false;
                    key = $"avatar:{BasisLocalPlayer.CurrentAvatarUniqueID}|{address}";
                    return true;
                case HVRVixxyRememberScope.RememberInThisTag:
                    if (string.IsNullOrEmpty(rememberTag)) return false;
                    key = $"tag:{rememberTag}";
                    return true;
                case HVRVixxyRememberScope.RememberAcrossAvatars:
                    key = $"global:{address}";
                    return true;
                default:
                    return false;
            }
        }

        public string ResolveTitle()
        {
            return titleSelection switch
            {
                HVRVixxyTitleSelection.UseObjectName => gameObject.name,
                HVRVixxyTitleSelection.UseCustomTitle => title,
                HVRVixxyTitleSelection.UseCustomTitleAndChoices => title,
                HVRVixxyTitleSelection.UseChoicesOnly => ResolveComplexTitle(),
                _ => title
            };
        }

        public string ResolveDescription()
        {
            return titleSelection switch
            {
                HVRVixxyTitleSelection.UseObjectName => "",
                HVRVixxyTitleSelection.UseCustomTitle => "",
                HVRVixxyTitleSelection.UseCustomTitleAndChoices => ResolveComplexTitle(),
                HVRVixxyTitleSelection.UseChoicesOnly => "",
                _ => title
            };
        }

        private string ResolveComplexTitle()
        {
            if (control == null) return title;

            var choices = control.choices;
            if (choices == null) return title;

            var index = (int)_value;
            if (index < 0 || index >= choices.Length) return title;

            return choices[index].title ?? title;
        }

        public void ApplyValue(float value)
        {
            _value = value;
            SubmitValue();
        }

        public float GetValue()
        {
            return _value;
        }

        private void SubmitValue()
        {
            if (control != null && _comms != null)
            {
                var actualAddress = control.IsInitialized ? control.AddressId : HVRAddress.AddressToId(control.CalculateAddress());
                _comms.VariableStore.SubmitOrDefineDefaultValue(actualAddress, _value);

                if (TryResolvePersistenceKey(out var key))
                {
                    HVRVixxyPersistentStore.Set(key, _value, control.defaultValue);
                }
            }
        }

        public List<Object> ListAssets()
        {
            return icon != null ? new List<Object> { icon } : new List<Object>();
        }
    }
}
