using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using UnityEngine.UI;

public class BasisOnScreenControls : MonoBehaviour
{
    public BasisScreenUIJoyStick LeftControl;
    public BasisScreenUIJoyStick RightControl;

    public Button Space;
    public Button C;
    public Button Escape;
    public Button V;
    public void OnEnable()
    {

        LeftControl.OnStickMove += OnStickMoveLeft;

        RightControl.OnStickMove += OnStickMoveRight;

        Space.onClick.AddListener(OnSpace);
        C.onClick.AddListener(OnC);
        Escape.onClick.AddListener(OnEscape);
        V.onClick.AddListener(OnV);
    }
    public void OnDisable()
    {
        Space.onClick.RemoveListener(OnSpace);
        C.onClick.RemoveListener(OnC);
        Escape.onClick.RemoveListener(OnEscape);
        V.onClick.RemoveListener(OnV);

        LeftControl.OnStickMove -= OnStickMoveLeft;
        RightControl.OnStickMove -= OnStickMoveRight;
    }
    private void OnV()
    {
        BasisLocalMicrophoneDriver.ToggleIsPaused();
    }

    private void OnEscape()
    {
        BasisHamburgerMenu.ToggleHamburgerMenu();
    }

    private void OnC()
    {
       BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchToggle();
    }

    private void OnSpace()
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();
    }

    private void OnStickMoveLeft(Vector2 vector)
    {
        BasisLocalPlayer.Instance.LocalCharacterDriver.SetMovementVector(vector);
        BasisLocalPlayer.Instance.LocalCharacterDriver.UpdateMovementSpeed(BasisLocalInputActions.Instance.IsRunHeld);
    }
    private void OnStickMoveRight(Vector2 vector)
    {
        BasisLocalInputActions.Instance.OnLookAction(vector, 10, false);
    }
}
