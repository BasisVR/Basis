using Basis;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using Basis.Shims;
using System;
using UnityEngine;
[Cilboxable]
public class CueController : MonoBehaviour
{
    [SerializeField] private BilliardsModule table;

    [SerializeField] private CueGrip primary;
    [SerializeField] private CueGrip secondary;

    [SerializeField] private GameObject desktop;
    [SerializeField] private GameObject body;
    [SerializeField] private GameObject cuetip;

    private BasisNetworkShim net;
    private BasisNetworkBehaviour primaryNetworking;
    private BasisNetworkBehaviour secondaryNetworking;
    private int holderId = -1;

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

    private Transform bodyTransform;
    private Transform primaryTransform;
    private Transform secondaryTransform;
    private Transform desktopTransform;
    private Transform tableTransform;
    private Transform[] gripTransforms;
    private Vector3[] gripPositions = new Vector3[2];
    private float lagFixedDeltaTime = -1;
    private float lagSmoothing = -1;
    private float lagFactor;

    private float gripSize;
    private float cuetipDistance;

    private int[] authorizedOwners;

    [NonSerialized]
    public bool TeamBlue;

    private bool lockHolderIsDesktop;
    private bool lockPrimaryLocked;
    private Vector3 lockPrimaryPos;
    private Vector3 lockPrimaryDir;
    private bool lockSecondaryLocked;
    private Vector3 lockSecondaryPos;
    private float lockCueScale;

    private const int LockStateLength = 43;

    private byte[] lockStateToBytes()
    {
        byte[] data = new byte[LockStateLength];
        data[0] = lockHolderIsDesktop ? (byte)1 : (byte)0;

        data[1] = lockPrimaryLocked ? (byte)1 : (byte)0;
        int o = BasisBinaryShim.WriteVector3(data, 2, lockPrimaryPos);
        o = BasisBinaryShim.WriteVector3(data, o, lockPrimaryDir);

        data[o++] = lockSecondaryLocked ? (byte)1 : (byte)0;
        o = BasisBinaryShim.WriteVector3(data, o, lockSecondaryPos);

        BasisBinaryShim.WriteSingle(data, o, lockCueScale);
        return data;
    }

    private bool lockStateFromBytes(byte[] data)
    {
        if (data == null || data.Length < LockStateLength) return false;

        lockHolderIsDesktop = data[0] != 0;

        lockPrimaryLocked = data[1] != 0;
        lockPrimaryPos = BasisBinaryShim.ReadVector3(data, 2);
        lockPrimaryDir = BasisBinaryShim.ReadVector3(data, 14);

        lockSecondaryLocked = data[26] != 0;
        lockSecondaryPos = BasisBinaryShim.ReadVector3(data, 27);

        lockCueScale = BasisBinaryShim.ReadSingle(data, 39);
        return true;
    }
    public void _Init()
    {
        bodyTransform = body.transform;
        primaryTransform = primary.transform;
        secondaryTransform = secondary.transform;
        desktopTransform = desktop.transform;
        tableTransform = table.transform;
        gripTransforms = new Transform[] { primaryTransform, secondaryTransform };

        cueRenderer = this.transform.Find("body/render").GetComponent<Renderer>();

        primaryController = primary;
        secondaryController = secondary;
        primaryController._Init(this, false);
        secondaryController._Init(this, true);
        primaryNetworking = primary.GetComponent<BasisNetworkBehaviour>();
        secondaryNetworking = secondary.GetComponent<BasisNetworkBehaviour>();

        gripSize = 0.03f;
        cuetipDistance = (cuetip.transform.position - primaryTransform.position).magnitude;

        origPrimaryPosition = primaryTransform.position;
        origSecondaryPosition = secondaryTransform.position;

        lagPrimaryPosition = origPrimaryPosition;
        lagSecondaryPosition = origSecondaryPosition;

        resetSecondaryOffset();
        _RefreshRenderer();

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkReady += OnNetworkReady;
            net.NetworkMessageReceived += OnNetworkMessage;
            net.OwnershipTransfer += OnOwnershipTransfer;
            net.PlayerJoined += OnPlayerJoined;
        }
    }
    private void OnNetworkReady()
    {
        if (net.IsOwnedLocallyOnServer)
        {
            holderId = table._LocalPlayerId();
        }
        else if (table._GetPlayer(net.CurrentOwnerId) != null)
        {
            holderId = net.CurrentOwnerId;
        }
    }
    private void OnOwnershipTransfer(BasisNetworkPlayer player)
    {
        if (player != null) holderId = player.playerId;
    }
    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || !IsLocalOwner()) return;
        if (player.playerId == table._LocalPlayerId()) return;
        net.SendCustomNetworkEvent(lockStateToBytes(), DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
    }
    private void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (buffer == null || !lockStateFromBytes(buffer)) return;
        refreshCueScale();
    }
    private bool IsLocalOwner()
    {
        return net != null && net.IsLocalOwner();
    }
    private void RequestSerialization()
    {
        if (net == null || !net.HasNetworkID) return;
        net.SendCustomNetworkEvent(lockStateToBytes(), DeliveryMethod.ReliableOrdered, null);
    }
    private void refreshCueScale()
    {
        float factor = Mathf.Clamp(lockCueScale, 0.5f, 1.5f) - 0.5f;
        bodyTransform.localScale = new Vector3(Mathf.Lerp(0.7f, 1.3f, factor), Mathf.Lerp(0.7f, 1.3f, factor), lockCueScale);
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
        primaryTransform.localRotation = Quaternion.identity;
        secondaryTransform.localRotation = Quaternion.identity;
        desktopTransform.localRotation = Quaternion.identity;
        origPrimaryPosition = newpos;
        primaryTransform.position = origPrimaryPosition;
        origSecondaryPosition = primaryTransform.TransformPoint(secondaryOffset);
        secondaryTransform.position = origSecondaryPosition;
        lagSecondaryPosition = origSecondaryPosition;
        lagPrimaryPosition = origPrimaryPosition;
        desktopTransform.position = origPrimaryPosition;
        bodyTransform.position = origPrimaryPosition;
    }
    public void UpdateDesktopPosition()
    {
        bodyTransform.GetPositionAndRotation(out var pos,out Quaternion Rotation);
        desktopTransform.SetPositionAndRotation(pos, Rotation);
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
                    if (!lockPrimaryLocked || table.noLockingLocal)
                    {
                        // base of cue goes to primary
                        bodyTransform.position = lagPrimaryPosition;

                        // holding in primary hand
                        if (!secondaryHolding)
                        {
                            // nothing in secondary hand. have the second grip track the cue
                            secondaryTransform.position = primaryTransform.TransformPoint(secondaryOffset);
                            bodyTransform.LookAt(lagSecondaryPosition);
                        }
                        else if (!lockSecondaryLocked)
                        {
                            // holding secondary hand. have cue track the second grip
                            bodyTransform.LookAt(lagSecondaryPosition);
                        }
                        else
                        {
                            // locking secondary hand. lock rotation on point
                            bodyTransform.LookAt(lockSecondaryPos);
                        }

                        // copy z rotation of primary
                        float rotation = primaryTransform.localEulerAngles.z;
                        Vector3 bodyRotation = bodyTransform.localEulerAngles;
                        bodyRotation.z = rotation;
                        bodyTransform.localEulerAngles = bodyRotation;
                    }
                    else
                    {
                        // locking primary hand. fix cue in line and ignore secondary hand
                        Vector3 delta = lagPrimaryPosition - lockPrimaryPos;
                        float distance = Vector3.Dot(delta, lockPrimaryDir);
                        bodyTransform.position = lockPrimaryPos + lockPrimaryDir * distance;
                    }

                    UpdateDesktopPosition();
                }
                else
                {
                    bodyTransform.position = desktopTransform.position;
                    bodyTransform.rotation = desktopTransform.rotation;
                }

                // clamp controllers
                clampControllers();
            }
            updateLagPosition();
        }
        else if (!table.localPlayerDistant && table.gameLive)
        {
            // other player has cue
            if (!lockHolderIsDesktop)
            {
                // other player is in vr, use the grips which update faster
                if (!lockPrimaryLocked || table.noLockingLocal)
                {
                    // base of cue goes to primary
                    bodyTransform.position = lagPrimaryPosition;

                    // holding in primary hand
                    if (!lockSecondaryLocked)
                    {
                        // have cue track the second grip
                        bodyTransform.LookAt(lagSecondaryPosition);
                    }
                    else
                    {
                        // locking secondary hand. lock rotation on point
                        bodyTransform.LookAt(lockSecondaryPos);
                    }
                }
                else
                {
                    // locking primary hand. fix cue in line and ignore secondary hand
                    Vector3 delta = lagPrimaryPosition - lockPrimaryPos;
                    float distance = Vector3.Dot(delta, lockPrimaryDir);
                    bodyTransform.position = lockPrimaryPos + lockPrimaryDir * distance;
                }
            }
            else
            {
                // other player is on desktop, use the slower synced marker
                bodyTransform.position = desktopTransform.position;
                bodyTransform.rotation = desktopTransform.rotation;
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
        float fixedDeltaTime = Time.fixedDeltaTime;
        if (fixedDeltaTime != lagFixedDeltaTime || cueSmoothing != lagSmoothing)
        {
            lagFixedDeltaTime = fixedDeltaTime;
            lagSmoothing = cueSmoothing;
            lagFactor = 1 - Mathf.Pow(0.5f, fixedDeltaTime * cueSmoothing);
        }
        BasisTransformBatchShim.GetPositions(gripTransforms, gripPositions);
        lagPrimaryPosition = Vector3.Lerp(lagPrimaryPosition, gripPositions[0], lagFactor);
        if (!lockSecondaryLocked)
        {
            lagSecondaryPosition = Vector3.Lerp(lagSecondaryPosition, gripPositions[1], lagFactor);
        }
    }

    private Vector3 clamp(Vector3 input, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        return new Vector3(Mathf.Clamp(input.x, minX, maxX), Mathf.Clamp(input.y, minY, maxY), Mathf.Clamp(input.z, minZ, maxZ));
    }

    private void resetSecondaryOffset()
    {
        Vector3 position = primaryTransform.InverseTransformPoint(secondaryTransform.position);
        secondaryOffset = position.normalized * Mathf.Clamp(position.magnitude, gripSize * 2, cuetipDistance);
    }

    private void takeOwnership()
    {
        holderId = table._LocalPlayerId();
        if (net != null) net.TakeOwnership();
        if (primaryNetworking != null) primaryNetworking.TakeOwnership();
        if (secondaryNetworking != null) secondaryNetworking.TakeOwnership();
    }

    private void resetPosition()
    {
        primaryTransform.position = origPrimaryPosition;
        primaryTransform.localRotation = Quaternion.identity;
        secondaryTransform.position = origSecondaryPosition;
        secondaryTransform.localRotation = Quaternion.identity;
        desktopTransform.position = origPrimaryPosition;
        desktopTransform.localRotation = Quaternion.identity;
        bodyTransform.position = origPrimaryPosition;
        bodyTransform.LookAt(origSecondaryPosition);
    }

    public void _OnPrimaryPickup()
    {
        takeOwnership();

        holderIsDesktop = BasisPlatformShim.IsDesktop;
        lockHolderIsDesktop = holderIsDesktop;
        primaryHolding = true;
        lockPrimaryLocked = false;
        lockCueScale = cueScaleMine;
        RequestSerialization();
        refreshCueScale();

        refreshCueSmoothing();

        table._OnPickupCue();

        if (!holderIsDesktop) secondaryController._Show();
    }

    public void _OnPrimaryDrop()
    {
        primaryHolding = false;
        lockHolderIsDesktop = false;
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
        lagPrimaryPosition = primaryTransform.position;
        lagSecondaryPosition = secondaryTransform.position;

        // move cue to primary grip, since it should be bounded
        bodyTransform.position = primaryTransform.position;
        // make sure cue is facing the secondary grip (since it may have flown off)
        bodyTransform.LookAt(secondaryTransform.position);
        // copy z rotation of primary
        float rotation = primaryTransform.localEulerAngles.z;
        Vector3 bodyRotation = bodyTransform.localEulerAngles;
        bodyRotation.z = rotation;
        bodyTransform.localEulerAngles = bodyRotation;
        // rotate primary grip to face cue, since cue is visual source of truth
        primaryTransform.rotation = bodyTransform.rotation;
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
            lockPrimaryLocked = true;
            lockPrimaryPos = bodyTransform.position;
            lockPrimaryDir = bodyTransform.forward.normalized;
            RequestSerialization();

            table._TriggerCueActivate();
        }
    }

    public void _OnPrimaryUseUp()
    {
        if (!holderIsDesktop)
        {
            lockPrimaryLocked = false;
            RequestSerialization();

            table._TriggerCueDeactivate();
        }
    }

    public void _OnSecondaryPickup()
    {
        secondaryHolding = true;
        lockSecondaryLocked = false;
        RequestSerialization();
    }

    public void _OnSecondaryDrop()
    {
        secondaryHolding = false;

        resetSecondaryOffset();
    }

    public void _OnSecondaryUseDown()
    {
        lockSecondaryLocked = true;
        lockSecondaryPos = secondaryTransform.position;

        RequestSerialization();
    }

    public void _OnSecondaryUseUp()
    {
        lockSecondaryLocked = false;

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
        lockCueScale = cueScaleMine;
        RequestSerialization();
        refreshCueScale();
    }

    public void resetScale()
    {
        if (!IsLocalOwner()) return;
        if (lockCueScale == 1) return;
        lockCueScale = 1;
        RequestSerialization();
        refreshCueScale();
    }

    private void clampControllers()
    {
        clampTransform(primaryTransform);
        clampTransform(secondaryTransform);
    }

    private void clampTransform(Transform child)
    {
        child.position = tableTransform.TransformPoint(clamp(tableTransform.InverseTransformPoint(child.position), -4.25f, 4.25f, 0f, 4f, -3.5f, 3.5f));
    }

    public GameObject _GetDesktopMarker()
    {
        return desktop;
    }

    public GameObject _GetCuetip()
    {
        return cuetip;
    }

    public IBasisPlayer _GetHolder()
    {
        return table._GetPlayer(holderId);
    }
}
