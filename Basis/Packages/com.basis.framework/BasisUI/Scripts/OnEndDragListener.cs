using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.BasisUI
{
    public class OnEndDragListener : MonoBehaviour, IEndDragHandler
    {
        public Action OnDragComplete;
        public void OnEndDrag(PointerEventData eventData) => OnDragComplete?.Invoke();
    }
}
