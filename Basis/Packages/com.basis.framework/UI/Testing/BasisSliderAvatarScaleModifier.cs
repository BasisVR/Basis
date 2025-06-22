using Basis.Scripts.BasisSdk.Players;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
public class BasisSliderAvatarScaleModifier : MonoBehaviour
{
    public Slider Slider;
    public TextMeshProUGUI Text;
    public Button ApplyOn;
    public float SelectedValue = 1.7f;
    public void Start()
    {
        SelectedValue = BasisLocalPlayer.Instance.CurrentHeight.CustomPlayerEyeHeight;
        Slider.value = SelectedValue;
        Slider.onValueChanged.AddListener(SliderChangeEvent);
        ApplyOn.onClick.AddListener(Apply);
        Text.text = $"{SelectedValue}";
    }
    public void SliderChangeEvent(float SliderValue)
    {
        Text.text = $"{SliderValue}";
        SelectedValue = SliderValue;
    }
    public void Apply()
    {
        BasisHeightDriver.SetCustomPlayerHeight(SelectedValue);
    }
}
