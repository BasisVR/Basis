using Basis;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using System;
using UnityEngine;
public class CueController : BasisNetworkBehaviour
{
    [SerializeField] public BilliardsModule table;
    [SerializeField] public BasisNetworkBehaviour primary;
    [SerializeField] public BasisNetworkBehaviour secondary;
    [SerializeField] public GameObject desktop;
    [SerializeField] public GameObject body;
    [SerializeField] public GameObject cuetip;
    public bool holderIsDesktop;
    public bool primaryHolding;
    public SyncedCueControllerData SyncControllerData = new SyncedCueControllerData();
    public class SyncedCueControllerData
    {
        public bool syncedHolderIsDesktop;
        public bool primaryLocked;
        public bool secondaryLocked;
        public float cueScale = 1;
        public Vector3 primaryLockPos;
        public Vector3 primaryLockDir;
        public Vector3 secondaryLockPos;
    }
    public bool secondaryHolding;
    public float cueScaleMine = 1;
    public float cueSmoothingLocal = 1;
    public float cueSmoothing = 30;
    public Vector3 secondaryOffset;
    public Vector3 origPrimaryPosition;
    public Vector3 origSecondaryPosition;
    public Vector3 lagPrimaryPosition;
    public Vector3 lagSecondaryPosition;
    public CueGrip primaryController;
    public CueGrip secondaryController;
    public Renderer cueRenderer;
    public float gripSize;
    public float cuetipDistance;
    public int[] authorizedOwners;
    [NonSerialized] public bool TeamBlue;
    public void Initialization()
    {
        cueRenderer = this.transform.Find("body/render").GetComponent<Renderer>();

        primaryController = primary.GetComponent<CueGrip>();
        secondaryController = secondary.GetComponent<CueGrip>();
        primaryController._Init(this, false);
        secondaryController._Init(this, true);

        gripSize = 0.03f;
        cuetipDistance = (cuetip.transform.position - primary.transform.position).magnitude;

        origPrimaryPosition = primary.transform.position;
        origSecondaryPosition = secondary.transform.position;

        lagPrimaryPosition = origPrimaryPosition;
        lagSecondaryPosition = origSecondaryPosition;

        resetSecondaryOffset();
        _RefreshRenderer();
    }
    private void refreshCueScale()
    {
        float factor = Mathf.Clamp(SyncControllerData.cueScale, 0.5f, 1.5f) - 0.5f;
        body.transform.localScale = new Vector3(Mathf.Lerp(0.7f, 1.3f, factor), Mathf.Lerp(0.7f, 1.3f, factor), SyncControllerData.cueScale);
    }

    private void refreshCueSmoothing()
    {
        if (!IsLocalOwner() || !primaryHolding)
        {
            cueSmoothing = 30;
            return;
        }
        cueSmoothing = 30 * cueSmoothingLocal;
    }

    public void _SetAuthorizedOwners(int[] newOwners)
    {
        authorizedOwners = newOwners;
    }

    public void _Enable()
    {
        primaryController._Show();
    }

    public void _Disable()
    {
        primaryController._Hide();
        secondaryController._Hide();
    }

    public void _ResetCuePosition()
    {
        if (IsLocalOwner())
        {
            resetPosition();
        }
    }
    public void _RefreshTable()
    {
        Vector3 newpos;
        if (TeamBlue)
        {
            newpos = table.tableModels[table.tableModelLocal].CueBlue.position;
        }
        else
        {
            newpos = table.tableModels[table.tableModelLocal].CueOrange.position;
        }
        primary.transform.localRotation = Quaternion.identity;
        secondary.transform.localRotation = Quaternion.identity;
        desktop.transform.localRotation = Quaternion.identity;
        origPrimaryPosition = newpos;
        primary.transform.position = origPrimaryPosition;
        origSecondaryPosition = primary.transform.TransformPoint(secondaryOffset);
        secondary.transform.position = origSecondaryPosition;
        lagSecondaryPosition = origSecondaryPosition;
        lagPrimaryPosition = origPrimaryPosition;
        desktop.transform.position = origPrimaryPosition;
        body.transform.position = origPrimaryPosition;
    }
    public void UpdateDesktopPosition()
    {
        body.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
        desktop.transform.SetPositionAndRotation(Position, Rotation);
    }
    private void FixedUpdate()
    {
        if (IsLocalOwner())
        {
            if (primaryHolding)
            {
                // must not be shooting, since that takes control of the cue object
                if (!table.desktopManager._IsInUI() || !table.desktopManager._IsShooting())
                {
                    if (!SyncControllerData.primaryLocked || table.noLockingLocal)
                    {
                        // base of cue goes to primary
                        body.transform.position = lagPrimaryPosition;

                        // holding in primary hand
                        if (!secondaryHolding)
                        {
                            // nothing in secondary hand. have the second grip track the cue
                            secondary.transform.position = primary.transform.TransformPoint(secondaryOffset);
                            body.transform.LookAt(lagSecondaryPosition);
                        }
                        else if (!SyncControllerData.secondaryLocked)
                        {
                            // holding secondary hand. have cue track the second grip
                            body.transform.LookAt(lagSecondaryPosition);
                        }
                        else
                        {
                            // locking secondary hand. lock rotation on point
                            body.transform.LookAt(SyncControllerData.secondaryLockPos);
                        }

                        // copy z rotation of primary
                        float rotation = primary.transform.localEulerAngles.z;
                        Vector3 bodyRotation = body.transform.localEulerAngles;
                        bodyRotation.z = rotation;
                        body.transform.localEulerAngles = bodyRotation;
                    }
                    else
                    {
                        // locking primary hand. fix cue in line and ignore secondary hand
                        Vector3 delta = lagPrimaryPosition - SyncControllerData.primaryLockPos;
                        float distance = Vector3.Dot(delta, SyncControllerData.primaryLockDir);
                        body.transform.position = SyncControllerData.primaryLockPos + SyncControllerData.primaryLockDir * distance;
                    }

                    UpdateDesktopPosition();
                }
                else
                {
                    desktop.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                    body.transform.SetPositionAndRotation(Position, Rotation);
                }

                // clamp controllers
                clampControllers();
            }
            updateLagPosition();
        }
        else if (table.gameLive)
        {
            // other player has cue
            if (!SyncControllerData.syncedHolderIsDesktop)
            {
                // other player is in vr, use the grips which update faster
                if (!SyncControllerData.primaryLocked || table.noLockingLocal)
                {
                    // base of cue goes to primary
                    body.transform.position = lagPrimaryPosition;

                    // holding in primary hand
                    if (!SyncControllerData.secondaryLocked)
                    {
                        // have cue track the second grip
                        body.transform.LookAt(lagSecondaryPosition);
                    }
                    else
                    {
                        // locking secondary hand. lock rotation on point
                        body.transform.LookAt(SyncControllerData.secondaryLockPos);
                    }
                }
                else
                {
                    // locking primary hand. fix cue in line and ignore secondary hand
                    Vector3 delta = lagPrimaryPosition - SyncControllerData.primaryLockPos;
                    float distance = Vector3.Dot(delta, SyncControllerData.primaryLockDir);
                    body.transform.position = SyncControllerData.primaryLockPos + SyncControllerData.primaryLockDir * distance;
                }
            }
            else
            {
                // other player is on desktop, use the slower synced marker
                desktop.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                body.transform.SetPositionAndRotation(Position, Rotation);
            }
            updateLagPosition();
        }
    }
    void updateLagPosition()
    {
        // todo: ugly ugly hack from legacy 8ball. intentionally smooth/lag the position a bit
        // we can't remove this because this directly affects physics
        // must occur at the end after we've finished updating the transform's position
        // otherwise vrchat will try to change it because it's a pickup
        lagPrimaryPosition = Vector3.Lerp(lagPrimaryPosition, primary.transform.position, 1 - Mathf.Pow(0.5f, Time.fixedDeltaTime * cueSmoothing));
        if (!SyncControllerData.secondaryLocked)
            lagSecondaryPosition = Vector3.Lerp(lagSecondaryPosition, secondary.transform.position, 1 - Mathf.Pow(0.5f, Time.fixedDeltaTime * cueSmoothing));
    }

    private Vector3 clamp(Vector3 input, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        input.x = Mathf.Clamp(input.x, minX, maxX);
        input.y = Mathf.Clamp(input.y, minY, maxY);
        input.z = Mathf.Clamp(input.z, minZ, maxZ);
        return input;
    }

    private void resetSecondaryOffset()
    {
        Vector3 position = primary.transform.InverseTransformPoint(secondary.transform.position);
        secondaryOffset = position.normalized * Mathf.Clamp(position.magnitude, gripSize * 2, cuetipDistance);
    }
    private void resetPosition()
    {
        primary.transform.position = origPrimaryPosition;
        primary.transform.localRotation = Quaternion.identity;
        secondary.transform.position = origSecondaryPosition;
        secondary.transform.localRotation = Quaternion.identity;
        desktop.transform.position = origPrimaryPosition;
        desktop.transform.localRotation = Quaternion.identity;
        body.transform.position = origPrimaryPosition;
        body.transform.LookAt(origSecondaryPosition);
    }

    public void _OnPrimaryPickup()
    {
        RequestTakeOwnership();
        primary.RequestTakeOwnership();
        secondary.RequestTakeOwnership();

        holderIsDesktop = !BasisNetworkPlayer.LocalPlayer.IsUserInVR();
        SyncControllerData.syncedHolderIsDesktop = holderIsDesktop;
        primaryHolding = true;
        SyncControllerData.primaryLocked = false;
        SyncControllerData.cueScale = cueScaleMine;
        RequestSerialization();//local does OnDeserialization from this function aswell
        refreshCueSmoothing();

        table._OnPickupCue();

        if (!holderIsDesktop)
        {
            secondaryController._Show();
        }
    }

    public void _OnPrimaryDrop()
    {
        primaryHolding = false;
        SyncControllerData.syncedHolderIsDesktop = false;
        RequestSerialization();//local does OnDeserialization from this function aswell
        refreshCueSmoothing();

        // hide secondary
        if (!holderIsDesktop) secondaryController._Hide();

        // clamp again
        clampControllers();

        // make sure lag position is reset
        lagPrimaryPosition = primary.transform.position;
        lagSecondaryPosition = secondary.transform.position;

        // move cue to primary grip, since it should be bounded
        body.transform.position = primary.transform.position;
        // make sure cue is facing the secondary grip (since it may have flown off)
        body.transform.LookAt(secondary.transform.position);
        // copy z rotation of primary
        float rotation = primary.transform.localEulerAngles.z;
        Vector3 bodyRotation = body.transform.localEulerAngles;
        bodyRotation.z = rotation;
        body.transform.localEulerAngles = bodyRotation;
        // rotate primary grip to face cue, since cue is visual source of truth
        primary.transform.rotation = body.transform.rotation;
        // reset secondary offset
        resetSecondaryOffset();
        // update desktop marker
        UpdateDesktopPosition();

        table._OnDropCue();
    }
    public void _OnPrimaryUseDown()
    {
        if (!holderIsDesktop)
        {
            SyncControllerData.primaryLocked = true;
            SyncControllerData.primaryLockPos = body.transform.position;
            SyncControllerData.primaryLockDir = body.transform.forward.normalized;
            RequestSerialization();//local does OnDeserialization from this function aswell

            table._TriggerCueActivate();
        }
    }

    private void RequestSerialization()
    {
        byte[] Data = SerializationUtility.SerializeValue<SyncedCueControllerData>(SyncControllerData, DataFormat.Binary);
        SendCustomNetworkEvent(Data, DeliveryMethod.ReliableOrdered);
        OnDeserialization();
    }
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        SyncControllerData = SerializationUtility.DeserializeValue<SyncedCueControllerData>(buffer, DataFormat.Binary);
        OnDeserialization();
    }
    private void OnDeserialization()
    {
        refreshCueScale();
    }

    public void _OnPrimaryUseUp()
    {
        if (!holderIsDesktop)
        {
            SyncControllerData.primaryLocked = false;
            RequestSerialization();//local does OnDeserialization from this function aswell

            table._TriggerCueDeactivate();
        }
    }

    public void _OnSecondaryPickup()
    {
        secondaryHolding = true;
        SyncControllerData.secondaryLocked = false;
        RequestSerialization();//local does OnDeserialization from this function aswell
    }

    public void _OnSecondaryDrop()
    {
        secondaryHolding = false;

        resetSecondaryOffset();
    }

    public void _OnSecondaryUseDown()
    {
        SyncControllerData.secondaryLocked = true;
        SyncControllerData.secondaryLockPos = secondary.transform.position;

        RequestSerialization();//local does OnDeserialization from this function aswell
    }

    public void _OnSecondaryUseUp()
    {
        SyncControllerData.secondaryLocked = false;

        RequestSerialization();//local does OnDeserialization from this function aswell
    }

    public void _RefreshRenderer()
    {
        cueRenderer.enabled = (table.gameLive && (!table.isPracticeMode || this == table.cueControllers[0]));
    }
    public void setSmoothing(float smoothing)
    {
        cueSmoothingLocal = smoothing;
        refreshCueSmoothing();
    }

    public void setScale(float scale)
    {
        cueScaleMine = scale;
        if (!IsLocalOwner())
        {
            BasisDebug.LogError("Was Not owner For Set Scale!");
            return;
        }

        SyncControllerData.cueScale = cueScaleMine;

        RequestSerialization();//local does OnDeserialization from this function aswell
    }

    public void resetScale()
    {
        if (!IsLocalOwner())
        {
            BasisDebug.LogError("Was Not owner For resetScale!");
            return;
        }
        if (SyncControllerData.cueScale == 1)
        {
            return;
        }
        SyncControllerData.cueScale = 1;

        RequestSerialization();//local does OnDeserialization from this function aswell

    }
    private void clampControllers()
    {
        clampTransform(primary.transform);
        clampTransform(secondary.transform);
    }

    private void clampTransform(Transform child)
    {
        child.position = table.transform.TransformPoint(clamp(table.transform.InverseTransformPoint(child.position), -4.25f, 4.25f, 0f, 4f, -3.5f, 3.5f));
    }

    public GameObject _GetDesktopMarker()
    {
        return desktop;
    }

    public GameObject _GetCuetip()
    {
        return cuetip;
    }

    public BasisNetworkPlayer _GetHolder()
    {
        return primary.currentOwnedPlayer;
    }
}
