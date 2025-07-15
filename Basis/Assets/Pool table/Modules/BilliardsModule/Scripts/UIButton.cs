using Basis.Scripts.Networking.NetworkedAvatar;
using System;

using UnityEngine;
using UnityEngine.InputSystem;




public class UIButton : MonoBehaviour
{
    private readonly int BUTTON_NORMAL = 0;
    private readonly int BUTTON_TOGGLE = 1;
    private readonly int BUTTON_ONETIME = 2;

    private readonly int VIBRATE_LEFT = (1 << 0);
    private readonly int VIBRATE_RIGHT = (1 << 1);
    private readonly int VIBRATE_BOTH = (1 << 0) | (1 << 1);

    [SerializeField] public Texture2D buttonOff;
    [SerializeField] public Texture2D buttonOn;
    [SerializeField] public Color outlineColor;
    [SerializeField] public int buttonType;

    [SerializeField] public float maxDesktopDistance = 2f;

    [SerializeField] public AudioClip pressSound;
    [SerializeField] public AudioClip releaseSound;

    [SerializeField] public MenuManager callback;

    [NonSerialized] public bool disableInteractions;

    private GameObject visual;
    private GameObject button;
    private GameObject desktopOutline;

    private Vector3 unpressedPosition;
    private Vector3 halfPressedPosition;
    private Vector3 pressedPosition;
    private Vector3 depressedPosition;

    private void Start()
    {
        visual = this.transform.Find("Visual").gameObject;
        button = this.transform.Find("Visual/Button").gameObject;
        desktopOutline = this.transform.Find("Visual/DesktopOutline").gameObject;

        _ResetPosition();

        desktopOutline.SetActive(false);
        desktopOutline.GetComponent<MeshRenderer>().material.SetColor("_Color", outlineColor);
    }

    public void _ResetPosition()
    {
        // can't use button here beacuse it might not have been set yet
        float depth = this.transform.Find("Visual/Button").GetComponent<MeshFilter>().sharedMesh.bounds.size.y * 0.9f;
        float scale = this.transform.localScale.y;

        unpressedPosition = this.transform.Find("Visual").localPosition;
        halfPressedPosition = unpressedPosition - new Vector3(0, depth * 0.55f * scale, 0);
        pressedPosition = unpressedPosition - new Vector3(0, depth * 0.75f * scale, 0);
        depressedPosition = unpressedPosition - new Vector3(0, depth * scale, 0);
    }

    private void Update()
    {
        BasisNetworkPlayer player = BasisNetworkPlayer.LocalPlayer;
        if (player == null) return;

        if (!disableInteractions)
        {
            if (!player.IsUserInVR())
            {
                tickDesktop(player);
            }
            else
            {
                tickVR(player);
            }
        }
    }

    private void tickDesktop(BasisNetworkPlayer player)
    {
      player.GetTrackingData( Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.Head,out Vector3 Position, out Quaternion Rotation);
        RaycastHit hit;

        bool canClick =
            (buttonState == STATE_INACTIVE || buttonState == STATE_DEPRESSED)
            && Physics.Raycast(
                Position,
                Rotation * Vector3.forward,
                out hit,
                maxDesktopDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide
            )
            && hit.collider != null
            && hit.collider.gameObject == button;

        bool isMouseDown = Mouse.current.leftButton.wasPressedThisFrame;
        bool isMousePress = Mouse.current.leftButton.isPressed;

        desktopOutline.SetActive(canClick);

        bool isClicking = false;
        if (canClick)
        {
            if (isMouseDown)
            {
                isClicking = true;
            }
            else if (isMousePress && buttonState == STATE_DEPRESSED)
            {
                isClicking = true;
            }
        }

        if (isClicking)
        {
            if (buttonState == STATE_INACTIVE)
            {
                buttonState = STATE_DEPRESSED;
                visual.transform.localPosition = depressedPosition;
                if (pressSound != null)
                {
                    AudioSource.PlayClipAtPoint(pressSound, this.transform.position, 0.1f);
                }
            }
        }
        else if (canClick)
        {
            if (buttonState == STATE_DEPRESSED)
            {
                if (buttonType == BUTTON_NORMAL)
                {
                    buttonState = STATE_INACTIVE;
                    visual.transform.localPosition = unpressedPosition;
                }
                else if (buttonType == BUTTON_ONETIME)
                {
                    buttonState = STATE_PUSHED;
                    button.GetComponent<MeshRenderer>().material.mainTexture = buttonOn;
                    visual.transform.localPosition = pressedPosition;
                }
                else if (buttonType == BUTTON_TOGGLE)
                {
                    buttonState = STATE_INACTIVE;
                    toggleState = !toggleState;
                    button.GetComponent<MeshRenderer>().material.mainTexture = toggleState ? buttonOn : buttonOff;
                    visual.transform.localPosition = toggleState ? halfPressedPosition : unpressedPosition;
                }

                if (releaseSound != null)
                {
                    AudioSource.PlayClipAtPoint(releaseSound, this.transform.position, 0.1f);
                }

                sendPressedEvent();
            }
        }
        else
        {
            if (buttonState == STATE_DEPRESSED)
            {
                buttonState = STATE_INACTIVE;
                visual.transform.localPosition = unpressedPosition;
            }
        }
    }

    private void tickVR(BasisNetworkPlayer player)
    {
        // reset position, we'll incrementally move it down with each attempt at pressing
        // this.transform.localPosition = unpressedPosition;

        float bestPosition = float.MaxValue;
        int vibration = 0;
        // Vector3 bestPosition = unpressedPosition;
        // bestPosition.y = float.MaxValue;
        Transform transform = this.transform;

        {
            Vector3 bonePosition = transform.InverseTransformPoint(player.GetBonePosition(HumanBodyBones.LeftIndexDistal));
            if (isPositionValid(bonePosition) && bonePosition.y < bestPosition)
            {
                bestPosition = bonePosition.y;
                vibration = VIBRATE_LEFT;
            }
        }
        {
            Vector3 bonePosition = transform.InverseTransformPoint(player.GetBonePosition(HumanBodyBones.RightIndexDistal));
            if (isPositionValid(bonePosition) && bonePosition.y < bestPosition)
            {
                bestPosition = bonePosition.y;
                vibration = VIBRATE_RIGHT;
            }
        }
        {
            Vector3 bonePosition = transform.InverseTransformPoint(player.GetBonePosition(HumanBodyBones.LeftFoot));
            if (isPositionValid(bonePosition) && bonePosition.y < bestPosition)
            {
                bestPosition = bonePosition.y;
                vibration = VIBRATE_LEFT;
            }
        }
        {
            Vector3 bonePosition = transform.InverseTransformPoint(player.GetBonePosition(HumanBodyBones.RightFoot));
            if (isPositionValid(bonePosition) && bonePosition.y < bestPosition)
            {
                bestPosition = bonePosition.y;
                vibration = VIBRATE_RIGHT;
            }
        }
        {
            Vector3 bonePosition = transform.InverseTransformPoint(player.GetBonePosition(HumanBodyBones.Head));
            if (isPositionValid(bonePosition) && bonePosition.y < bestPosition)
            {
                bestPosition = bonePosition.y;
                vibration = VIBRATE_BOTH;
            }
        }
        {
            player.GetTrackingData(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand, out Vector3 Position,out Quaternion Rotation);
            Vector3 trackingPosition = transform.InverseTransformPoint(Position);

            if (isPositionValid(trackingPosition) && trackingPosition.y < bestPosition)
            {
                bestPosition = trackingPosition.y;
                vibration = VIBRATE_LEFT;
            }
        }
        {
            player.GetTrackingData(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand, out Vector3 Position, out Quaternion Rotation);
            Vector3 trackingPosition = transform.InverseTransformPoint(Position);

            if (isPositionValid(trackingPosition) && trackingPosition.y < bestPosition)
            {
                bestPosition = trackingPosition.y;
                vibration = VIBRATE_RIGHT;
            }
        }
        {
            player.GetTrackingData(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.Head, out Vector3 Position, out Quaternion Rotation);

            Vector3 trackingPosition = transform.InverseTransformPoint(Position);
            if (isPositionValid(trackingPosition) && trackingPosition.y < bestPosition)
            {
                bestPosition = trackingPosition.y;
                vibration = VIBRATE_BOTH;
            }
        }

        if (bestPosition > unpressedPosition.y && buttonState == STATE_INACTIVE && bestPosition != float.MaxValue)
        {
            // special case: only if player is actually within button's column of space do we update the state
            buttonState = STATE_CAN_PRESS;
        }
        else if (buttonState == STATE_CAN_PRESS && (bestPosition == float.MaxValue || (bestPosition < depressedPosition.y && bestPosition > lastPosition)))
        {
            // set the button inactive again if
            // 1) the player is outside of the button's column
            // 2) the player is inside of the button's column, but below the button and moving upwards
            buttonState = STATE_INACTIVE;
        }
        else if (bestPosition < unpressedPosition.y && buttonState == STATE_CAN_PRESS)
        {
            buttonState = STATE_PRESSING;
        }
        else if (bestPosition < depressedPosition.y && buttonState == STATE_PRESSING)
        {
            buttonState = STATE_DEPRESSED;

            if (pressSound != null)
            {
                AudioSource.PlayClipAtPoint(pressSound, this.transform.position, 0.1f);
            }

            if (buttonType == BUTTON_ONETIME)
            {
                button.GetComponent<MeshRenderer>().material.mainTexture = buttonOn;
            }
            else if (buttonType == BUTTON_TOGGLE)
            {
                toggleState = !toggleState;
                button.GetComponent<MeshRenderer>().material.mainTexture = toggleState ? buttonOn : buttonOff;
            }

            if ((vibration & VIBRATE_LEFT) != 0) player.PlayHaptic( Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand, 0.02f, 1.0f, 1.0f);
            if ((vibration & VIBRATE_RIGHT) != 0) player.PlayHaptic( Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand, 0.02f, 1.0f, 1.0f);

            sendPressedEvent();
        }
        else if (bestPosition >= pressedPosition.y && buttonType == BUTTON_ONETIME && buttonState == STATE_DEPRESSED)
        {
            buttonState = STATE_PUSHED;

            if ((vibration & VIBRATE_LEFT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand, 0.02f, 1.0f, 1.0f);
            if ((vibration & VIBRATE_RIGHT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand, 0.02f, 1.0f, 1.0f);
            if (releaseSound != null)
            {
                AudioSource.PlayClipAtPoint(releaseSound, this.transform.position, 0.1f);
            }
        }
        else if (bestPosition >= halfPressedPosition.y && buttonType == BUTTON_TOGGLE && buttonState == STATE_DEPRESSED)
        {
            buttonState = STATE_INACTIVE;
            
            if ((vibration & VIBRATE_LEFT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand, 0.02f, 1.0f, 1.0f);
            if ((vibration & VIBRATE_RIGHT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand, 0.02f, 1.0f, 1.0f);
            if (releaseSound != null)
            {
                AudioSource.PlayClipAtPoint(releaseSound, this.transform.position, 0.1f);
            }
        }
        else if (bestPosition >= unpressedPosition.y && buttonType == BUTTON_NORMAL && buttonState == STATE_DEPRESSED)
        {
            buttonState = STATE_INACTIVE;

            if ((vibration & VIBRATE_LEFT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand, 0.02f, 1.0f, 1.0f);
            if ((vibration & VIBRATE_RIGHT) != 0) player.PlayHaptic(Basis.Scripts.TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand, 0.02f, 1.0f, 1.0f);
            if (releaseSound != null)
            {
                AudioSource.PlayClipAtPoint(releaseSound, this.transform.position, 0.1f);
            }
        }
        else if (bestPosition >= unpressedPosition.y && buttonState == STATE_PRESSING)
        {
            buttonState = STATE_INACTIVE;
        }

        lastPosition = bestPosition;

        float maxHeight = unpressedPosition.y;
        if (buttonType == BUTTON_TOGGLE && toggleState) maxHeight = halfPressedPosition.y;
        if (buttonType == BUTTON_ONETIME && buttonState == STATE_PUSHED) maxHeight = pressedPosition.y;

        if (buttonState == STATE_INACTIVE || buttonState == STATE_CAN_PRESS)
        {
            bestPosition = maxHeight;
        }

        bestPosition = Mathf.Clamp(bestPosition, depressedPosition.y, maxHeight);

        Vector3 newPosition = visual.transform.localPosition;
        newPosition.y = bestPosition;
        visual.transform.localPosition = newPosition;
    }

    private int buttonState;
    public bool toggleState;
    private float lastPosition;

    private readonly int STATE_INACTIVE = 0;
    private readonly int STATE_CAN_PRESS = 1;
    private readonly int STATE_PRESSING = 2;
    private readonly int STATE_DEPRESSED = 3;
    private readonly int STATE_PUSHED = 4;

    private bool isPositionValid(Vector3 localPos)
    {
        Vector3 size = button.GetComponent<MeshFilter>().sharedMesh.bounds.size;

        Vector3 visualPos = visual.transform.localPosition;
        Vector3 thisScale = this.transform.localScale;
        return Mathf.Abs(localPos.x - visualPos.x) <= size.x / 2f * thisScale.x && Mathf.Abs(localPos.z - visualPos.z) <= size.z / 2f * thisScale.z;
    }

    private void sendPressedEvent()
    {
        if (callback == null) return;
        callback.inButton = this;
        callback._OnButtonPressed();
    }

    public void _ResetButton()
    {
        if (!button) return;
        
        buttonState = STATE_INACTIVE;
        lastPosition = float.MinValue;
        visual.transform.localPosition = unpressedPosition;
    }

    public void _ResetPushButton()
    {
        if (!button) return;
        if (buttonState != STATE_PUSHED) return;

        buttonState = STATE_INACTIVE;
        button.GetComponent<MeshRenderer>().material.mainTexture = buttonOff;
        visual.transform.localPosition = unpressedPosition;
    }

    public void _SetButtonPushed()
    {
        if (!button) return;
        if (buttonState != STATE_INACTIVE) return;

        buttonState = STATE_PUSHED;
        button.GetComponent<MeshRenderer>().material.mainTexture = buttonOn;
        visual.transform.localPosition = pressedPosition;
    }

    public void _SetButtonToggle(bool newToggle)
    {
        if (!button) return;
        if (buttonState != STATE_INACTIVE) return;
        if (toggleState == newToggle) return;
        
        toggleState = newToggle;
        button.GetComponent<MeshRenderer>().material.mainTexture = toggleState ? buttonOn : buttonOff;
        visual.transform.localPosition = toggleState ? halfPressedPosition : unpressedPosition;
    }
}
