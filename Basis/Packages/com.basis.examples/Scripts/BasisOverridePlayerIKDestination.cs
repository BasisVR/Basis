using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

public class BasisOverridePlayerIKDestination : MonoBehaviour
{
    public HumanBodyBones Overidenbone = HumanBodyBones.Hips;
    public void Update()
    {
        this.transform.GetPositionAndRotation(out Vector3 Position,out Quaternion Rotation);
        BasisLocalPlayer.Instance.LocalRigDriver.SetOverrideData(Overidenbone, Position, Rotation);
    }
    public void OnEnable()
    {
        BasisLocalPlayer.Instance.LocalRigDriver.SetOverrideUsage(Overidenbone, true);
    }
    public void OnDisable()
    {
        BasisLocalPlayer.Instance.LocalRigDriver.SetOverrideUsage(Overidenbone, false);
    }
}
