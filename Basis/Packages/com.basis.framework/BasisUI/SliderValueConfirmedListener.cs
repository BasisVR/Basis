using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.BasisUI
{
    public class SliderValueConfirmedListener : MonoBehaviour, IEndDragHandler, IPointerUpHandler
    {
        public Action OnValueConfirmed;
        public void OnEndDrag(PointerEventData eventData) => InvokeConfirmation();
        public void OnPointerUp(PointerEventData eventData) => InvokeConfirmation();

        private void InvokeConfirmation()
        {
            OnValueConfirmed?.Invoke();
        }
    }
}
