using UnityEngine;
using UnityEngine.EventSystems;

public class BasisButtonHeldCallBack : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public delegate void ButtonPressEvent();
    public event ButtonPressEvent OnButtonPressed;
    public event ButtonPressEvent OnButtonReleased;

    public void OnPointerDown(PointerEventData eventData)
    {
        // Handle button press
        OnButtonPressed?.Invoke();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        // Handle button release
        OnButtonReleased?.Invoke();
    }
}