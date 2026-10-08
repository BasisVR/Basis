using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[AutoStaticsCleanup]
public partial class BasisPersonalMirror : MonoBehaviour
{
    public static BasisPersonalMirror Instance;
    public void OnEnable()
    {
        Instance = this;

    }
}
