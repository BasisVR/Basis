using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// One UI Toolkit pointer. Each Basis pointer source — every ray device and every poking
    /// fingertip — owns an instance, so they hover, press and capture independently.
    /// </summary>
    public class BasisUIToolkitPointer
    {
        // Far outside any panel: dispatched on leave so the panel picks nothing and emits its
        // own PointerLeave. Without it the last hovered element stays highlighted forever.
        private static readonly Vector2 OffPanelPosition = new Vector2(-10000f, -10000f);
        private static int NextPointerIndex;

        public readonly int PointerIdValue;

        private BasisUIToolkitPanel ActivePanel;
        private Vector2 LastPanelPosition;
        private bool IsDown;
        private bool HasPosition;
        private bool WasDownLastFrame;
        private bool RisingEdge;

        public bool WantsCapture => IsDown && ActivePanel != null;
        public BasisUIToolkitPanel CapturedPanel => ActivePanel;
        public bool IsPressed => IsDown;
        public bool RisingEdgeThisFrame => RisingEdge;

        public enum PointerAction
        {
            Move,
            Press,
            Release,
        }

        /// <summary>
        /// Pure press-edge decision. Something already held when the pointer arrives on a panel
        /// must resolve to <see cref="PointerAction.Move"/> and never <see cref="PointerAction.Press"/>
        /// — that is the spawned-menu phantom click (press on panel A opens panel B under the
        /// pointer, B must not eat a press from the same hold).
        /// </summary>
        public static PointerAction ResolveAction(bool risingEdge, bool isDown, bool isPressed)
        {
            if (risingEdge && !isPressed)
            {
                return PointerAction.Press;
            }

            if (!isDown && isPressed)
            {
                return PointerAction.Release;
            }

            return PointerAction.Move;
        }

        public BasisUIToolkitPointer()
        {
            int index = NextPointerIndex;
            NextPointerIndex++;

            if (BasisUIToolkitPointerIdentity.Supported)
            {
                int count = Mathf.Max(1, PointerId.trackedPointerCount);
                PointerIdValue = PointerId.trackedPointerIdBase + (index % count);
            }
            else
            {
                PointerIdValue = PointerId.penPointerIdBase;
            }
        }

        /// <summary>
        /// Called once per pointer per frame before any dispatch, so the press edge is measured
        /// against the input itself rather than against whichever panel is under it. Something
        /// already held when the pointer arrives on a panel must not press it — that is the
        /// spawned-menu phantom click the uGUI path already guards against.
        /// </summary>
        public void BeginFrame(bool isDown)
        {
            RisingEdge = isDown && !WasDownLastFrame;
            WasDownLastFrame = isDown;
        }

        public void Process(BasisUIToolkitPanel panel, Vector2 panelPosition, bool isDown, Vector2 scrollDelta)
        {
            IPanel target = panel != null ? panel.RuntimePanel : null;
            if (target == null)
            {
                Release();
                return;
            }

            if (ActivePanel != null && ActivePanel != panel)
            {
                Release();
            }

            bool continuous = ActivePanel == panel && HasPosition;
            Vector2 delta = continuous ? panelPosition - LastPanelPosition : Vector2.zero;
            ActivePanel = panel;

            switch (ResolveAction(RisingEdge, isDown, IsDown))
            {
                case PointerAction.Press:
                    Dispatch<PointerMoveEvent>(target, panelPosition, delta, false, PenEventType.NoContact);
                    Dispatch<PointerDownEvent>(target, panelPosition, Vector2.zero, true, PenEventType.PenDown);
                    IsDown = true;
                    break;

                case PointerAction.Release:
                    Dispatch<PointerUpEvent>(target, panelPosition, delta, false, PenEventType.PenUp);
                    IsDown = false;
                    break;

                default:
                    Dispatch<PointerMoveEvent>(target, panelPosition, delta, IsDown, PenEventType.NoContact);
                    break;
            }

            if (scrollDelta.sqrMagnitude > 0f)
            {
                DispatchWheel(target, panelPosition, scrollDelta);
            }

            LastPanelPosition = panelPosition;
            HasPosition = true;
        }

        public void Release()
        {
            // State is cleared unconditionally: a panel destroyed mid-press leaves ActivePanel
            // reading as null, and an early return here would strand IsDown set forever — the
            // next panel would then receive an unpaired PointerUp.
            IPanel target = ActivePanel != null ? ActivePanel.RuntimePanel : null;
            if (target != null)
            {
                if (IsDown)
                {
                    Dispatch<PointerUpEvent>(target, LastPanelPosition, Vector2.zero, false, PenEventType.PenUp);
                }

                Dispatch<PointerMoveEvent>(target, OffPanelPosition, Vector2.zero, false, PenEventType.NoContact);
                PointerCaptureHelper.ReleasePointer(target, PointerIdValue);
            }

            ActivePanel = null;
            IsDown = false;
            HasPosition = false;
        }

        private void Dispatch<T>(IPanel panel, Vector2 position, Vector2 delta, bool pressed, PenEventType contact) where T : PointerEventBase<T>, new()
        {
            // Pen rather than touch as the source shape: a pen reports hover without contact, so
            // an un-pressed move does not read as a held button. Touch pointers have no hover
            // state and would leave every panel looking permanently pressed. The identity is then
            // overridden to a tracked pointer so each device stays independent.
            PenData pen = new PenData
            {
                position = position,
                deltaPos = delta,
                pressure = pressed ? 1f : 0f,
                penStatus = pressed ? PenStatus.Contact : PenStatus.None,
                contactType = contact,
            };

            using (T pointerEvent = PointerEventBase<T>.GetPooled(pen, EventModifiers.None))
            {
                BasisUIToolkitPointerIdentity.Apply(pointerEvent, PointerIdValue);
                panel.visualTree.SendEvent(pointerEvent);
            }
        }

        private static void DispatchWheel(IPanel panel, Vector2 position, Vector2 scrollDelta)
        {
            // Basis scroll is positive-up (stick/wheel); IMGUI wheel delta is positive-down.
            Event wheel = new Event
            {
                type = EventType.ScrollWheel,
                mousePosition = position,
                delta = new Vector2(scrollDelta.x, -scrollDelta.y),
            };

            using (WheelEvent wheelEvent = WheelEvent.GetPooled(wheel))
            {
                panel.visualTree.SendEvent(wheelEvent);
            }
        }
    }
}
