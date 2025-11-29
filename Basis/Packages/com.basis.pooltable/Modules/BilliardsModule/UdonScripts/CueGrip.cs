using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
public class CueGrip : MonoBehaviour
{
    private CueController controller;
    private BasisPickupInteractable pickup;
    private MeshRenderer meshRenderer;
    private SphereCollider sphereCollider;
    bool isSecondary = false;
    public void _Init(CueController controller_, bool _isSecondary)
    {
        controller = controller_;
        isSecondary = _isSecondary;
        pickup = this.GetComponent<BasisPickupInteractable>();
        meshRenderer = this.GetComponent<MeshRenderer>();
        sphereCollider = this.GetComponent<SphereCollider>();
        _Hide();
        pickup.OnInteractStartEvent += OnPickup;
        pickup.OnInteractEndEvent += OnDrop;
        pickup.OnPickupUse += OnPickupUse;
        if (BasisDeviceManagement.IsCurrentModeVR())
        {
            pickup.AutoHold = BasisInteractableObject.BasisAutoHold.No;
        }
        else
        {
            pickup.AutoHold = BasisInteractableObject.BasisAutoHold.Yes;
        }
    }
    private void OnPickupUse(BasisPickUpUseMode mode)
    {
        switch (mode)
        {
            case BasisPickUpUseMode.OnPickUpUseUp:
                OnPickupUseUp();
                break;
            case BasisPickUpUseMode.OnPickUpUseDown:
                OnPickupUseDown();
                break;
            case BasisPickUpUseMode.OnPickUpStillDown:

                break;
        }
    }
    public void OnPickup(BasisInput Input)
    {
        BasisDebug.Log("OnPIckup");
        if (isSecondary)
        {
            controller._OnSecondaryPickup();
        }
        else
        {
            controller._OnPrimaryPickup();
        }
    }
    public void OnDrop(BasisInput Input)
    {
        BasisDebug.Log("OnDrop");
        if (isSecondary)
        {
            controller._OnSecondaryDrop();
        }
        else
        {
            controller._OnPrimaryDrop();
        }
    }
    public void OnPickupUseDown()
    {
        meshRenderer.enabled = false;
        if (isSecondary)
        {
            controller._OnSecondaryUseDown();
        }
        else
        {
            controller._OnPrimaryUseDown();
        }
    }
    public void OnPickupUseUp()
    {
        meshRenderer.enabled = true;
        if (isSecondary)
        {
            controller._OnSecondaryUseUp();
        }
        else
        {
            controller._OnPrimaryUseUp();
        }
    }
    public void _Show()
    {
        sphereCollider.enabled = true;
        pickup.InteractableEnabled = true;
        meshRenderer.enabled = true;
    }
    public void _Hide()
    {
        pickup.Drop();
        pickup.InteractableEnabled = false;
        sphereCollider.enabled = false;
        meshRenderer.enabled = false;
    }
}
