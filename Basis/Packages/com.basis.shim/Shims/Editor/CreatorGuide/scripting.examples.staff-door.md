```csharp title="StaffDoor.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class StaffDoor : MonoBehaviour
{
    public GameObject door;
    public GameObject staffSign;

    void Start()
    {
        GetComponent<BasisPermissionEventShim>();
    }

    void OnLocalPermissionsChanged(bool isAdmin, bool isModerator)
    {
        bool staff = isAdmin || isModerator;
        door.SetActive(!staff);
        staffSign.SetActive(staff);
    }
}
```

- The door is turned off for staff on their own client. Nobody else is affected, so nothing is networked.
- To show a badge over *other* staff, ask with `BasisPermissionsShim.IsModerator(player, callback)`; it is a round trip to the server.
- Client side checks keep honest players out. Do not rely on them for anything a modified client must not reach.
