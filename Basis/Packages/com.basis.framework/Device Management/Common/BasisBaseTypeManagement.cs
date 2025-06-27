using System.Threading.Tasks;
using UnityEngine;

public abstract class BasisBaseTypeManagement : MonoBehaviour
{
    public abstract void StopSDK();
    public abstract void BeginLoadSDK();
    public abstract void StartSDK();
    public abstract string Type();
}
