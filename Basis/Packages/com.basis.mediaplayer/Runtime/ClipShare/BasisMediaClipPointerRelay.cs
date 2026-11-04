using System;
using UnityEngine;
using UnityEngine.EventSystems;

public sealed class BasisMediaClipPointerRelay : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action Pressed;
    public Action Released;
    public bool IsPressed { get; private set; }

    public void OnPointerDown(PointerEventData eventData)
    {
        IsPressed = true;
        Pressed?.Invoke();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!IsPressed) return;
        IsPressed = false;
        Released?.Invoke();
    }

    private void OnDisable()
    {
        if (!IsPressed) return;
        IsPressed = false;
        Released?.Invoke();
    }
}
