using System;
using UnityEngine;
using UnityEngine.EventSystems;

public sealed class BasisInspectorScroll : MonoBehaviour, IScrollHandler
{
    public Action<float> Scrolled;
    public void OnScroll(PointerEventData eventData) => Scrolled?.Invoke(eventData.scrollDelta.y);
}
