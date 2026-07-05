using UnityEngine;
using UnityEngine.EventSystems;

namespace Basis.Shims
{
    public class BasisActionTrigger : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IDropHandler,
        IScrollHandler,
        IUpdateSelectedHandler,
        ISelectHandler,
        IDeselectHandler,
        IMoveHandler,
        ISubmitHandler,
        ICancelHandler
    {
        public delegate void PointerEventHandler(PointerEventData eventData);

        public delegate void BaseEventHandler(BaseEventData eventData);

        public delegate void AxisEventHandler(AxisEventData eventData);

        public PointerEventHandler PointerEnter { get; set; }
        public PointerEventHandler PointerExit { get; set; }
        public PointerEventHandler PointerDown { get; set; }
        public PointerEventHandler PointerUp { get; set; }
        public PointerEventHandler PointerClick { get; set; }
        public PointerEventHandler InitializePotentialDrag { get; set; }
        public PointerEventHandler BeginDrag { get; set; }
        public PointerEventHandler Drag { get; set; }
        public PointerEventHandler EndDrag { get; set; }
        public PointerEventHandler Drop { get; set; }
        public PointerEventHandler Scroll { get; set; }
        public BaseEventHandler UpdateSelected { get; set; }
        public BaseEventHandler Select { get; set; }
        public BaseEventHandler Deselect { get; set; }
        public AxisEventHandler Move { get; set; }
        public BaseEventHandler Submit { get; set; }
        public BaseEventHandler Cancel { get; set; }

        public void OnPointerEnter(PointerEventData eventData)
        {
            PointerEnter?.Invoke(eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            PointerExit?.Invoke(eventData);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            PointerDown?.Invoke(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            PointerUp?.Invoke(eventData);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            PointerClick?.Invoke(eventData);
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            InitializePotentialDrag?.Invoke(eventData);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            BeginDrag?.Invoke(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Drag?.Invoke(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            EndDrag?.Invoke(eventData);
        }

        public void OnDrop(PointerEventData eventData)
        {
            Drop?.Invoke(eventData);
        }

        public void OnScroll(PointerEventData eventData)
        {
            Scroll?.Invoke(eventData);
        }

        public void OnUpdateSelected(BaseEventData eventData)
        {
            UpdateSelected?.Invoke(eventData);
        }

        public void OnSelect(BaseEventData eventData)
        {
            Select?.Invoke(eventData);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            Deselect?.Invoke(eventData);
        }

        public void OnMove(AxisEventData eventData)
        {
            Move?.Invoke(eventData);
        }

        public void OnSubmit(BaseEventData eventData)
        {
            Submit?.Invoke(eventData);
        }

        public void OnCancel(BaseEventData eventData)
        {
            Cancel?.Invoke(eventData);
        }
    }
}
