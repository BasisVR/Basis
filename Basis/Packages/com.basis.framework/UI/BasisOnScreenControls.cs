using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;

public class BasisOnScreenControls : MonoBehaviour
{
    public BasisScreenUIJoyStick LeftControl;
    public BasisScreenUIJoyStick RightControl;

    public Canvas LeftUIJoystickCanvas;
    public Canvas RightUIJoystickCanvas;

    public Button Space;
    public Button C;
    public Button Escape;
    public Button V;

    public float DistanceFromCamera = 0.25f; // meters in front of camera (camera-space depth)

    void OnEnable()
    {
        LeftControl.OnStickMove += OnStickMoveLeft;
        RightControl.OnStickMove += OnStickMoveRight;

        Space.onClick.AddListener(OnSpace);
        C.onClick.AddListener(OnC);
        Escape.onClick.AddListener(OnEscape);
        V.onClick.AddListener(OnV);
    }

    void LateUpdate()
    {
        PositionWorldCanvases();
    }

    void OnDisable()
    {
        Space.onClick.RemoveListener(OnSpace);
        C.onClick.RemoveListener(OnC);
        Escape.onClick.RemoveListener(OnEscape);
        V.onClick.RemoveListener(OnV);

        LeftControl.OnStickMove -= OnStickMoveLeft;
        RightControl.OnStickMove -= OnStickMoveRight;
    }
    void PositionWorldCanvases()
    {
        if (!BasisLocalCameraDriver.HasInstance) return;

        var driver = BasisLocalCameraDriver.Instance;
        var cam = driver.Camera;

        // Match your scaling approach.
        float avatarScale = 1f;
        if (driver.LocalPlayer != null)
        {
            avatarScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
        }

        PlaceWorldCanvasAtViewport(driver, cam, LeftUIJoystickCanvas, -0.5f, 0.2f, avatarScale);
        PlaceWorldCanvasAtViewport(driver, cam, RightUIJoystickCanvas, 1.5f, 0.2f, avatarScale);
    }

    void PlaceWorldCanvasAtViewport(
        BasisLocalCameraDriver driver,
        Camera cam,
        Canvas canvas,
        float xPercent,
        float yPercent,
        float avatarScale)
    {

        var t = canvas.transform;

        // 1) Viewport -> world point at a fixed depth from the camera
        Vector3 worldPoint = cam.ViewportToWorldPoint(new Vector3(xPercent, yPercent, DistanceFromCamera));

        // 2) World -> camera-driver local space (same trick as your mic icon code)
        Vector3 localPos = driver.transform.InverseTransformPoint(worldPoint);

        // 3) Apply avatar scale so it stays “correct” when the user changes height/avatar
        t.localPosition = localPos * avatarScale;
    }

    void OnV() => BasisLocalMicrophoneDriver.ToggleIsPaused();
    void OnEscape() => BasisMainMenu.Toggle();
    void OnC() => BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchToggle();
    void OnSpace() => BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJumpRequest();

    void OnStickMoveLeft(Vector2 vector)
    {
        var inst = BasisLocalPlayer.Instance;
        inst.LocalCharacterDriver.SetMovementVector(vector);
        inst.LocalCharacterDriver.UpdateMovementSpeed(BasisLocalInputActions.Instance.IsRunHeld);
    }

    void OnStickMoveRight(Vector2 vector)
    {
        BasisLocalInputActions.Instance.OnLookAction(vector, 10);
    }
}
