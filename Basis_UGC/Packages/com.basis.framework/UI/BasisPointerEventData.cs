using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.Scripts.UI
{
public class BasisPointerEventData : PointerEventData
{
    public bool WasLastDown = false;
    public BasisPointerEventData(EventSystem eventSystem) : base(eventSystem)
    {
    }
}
}