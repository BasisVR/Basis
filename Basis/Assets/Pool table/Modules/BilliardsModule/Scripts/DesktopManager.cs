
using Basis.Scripts.Networking.NetworkedAvatar;
using Metaphira.Modules.CameraOverride;

using UnityEngine;
using UnityEngine.InputSystem;



public class DesktopManager : MonoBehaviour
{
    private const int CAMERA_RENDER_MODE_DISABLED = CameraOverrideModule.RENDER_MODE_DISABLED;
    private const int CAMERA_RENDER_MODE_DESKTOP = CameraOverrideModule.RENDER_MODE_DESKTOP;

    private const float k_BALL_RADIUS = 0.03f;
    private const float CURSOR_SPEED = 0.035f;
    private float MAX_SPIN_MAGNITUDE = 0.90f;

    [SerializeField] private GameObject root;
    [SerializeField] private GameObject cursorIndicator;
    [SerializeField] private GameObject spinIndicator;
    [SerializeField] private GameObject jumpIndicator;
    [SerializeField] private GameObject powerIndicator;
    [SerializeField] private GameObject pressE;

    private BilliardsModule table;

    private bool isDesktopUser;

    private bool canShoot;

    private bool holdingCue;
    private bool inUI;
    private bool repositionMode;

    private bool isShooting;
    private bool isRepositioning;
    private Repositioner currentRepositioner;

    private Vector3 initialShotDirection;
    private float initialPower;
    Vector3 initialCursorPosition;

    private Vector3 spin;
    private float jumpAngle;
    private float power;

    private Vector3 cursor;
    private float cursorClampX;
    private float cursorClampZ;

    private Vector3 rootStartScale;
    private float cameraStartScale;

    public void _Init(BilliardsModule table_)
    {
        table = table_;
        cursorClampX = table.k_TABLE_WIDTH + .3f;
        cursorClampZ = table.k_TABLE_HEIGHT + .3f;
        rootStartScale = root.transform.localScale;
        cameraStartScale = root.GetComponentInChildren<Camera>().orthographicSize;
        _RefreshTable();
        _RefreshPhysics();
    }

    public void _OnGameStarted()
    {
        // maybe vrchat lets people switch between pc and vr in the future idk
        isDesktopUser = !BasisNetworkPlayer.LocalPlayer.IsUserInVR();
    }

    public void _OnPickupCue()
    {
        holdingCue = true;
    }

    public void _OnDropCue()
    {
        holdingCue = false;
        exitUI();
    }

    public void _RefreshPhysics()
    {
        MAX_SPIN_MAGNITUDE = table.currentPhysicsManager.CueMaxHitRadius;
    }

    public void _RefreshTable()
    {
        Camera desktopCamera = root.GetComponentInChildren<Camera>();
        Vector3 campos = desktopCamera.transform.position;
        float SF = table.tableModels[table.tableModelLocal].DesktopUIScaleFactor;
        desktopCamera.orthographicSize = cameraStartScale * SF;
        root.transform.localScale = rootStartScale * SF;
        desktopCamera.transform.position = campos; // don't change camera position with it's parent's scale(root)
    }

    public void _Tick()
    {
        if (!isDesktopUser) return;

        if (BasisNetworkPlayer.LocalPlayer == null) return;

        BasisNetworkPlayer.LocalPlayer.GetTrackingData( Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.Head,out var Tracking);
        Vector3 basePosition = Tracking.position + Tracking.rotation * Vector3.forward;

        pressE.transform.position = basePosition;

        Vector3 playerPos = table.transform.InverseTransformPoint(BasisNetworkPlayer.LocalPlayer.GetPosition());
        bool canUseUI = (Mathf.Abs(playerPos.x) < 3.5f) && (Mathf.Abs(playerPos.z) < 2.5f);

        pressE.SetActive(holdingCue && canUseUI);

        if (!holdingCue)
        {
            if (inUI) exitUI();

            return;
        }

        if (canUseUI)
        {
            if (Keyboard.current[Key.E].wasPressedThisFrame)
            {
                if (!inUI)
                {
                    enterUI();
                }
                else
                {
                    exitUI();
                }
            }
        }

        if (inUI)
        {
            tickUI();
        }
    }

    private void tickUI()
    {
      //  bool clickNow = Input.GetKeyDown(KeyCode.Mouse0);
      //  bool click = Input.GetKey(KeyCode.Mouse0);

        bool clickNow = Mouse.current.leftButton.wasPressedThisFrame;
        bool click = Mouse.current.leftButton.isPressed;

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        cursor.x = Mathf.Clamp(cursor.x + mouseDelta.x * CURSOR_SPEED, -cursorClampX, cursorClampX);
        cursor.y = 2.0f; // fixed height to see it on the table
        cursor.z = Mathf.Clamp(cursor.z + mouseDelta.y * CURSOR_SPEED, -cursorClampZ, cursorClampZ);

        if (Keyboard.current[Key.Q].wasPressedThisFrame)
        {
            if (!table.isLocalSimulationRunning)
            {
                if (isShooting)
                {
                    power = 0;
                    renderCuePosition(initialShotDirection);
                    stopShooting();
                }
                repositionMode = !repositionMode;
            }
        }

        if (canShoot)
        {
            Vector3 flatCursor = cursor;
            flatCursor.y = 0.0f;

            Vector3 shotDirection = flatCursor - table.ballsP[0];

            if (repositionMode)
            {
                if (!isRepositioning)
                {
                    if (Mouse.current.leftButton.wasPressedThisFrame)
                    {
                        isRepositioning = true;

                        Vector3 localPos = new Vector3(cursor.x, 0, cursor.z);
                        Vector3 worldPos = table.balls[0].transform.parent.TransformPoint(localPos);
                        Collider[] colliders = Physics.OverlapSphere(worldPos, k_BALL_RADIUS / 4f, 1 << 22);
                        foreach (Collider c in colliders)
                        {
                            if (c != null && c.gameObject != null)
                            {
                                Repositioner repositioner = c.gameObject.GetComponent<Repositioner>();
                                if (repositioner != null)
                                {
                                    table.repositionManager._BeginReposition(repositioner);
                                    currentRepositioner = repositioner;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (currentRepositioner != null)
                {
                    Vector3 localPos = new Vector3(cursor.x, 0, cursor.z);
                    Vector3 worldPos = table.balls[0].transform.parent.TransformPoint(localPos);
                    worldPos.y = currentRepositioner.transform.position.y;
                    currentRepositioner.transform.position = worldPos;
                }

                if (!Mouse.current.leftButton.isPressed)
                {
                    isRepositioning = false;
                    stopRepositioning();
                }
            }
            else
            {
                if (Mouse.current.leftButton.isPressed)
                {
                    if (!isShooting)
                    {
                        isShooting = true;

                        initialShotDirection = shotDirection.normalized;
                        initialCursorPosition = cursor;
                        initialPower = Vector3.Dot(initialShotDirection, flatCursor);

                        // unlock cursor
                        cursorIndicator.SetActive(false);
                        cursorClampX = Mathf.Infinity;
                        cursorClampZ = Mathf.Infinity;
                    }

                    power = Mathf.Clamp(initialPower - Vector3.Dot(initialShotDirection, flatCursor), 0.0f, 0.5f);
                    shotDirection = initialShotDirection;
                }
                else
                {
                    // Trigger shot
                    if (isShooting)
                    {
                        // we still keep the same shot direction if we're shooting
                        shotDirection = initialShotDirection;

                        if (power > 0)
                        {
                            float vel = Mathf.Pow(power * 2.0f, 1.4f) * 4.0f;
                            table.currentPhysicsManager.inV0 = vel;
                            table.currentPhysicsManager._ApplyPhysics();

                            table._TriggerCueBallHit();

                            // shot was successful, reset some state
                            _DenyShoot();
                        }

                        stopShooting();
                    }
                }

                renderCuePosition(shotDirection);
                updateSpinIndicator();
                updateJumpIndicator();
            }
        }

        cursorIndicator.transform.localPosition = cursor * (1 / table.tableModels[table.tableModelLocal].DesktopUIScaleFactor);
        powerIndicator.transform.localScale = new Vector3(1.0f - (power * 2.0f), 1.0f, 1.0f);

        var kb = Keyboard.current;

        bool hitCtrlNow = kb[Key.LeftCtrl].wasPressedThisFrame || kb[Key.RightCtrl].wasPressedThisFrame;
        bool hitCtrl = kb[Key.LeftCtrl].isPressed || kb[Key.RightCtrl].isPressed;

        bool hitZNow = kb[Key.Z].wasPressedThisFrame;
        bool hitZ = kb[Key.Z].isPressed;

        bool hitXNow = kb[Key.X].wasPressedThisFrame;
        bool hitX = kb[Key.X].isPressed;

        if ((hitCtrlNow && hitZ) || (hitCtrl && hitZNow))
        {
            table.practiceManager._Undo();
        }
        else if ((hitCtrlNow && hitX) || (hitCtrl && hitXNow))
        {
            table.practiceManager._Redo();
        }
    }

    private void updateSpinIndicator()
    {
        var kb = Keyboard.current;

        // Check if LeftShift is NOT pressed and W is pressed
        if (!kb[Key.LeftShift].isPressed && kb[Key.W].isPressed)
        {
            spin += Vector3.forward * Time.deltaTime;
        }
        if (!kb[Key.LeftShift].isPressed && kb[Key.S].isPressed)
        {
            spin += Vector3.back * Time.deltaTime;
        }
        if (kb[Key.A].isPressed)
        {
            spin += Vector3.left * Time.deltaTime;
        }
        if (kb[Key.D].isPressed)
        {
            spin += Vector3.right * Time.deltaTime;
        }

        if (spin.magnitude > MAX_SPIN_MAGNITUDE)
        {
            spin = spin.normalized * MAX_SPIN_MAGNITUDE;
        }

        spinIndicator.transform.localPosition = spin;
    }

    private void updateJumpIndicator()
    {
        var kb = Keyboard.current;

        if (kb[Key.LeftShift].isPressed && kb[Key.W].isPressed)
        {
            jumpAngle += Time.deltaTime;
        }
        if (kb[Key.LeftShift].isPressed && kb[Key.S].isPressed)
        {
            jumpAngle -= Time.deltaTime;
        }

        jumpAngle = Mathf.Clamp(jumpAngle, 0, Mathf.PI / 2);

        jumpIndicator.transform.localPosition = new Vector3(
            -Mathf.Cos(jumpAngle) * 1.1f,
            0,
            Mathf.Sin(jumpAngle) * 1.1f
        );
    }
    private void renderCuePosition(Vector3 dir)
    {
        CueController cue = table.activeCue;
        cue.UpdateDesktopPosition(); // otherwise it spazzes out if FPS > FixedUpdate rate

        float a = spin.x * k_BALL_RADIUS;
        float b = spin.z * k_BALL_RADIUS;
        float c = Mathf.Sqrt(Mathf.Pow(k_BALL_RADIUS, 2) - Mathf.Pow(a, 2) - Mathf.Pow(b, 2));

        float dist = (cue._GetCuetip().transform.position - cue._GetDesktopMarker().transform.position).magnitude;
        dist += power; // show the amount of power being applied
        dist += 0.05f; // add some extra distance so the cue tip isn't touching the ball

        Vector3 ballHitPos = new Vector3(a, b, -c);
        Vector3 cueGripPos = new Vector3(a, b + dist * Mathf.Sin(jumpAngle), -(c + dist * Mathf.Cos(jumpAngle)));

        Quaternion spinRot = Quaternion.AngleAxis(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, Vector3.up);
        Transform tableSurface = table.tableSurface;
        cue._GetDesktopMarker().transform.position = tableSurface.TransformPoint(table.ballsP[0] + (spinRot * cueGripPos));
        cue._GetDesktopMarker().transform.LookAt(tableSurface.TransformPoint(table.ballsP[0] + (spinRot * ballHitPos)));
    }

    private void stopRepositioning()
    {
        isRepositioning = false;
        if (currentRepositioner != null)
        {
            table.repositionManager._EndReposition(currentRepositioner);
            currentRepositioner = null;
        }
    }

    private void enterUI()
    {
        inUI = true;
        repositionMode = false;
        root.SetActive(true);
        BasisNetworkPlayer.LocalPlayer.Immobilize(true);

        Camera desktopCamera = root.GetComponentInChildren<Camera>();
        table.cameraOverrideModule.shouldMaintainAspectRatio = true;
        table.cameraOverrideModule.aspectRatio = new Vector2(1920, 1080);
        table.cameraOverrideModule._SetTargetCamera(desktopCamera);
        table.cameraOverrideModule._SetRenderMode(CAMERA_RENDER_MODE_DESKTOP);

        // table.activeCue.RequestSerialization();

        if (canShoot)
        {
            table._TriggerOnPlayerPrepareShoot();
        }

        cursorClampX = table.k_TABLE_WIDTH + .3f;
        cursorClampZ = table.k_TABLE_HEIGHT + .3f;
    }

    private void exitUI()
    {
        stopShooting();
        stopRepositioning();
        resetShootState();
        inUI = false;
        root.SetActive(false);
        table.cameraOverrideModule._SetRenderMode(CAMERA_RENDER_MODE_DISABLED);
        BasisNetworkPlayer.LocalPlayer.Immobilize(false);
    }

    private void stopShooting()
    {
        isShooting = false;
        cursor = initialCursorPosition;
        cursorIndicator.SetActive(true);
        cursorClampX = table.k_TABLE_WIDTH + .3f;
        cursorClampZ = table.k_TABLE_HEIGHT + .3f;
    }

    private void resetShootState()
    {
        spin = Vector3.zero;
        jumpAngle = 0;
        power = 0;
    }

    public void _AllowShoot()
    {
        canShoot = true;

        if (inUI)
        {
            table._TriggerOnPlayerPrepareShoot();
        }
    }

    public void _DenyShoot()
    {
        canShoot = false;
        resetShootState();
    }

    public bool _IsInUI()
    {
        return inUI;
    }

    public bool _IsShooting()
    {
        return canShoot;
    }
}
