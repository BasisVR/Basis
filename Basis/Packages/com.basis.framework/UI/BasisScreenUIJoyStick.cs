using Basis.Scripts.Drivers;
using System;
using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RectTransform))]
public class BasisScreenUIJoyStick : MonoBehaviour,
    IPointerDownHandler,
    IPointerUpHandler,
    IDragHandler
{
    public float movementRange = 50f;
    public Action<Vector2> OnStickMove;
    public Action OnStickActive;
    public Action OnStickReleased;

    RectTransform rect;
    Canvas canvas;
    RectTransform canvasRect;
    Vector2 startPos;
    bool isPressed;

    void Awake()
    {
        rect = (RectTransform)transform;
        canvas = GetComponentInParent<Canvas>();
        if (canvas != null)
            canvasRect = canvas.GetComponent<RectTransform>();

        startPos = rect.anchoredPosition;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isPressed = true;
        OnStickActive?.Invoke();
        OnDrag(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isPressed) return;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            eventData.position,
            BasisLocalCameraDriver.Instance.Camera,
            out Vector2 localPos);

        var delta = localPos - startPos;
        delta = Vector2.ClampMagnitude(delta, movementRange);

        rect.anchoredPosition = startPos + delta;

        var norm = delta / movementRange;
        OnStickMove?.Invoke(norm);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!isPressed) return;
        isPressed = false;
        rect.anchoredPosition = startPos;
        OnStickMove?.Invoke(Vector2.zero);
        OnStickReleased?.Invoke();
    }
}
