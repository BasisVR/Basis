using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.BasisCharacterController;
using UnityEngine;

public class BasisZoneSwitchCCMode : MonoBehaviour
{
    public BasisLocalCharacterDriver.Mode Mode = BasisLocalCharacterDriver.Mode.Fly;

    void OnTriggerEnter(Collider other)
    {
        if (other != null && other.gameObject != null)
        {
            if (other.gameObject.TryGetComponent<BasisLocalPlayer>(out BasisLocalPlayer Local))
            {
                if (Local.LocalCharacterDriver.CurrentModeKind != Mode)
                {
                    Local.LocalCharacterDriver.SetMode(Mode);
                }
            }
        }
    }
}
