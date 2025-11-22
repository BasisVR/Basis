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
        SelectedValue = BasisLocalPlayer.Instance.Height.PlayerEyeHeightMeters;
        Slider.value = SelectedValue;
        Slider.onValueChanged.AddListener(SliderChangeEvent);
        ApplyOn.onClick.AddListener(Apply);
        Text.text = $"{SelectedValue}";
    }
    public void SliderChangeEvent(float SliderValue)
    {
        Text.text = $"{SliderValue}m";
        SelectedValue = SliderValue;
    }
    public void Apply()
    {
        BasisLocalPlayer.Instance.Height.SetPlayerSize(BasisHeightMeasurement.EyeHeightMeters, SelectedValue);
    }
}
