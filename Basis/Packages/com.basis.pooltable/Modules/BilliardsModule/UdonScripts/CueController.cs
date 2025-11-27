using Basis;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using System;
using System.IO;
using UnityEngine;
public class CueController : BasisNetworkBehaviour
{
    [SerializeField] private BilliardsModule table;

    [SerializeField] private CueGrip primary;
    [SerializeField] private CueGrip secondary;
    public BasisObjectSyncNetworking PrimaryNetworking;
    public BasisObjectSyncNetworking SecondaryNetworking;

    [SerializeField] private GameObject desktop;
    [SerializeField] private GameObject body;
    [SerializeField] private GameObject cuetip;

    private bool holderIsDesktop;

    private bool primaryHolding;

    private bool secondaryHolding;

    private float cueScaleMine = 1;
    private float cueSmoothingLocal = 1;
    private float cueSmoothing = 30;

    private Vector3 secondaryOffset;

    private Vector3 origPrimaryPosition;
    private Vector3 origSecondaryPosition;

    private Vector3 lagPrimaryPosition;
    private Vector3 lagSecondaryPosition;

    private CueGrip primaryController;
    private CueGrip secondaryController;

    private Renderer cueRenderer;

    private float gripSize;
    private float cuetipDistance;

    private int[] authorizedOwners;

    [NonSerialized]
    public bool TeamBlue;

    public CueLockState SynccueLockState;

    [System.Serializable]
    public struct CueLockState
    {
        public bool syncedHolderIsDesktop;

        public bool primaryLocked;
        public Vector3 primaryLockPos;
        public Vector3 primaryLockDir;

        public bool secondaryLocked;
        public Vector3 secondaryLockPos;

        public float cueScale;

        // Convert to byte array
        public byte[] ToByteArray()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(syncedHolderIsDesktop);

                writer.Write(primaryLocked);
                WriteVector3(writer, primaryLockPos);
                WriteVector3(writer, primaryLockDir);

                writer.Write(secondaryLocked);
                WriteVector3(writer, secondaryLockPos);

                writer.Write(cueScale);

                return stream.ToArray();
            }
        }

        // Convert from byte array
        public static CueLockState FromByteArray(byte[] data)
        {
            CueLockState state = new CueLockState();
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                state.syncedHolderIsDesktop = reader.ReadBoolean();

                state.primaryLocked = reader.ReadBoolean();
                state.primaryLockPos = ReadVector3(reader);
                state.primaryLockDir = ReadVector3(reader);

                state.secondaryLocked = reader.ReadBoolean();
                state.secondaryLockPos = ReadVector3(reader);

                state.cueScale = reader.ReadSingle();
            }
            return state;
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 vec)
        {
            writer.Write(vec.x);
            writer.Write(vec.y);
            writer.Write(vec.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
    public void _Init()
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
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        SynccueLockState = CueLockState.FromByteArray(buffer);
        refreshCueScale();
    }
    private void RequestSerialization()
    {
        SendCustomNetworkEvent(SynccueLockState.ToByteArray(), DeliveryMethod.ReliableOrdered);
    }
    private void refreshCueScale()
    {
        float factor = Mathf.Clamp(SynccueLockState.cueScale, 0.5f, 1.5f) - 0.5f;
        body.transform.localScale = new Vector3(Mathf.Lerp(0.7f, 1.3f, factor), Mathf.Lerp(0.7f, 1.3f, factor), SynccueLockState.cueScale);
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
        desktop.transform.position = body.transform.position;
        desktop.transform.rotation = body.transform.rotation;
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
                    if (!SynccueLockState.primaryLocked || table.noLockingLocal)
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
                        else if (!SynccueLockState.secondaryLocked)
                        {
                            // holding secondary hand. have cue track the second grip
                            body.transform.LookAt(lagSecondaryPosition);
                        }
                        else
                        {
                            // locking secondary hand. lock rotation on point
                            body.transform.LookAt(SynccueLockState.secondaryLockPos);
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
                        Vector3 delta = lagPrimaryPosition - SynccueLockState.primaryLockPos;
                        float distance = Vector3.Dot(delta, SynccueLockState.primaryLockDir);
                        body.transform.position = SynccueLockState.primaryLockPos + SynccueLockState.primaryLockDir * distance;
                    }

                    UpdateDesktopPosition();
                }
                else
                {
                    body.transform.position = desktop.transform.position;
                    body.transform.rotation = desktop.transform.rotation;
                }

                // clamp controllers
                clampControllers();
            }
            updateLagPosition();
        }
        else if (!table.localPlayerDistant && table.gameLive)
        {
            // other player has cue
            if (!SynccueLockState.syncedHolderIsDesktop)
            {
                // other player is in vr, use the grips which update faster
                if (!SynccueLockState.primaryLocked || table.noLockingLocal)
                {
                    // base of cue goes to primary
                    body.transform.position = lagPrimaryPosition;

                    // holding in primary hand
                    if (!SynccueLockState.secondaryLocked)
                    {
                        // have cue track the second grip
                        body.transform.LookAt(lagSecondaryPosition);
                    }
                    else
                    {
                        // locking secondary hand. lock rotation on point
                        body.transform.LookAt(SynccueLockState.secondaryLockPos);
                    }
                }
                else
                {
                    // locking primary hand. fix cue in line and ignore secondary hand
                    Vector3 delta = lagPrimaryPosition - SynccueLockState.primaryLockPos;
                    float distance = Vector3.Dot(delta, SynccueLockState.primaryLockDir);
                    body.transform.position = SynccueLockState.primaryLockPos + SynccueLockState.primaryLockDir * distance;
                }
            }
            else
            {
                // other player is on desktop, use the slower synced marker
                body.transform.position = desktop.transform.position;
                body.transform.rotation = desktop.transform.rotation;
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
        if (!SynccueLockState.secondaryLocked)
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

    private void takeOwnership()
    {
        TakeOwnership();
        PrimaryNetworking.TakeOwnership();
        SecondaryNetworking.TakeOwnership();
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
        takeOwnership();

        holderIsDesktop = !BasisNetworkPlayer.LocalPlayer.IsUserInVR();
        SynccueLockState.syncedHolderIsDesktop = holderIsDesktop;
        primaryHolding = true;
        SynccueLockState.primaryLocked = false;
        SynccueLockState.cueScale = cueScaleMine;
        RequestSerialization();
        refreshCueScale();

        refreshCueSmoothing();

        table._OnPickupCue();

        if (!holderIsDesktop) secondaryController._Show();
    }

    public void _OnPrimaryDrop()
    {
        primaryHolding = false;
        SynccueLockState.syncedHolderIsDesktop = false;
        RequestSerialization();
        refreshCueScale();

        refreshCueSmoothing();

        // hide secondary
        if (!holderIsDesktop)
        {
            secondaryController._Hide();
        }
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
            SynccueLockState.primaryLocked = true;
            SynccueLockState.primaryLockPos = body.transform.position;
            SynccueLockState.primaryLockDir = body.transform.forward.normalized;
            RequestSerialization();

            table._TriggerCueActivate();
        }
    }

    public void _OnPrimaryUseUp()
    {
        if (!holderIsDesktop)
        {
            SynccueLockState.primaryLocked = false;
            RequestSerialization();

            table._TriggerCueDeactivate();
        }
    }

    public void _OnSecondaryPickup()
    {
        secondaryHolding = true;
        SynccueLockState.secondaryLocked = false;
        RequestSerialization();
    }

    public void _OnSecondaryDrop()
    {
        secondaryHolding = false;

        resetSecondaryOffset();
    }

    public void _OnSecondaryUseDown()
    {
        SynccueLockState.secondaryLocked = true;
        SynccueLockState.secondaryLockPos = secondary.transform.position;

        RequestSerialization();
    }

    public void _OnSecondaryUseUp()
    {
        SynccueLockState.secondaryLocked = false;

        RequestSerialization();
    }

    public void _RefreshRenderer()
    {
        // enable if live, in LoD range,
        // disable second cue if in practice mode
        if (table.gameLive && !table.localPlayerDistant && (!table.isPracticeMode || this == table.cueControllers[0]))
            _EnableRenderer();
        else
            _DisableRenderer();
    }

    public void _EnableRenderer()
    {
        cueRenderer.enabled = true;
    }

    public void _DisableRenderer()
    {
        cueRenderer.enabled = false;
    }

    public void setSmoothing(float smoothing)
    {
        cueSmoothingLocal = smoothing;
        refreshCueSmoothing();
    }

    public void setScale(float scale)
    {
        cueScaleMine = scale;
        if (!IsLocalOwner()) return;
        SynccueLockState.cueScale = cueScaleMine;
        RequestSerialization();
        refreshCueScale();
    }

    public void resetScale()
    {
        if (!IsLocalOwner()) return;
        if (SynccueLockState.cueScale == 1) return;
        SynccueLockState.cueScale = 1;
        RequestSerialization();
        refreshCueScale();
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
        return PrimaryNetworking.currentOwnedPlayer;
    }
}
