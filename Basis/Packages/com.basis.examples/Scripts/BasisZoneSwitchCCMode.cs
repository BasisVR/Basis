using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

public class BasisZoneSwitchCCMode : MonoBehaviour
{
//    public Basis.Scripts.BasisCharacterController.BasisLocalCharacterDriver.Mode Mode = Basis.Scripts.BasisCharacterController.BasisLocalCharacterDriver.Mode.Fly;
    void OnTriggerEnter(Collider other)
    {
        if (other != null && other.gameObject != null)
        {
            if (other.gameObject.TryGetComponent<BasisLocalPlayer>(out BasisLocalPlayer Local))
            {
               // if (Local.LocalCharacterDriver.CurrentModeKind != Mode)
                {
               //     Local.LocalCharacterDriver.SetMode(Mode);
                }
            }
        }
    }
}
