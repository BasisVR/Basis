using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// The move control for a panel, along with the reset that sits beside it. Holding move drags
    /// the panel around with whatever is pointing at it — the mouse on desktop, a hand ray in VR —
    /// and in VR the thumbstick of the hand doing the holding reels the panel closer or pushes it
    /// away. Desktop drags stay in the panel's own plane and inside the screen: there is no depth
    /// axis to aim along with a mouse, and a panel dragged off the edge could not be dragged back.
    /// <para>
    /// Where the user leaves it is remembered per panel and per platform, see
    /// <see cref="BasisPanelOffsetMemory"/>. Reset puts it back where the panel was authored, and
    /// keeps out of the way until there is something to put back.
    /// </para>
    /// <para>
    /// The drag is computed entirely in the panel parent's local space. On desktop both the pointer
    /// and the menu follow the head, so a world-space delta would count that shared motion twice and
    /// the panel would slide away whenever the user turned.
    /// </para>
    /// </summary>
    public class BasisPanelMoveHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public const string MoveKey = "menu.panel.move";
        public const string MoveTooltipKey = "menu.panel.move.tooltip";
        public const string ResetKey = "menu.panel.reset";
        public const string ResetTooltipKey = "menu.panel.reset.tooltip";
        public const string ResetPositionKey = "menu.panel.reset.position";
        public const string ResetAllKey = "menu.panel.reset.all";
        public const string SearchKey = "menu.panel.search";
        public const string SearchTooltipKey = "menu.panel.search.tooltip";

        /// <summary>
        /// Runs after the raycasters have refreshed their rays for this frame (BasisDesktopEye is
        /// 100, the hands poll earlier in the render pass) and after BasisMenuMover has placed the
        /// menu root (99), so a drag reads this frame's pointer against this frame's menu.
        /// </summary>
        private const int TickPriority = 101;

        private const string LockOwner = nameof(BasisPanelMoveHandle);

        /// <summary>Metres per second the thumbstick reels the panel in or out, at avatar scale 1.</summary>
        private const float PushMetresPerSecond = 0.75f;
        private const float StickDeadZone = 0.15f;
        private const float MinGrabMetres = 0.15f;
        private const float MaxGrabMetres = 3f;

        private BasisMenuPanel _panel;
        private RectTransform _panelRect;
        private PanelButton _moveButton;
        private PanelButton _resetButton;
        private PanelButton _searchButton;
        private string _panelKey;
        private Vector3 _defaultLocalPosition;

        private BasisInput _draggingInput;
        private float _grabDistance;
        private Vector3 _grabLocalOffset;
        private bool _lockDepth;
        private float _lockedDepth;
        private bool _dragging;
        private bool _bootModeHooked;
        private bool _resetGestureHooked;

        private readonly BasisLocks.LockContext _movementLock = BasisLocks.GetContext(BasisLocks.Movement);
        private readonly BasisLocks.LockContext _lookLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        /// <summary>
        /// Builds the move and reset buttons for <paramref name="panel"/> and applies whatever
        /// placement the user last left it at. Panels with a header (every provider page) get the
        /// buttons alongside Close; panels without one — the keyboard, dialogues, prompts that
        /// spawn over the menu — get a compact icon strip in their top-right corner instead.
        /// </summary>
        public static BasisPanelMoveHandle Attach(BasisMenuPanel panel, string panelKey)
        {
            if (panel == null)
            {
                return null;
            }

            PanelElementDescriptor descriptor = panel.Descriptor;
            bool inHeader = descriptor != null && descriptor.HasHeader;
            RectTransform host = inHeader ? descriptor.Header : CreateOverlayStrip(panel);
            if (host == null)
            {
                return null;
            }

            // Search sits leftmost of the three so the destructive one stays furthest from it. It
            // and Reset are both built inactive: each appears only once it has something to do — a
            // search to run, a placement or a page default to put back.
            PanelButton search = CreateButton(host, inHeader, SearchKey, SearchTooltipKey, AddressableAssets.Sprites.Search, SearchStyle, "Search Button");
            PanelButton move = CreateButton(host, inHeader, MoveKey, MoveTooltipKey, AddressableAssets.Sprites.Move, MoveStyle, "Move Button");
            PanelButton reset = CreateButton(host, inHeader, ResetKey, ResetTooltipKey, AddressableAssets.Sprites.Reset, ResetStyle, "Reset Button");
            if (move == null)
            {
                return null;
            }

            if (search != null)
            {
                search.gameObject.SetActive(false);
            }

            if (reset != null)
            {
                reset.gameObject.SetActive(false);
            }

            BasisPanelMoveHandle handle = move.gameObject.AddComponent<BasisPanelMoveHandle>();
            handle.Bind(panel, move, reset, search, panelKey);
            return handle;
        }

        /// <summary>
        /// Points this panel's header Search at something to search. Until this is called the button
        /// stays hidden, so a panel with nothing to look through does not offer to look through it.
        /// Pass a null query to take it away again.
        /// </summary>
        public static void SetPanelSearch(BasisMenuPanel panel, string title, BasisPanelSearchQuery query)
        {
            if (panel == null)
            {
                return;
            }

            BasisPanelMoveHandle handle = panel.GetComponentInChildren<BasisPanelMoveHandle>(true);
            if (handle == null)
            {
                return;
            }

            handle._searchTitle = title;
            handle._search = query;
            if (handle._searchButton != null)
            {
                handle._searchButton.gameObject.SetActive(query != null);
            }
        }

        private string _searchTitle;
        private BasisPanelSearchQuery _search;

        private void OpenSearch()
        {
            if (_search != null)
            {
                BasisPanelSearchPopup.Open(_searchTitle, _search);
            }
        }

        // Compact icon-only metrics for the overlay strip; the header variant keeps the Close
        // button's own 130x50 so the three read as one row.
        private const float OverlayButtonWidth = 62f;
        private const float OverlayButtonHeight = 50f;
        private const float OverlayMargin = 14f;
        private const float OverlaySpacing = 6f;

        /// <summary>Accent blue: move and search are ordinary things the user can do freely.</summary>
        private const string MoveStyle = "Button Standard";
        private const string SearchStyle = "Button Standard";

        /// <summary>
        /// Danger red: reset throws away a placement the user arranged by hand and there is no undo,
        /// so it should read as the one control here that discards something.
        /// </summary>
        private const string ResetStyle = "Button Danger";

        /// <summary>
        /// Both variants are built from the Close Button prefab so they match the close control they
        /// sit beside, then re-skinned to say what they actually do. In a header each lands
        /// immediately left of Close, which is always its last child.
        /// </summary>
        private static PanelButton CreateButton(RectTransform host, bool inHeader, string titleKey, string tooltipKey, string icon, string style, string name)
        {
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, host);
            if (button == null)
            {
                return null;
            }

            button.gameObject.name = name;
            // The label is dropped on the overlay strip: it sits over panel content that has no
            // room to spare, so it reads as window chrome rather than another row of controls.
            button.Descriptor.SetTitle(inHeader ? BasisLocalization.Get(titleKey) : string.Empty);
            button.Descriptor.SetTooltip(BasisLocalization.Get(tooltipKey));
            button.SetIcon(icon);
            if (button.ButtonStyling != null)
            {
                button.ButtonStyling.SetStyle(style);
            }

            if (inHeader)
            {
                button.transform.SetSiblingIndex(Mathf.Max(0, host.childCount - 2));
            }
            else
            {
                button.SetSize(new Vector2(OverlayButtonWidth, OverlayButtonHeight));
            }

            return button;
        }

        /// <summary>
        /// A floating control strip pinned inside the panel's top-right corner, for panel styles
        /// that never had a header row. Parented to the panel itself rather than its content, so it
        /// is not clipped by the panel's mask and does not disturb the content layout, and added
        /// last so it draws over whatever it overlaps. Kept inside the panel rect deliberately: the
        /// panel's own box collider is what the pointer ray hits, so anything hanging off the edge
        /// would be unclickable.
        /// </summary>
        private static RectTransform CreateOverlayStrip(BasisMenuPanel panel)
        {
            GameObject stripObject = new GameObject("Panel Move Controls", typeof(RectTransform));
            stripObject.layer = panel.gameObject.layer;

            RectTransform strip = (RectTransform)stripObject.transform;
            strip.SetParent(panel.rectTransform, false);
            strip.localScale = Vector3.one;
            strip.localRotation = Quaternion.identity;
            strip.anchorMin = new Vector2(1f, 1f);
            strip.anchorMax = new Vector2(1f, 1f);
            strip.pivot = new Vector2(1f, 1f);
            strip.anchoredPosition = new Vector2(-OverlayMargin, -OverlayMargin);
            strip.sizeDelta = new Vector2(0f, OverlayButtonHeight);
            strip.SetAsLastSibling();

            HorizontalLayoutGroup layout = stripObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleRight;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.spacing = OverlaySpacing;

            ContentSizeFitter fitter = stripObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return strip;
        }

        private void Bind(BasisMenuPanel panel, PanelButton move, PanelButton reset, PanelButton search, string panelKey)
        {
            _panel = panel;
            _panelRect = (RectTransform)panel.transform;
            _moveButton = move;
            _resetButton = reset;
            _searchButton = search;
            _panelKey = panelKey;
            _defaultLocalPosition = panel.Data.PanelPosition;

            if (reset != null)
            {
                reset.OnClicked += RequestReset;
            }
            if (search != null)
            {
                search.OnClicked += OpenSearch;
            }

            ApplyStoredOffset();
            RefreshResetAvailability();

            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            _bootModeHooked = true;
        }

        private void OnDisable()
        {
            EndDrag();
        }

        private void OnDestroy()
        {
            EndDrag();
            if (_bootModeHooked)
            {
                BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
                _bootModeHooked = false;
            }
            if (_moveButton != null)
            {
                _moveButton.OnResetRequested -= RequestReset;
            }
            if (_resetButton != null)
            {
                _resetButton.OnClicked -= RequestReset;
            }
            if (_searchButton != null)
            {
                _searchButton.OnClicked -= OpenSearch;
            }
        }

        /// <summary>Desktop and VR hold separate placements, so switching modes re-applies the other one.</summary>
        private void OnBootModeChanged(string mode)
        {
            EndDrag();
            ApplyStoredOffset();
            RefreshResetAvailability();
        }

        private void ApplyStoredOffset()
        {
            if (_panelRect == null)
            {
                return;
            }

            SetPanelLocalPosition(_defaultLocalPosition + BasisPanelOffsetMemory.Get(_panelKey));
        }

        /// <summary>
        /// Tells this panel's Reset button that the page currently on show has its own defaults to
        /// go back to — the settings tabs each do. Reset then asks which the user meant — page,
        /// placement, or both — instead of silently picking one; panels with nothing registered
        /// (the keyboard, dialogues, prompts) keep the plain behaviour, because there is no choice
        /// to offer.
        /// <para>
        /// <paramref name="changes"/> optionally lists what the page reset would touch — every
        /// setting currently off its default. It is asked again each time the dialogue opens, so it
        /// reflects what the user just finished tweaking, and the dialogue shows the rows so
        /// "reset this page" is a decision about known values rather than a guess.
        /// </para>
        /// <para>Call again whenever the page changes; the last registration wins.</para>
        /// </summary>
        public static void SetPanelReset(BasisMenuPanel panel, string label, string question, Action reset,
            Func<List<BasisMenuDialoguePanel.DetailRow>> changes = null)
        {
            if (panel == null)
            {
                return;
            }

            BasisPanelMoveHandle handle = panel.GetComponentInChildren<BasisPanelMoveHandle>(true);
            if (handle == null)
            {
                return;
            }

            handle._panelResetLabel = label;
            handle._panelResetQuestion = question;
            handle._panelReset = reset;
            handle._panelResetChanges = changes;
            handle.RefreshResetAvailability();
        }

        public static void RefreshDefaultPosition(BasisMenuPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            BasisPanelMoveHandle handle = panel.GetComponentInChildren<BasisPanelMoveHandle>(true);
            if (handle == null || handle._panel != panel)
            {
                return;
            }

            handle._defaultLocalPosition = panel.Data.PanelPosition;
            handle.ApplyStoredOffset();
        }

        private string _panelResetLabel;
        private string _panelResetQuestion;
        private Action _panelReset;
        private Func<List<BasisMenuDialoguePanel.DetailRow>> _panelResetChanges;

        public const string ResetChangedKey = "menu.panel.reset.changed";
        public const string ResetNoChangesKey = "menu.panel.reset.nochanges";

        /// <summary>
        /// The Reset button. With a page reset registered this is a fork — put the panel back where
        /// it was, put the page's settings back to defaults, or both — so it asks rather than
        /// guessing. The panel's Close button is the way out that resets nothing, so every button
        /// in the dialogue itself resets something and dismissing it must not be captured as a
        /// pending notification whose dismiss would fire the decline callback.
        /// </summary>
        private void RequestReset()
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
            if (_panelReset == null || menu == null)
            {
                ResetPlacement();
                return;
            }

            // OpenDialogue refuses while another modal is already up. Without this the
            // EnableAlternate below would graft a third button onto that unrelated dialogue.
            if (menu.Dialogue != null)
            {
                return;
            }

            string question = _panelResetQuestion;
            List<BasisMenuDialoguePanel.DetailRow> changes = _panelResetChanges?.Invoke();
            if (changes != null)
            {
                question += "\n\n" + (changes.Count > 0
                    ? BasisLocalization.Get(ResetChangedKey, changes.Count)
                    : BasisLocalization.Get(ResetNoChangesKey));
            }

            Action pageReset = _panelReset;
            menu.OpenDialogue(
                BasisLocalization.Get(ResetKey),
                question,
                _panelResetLabel,
                BasisLocalization.Get(ResetAllKey),
                accepted =>
                {
                    pageReset.Invoke();
                    if (!accepted)
                    {
                        ResetPlacement();
                    }
                });

            BasisMenuDialoguePanel dialogue = menu.Dialogue;
            if (dialogue != null)
            {
                dialogue.CaptureOnClose = false;
                dialogue.EnableAlternate(BasisLocalization.Get(ResetPositionKey), ResetPlacement);
                if (changes != null && changes.Count > 0)
                {
                    dialogue.ShowDetails(changes);
                }
            }
        }

        private void ResetPlacement()
        {
            EndDrag();
            BasisPanelOffsetMemory.Clear(_panelKey);
            SetPanelLocalPosition(_defaultLocalPosition);
            RefreshResetAvailability();
        }

        /// <summary>
        /// Slack on "has this panel been moved", in panel-local units: sideways a fraction of a
        /// millimetre at the menu's group scale, in depth a couple of millimetres. Below that the
        /// drag came back to where it started and left no placement worth undoing.
        /// </summary>
        private const float MovedEpsilon = 0.25f;

        private bool HasMovedPlacement =>
            BasisPanelOffsetMemory.Get(_panelKey).sqrMagnitude > MovedEpsilon * MovedEpsilon;

        /// <summary>
        /// Reset only ever puts something back: a placement the user dragged, or the defaults of a
        /// page that registered its own. A panel with neither has nothing to undo, so the button
        /// stays out of the header until it does — a control that visibly does nothing when pressed
        /// teaches the user to distrust the ones beside it.
        /// <para>
        /// The shared reset gesture — right click on desktop, thumbstick click in VR, which the move
        /// button answers so a user who learnt it on sliders finds it here too — is hooked and
        /// unhooked alongside. It is not merely ignored while unavailable because the move button's
        /// tooltip advertises the gesture off that same hook.
        /// </para>
        /// </summary>
        private void RefreshResetAvailability()
        {
            bool available = _panelReset != null || HasMovedPlacement;

            if (_resetButton != null)
            {
                _resetButton.gameObject.SetActive(available);
            }

            if (available == _resetGestureHooked || _moveButton == null)
            {
                return;
            }

            _resetGestureHooked = available;
            if (available)
            {
                _moveButton.OnResetRequested += RequestReset;
            }
            else
            {
                _moveButton.OnResetRequested -= RequestReset;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_dragging || _moveButton == null || !_moveButton.IsInteractable)
            {
                return;
            }

            BasisInput input = ResolveInput(eventData);
            if (input == null || !input.HasRaycaster || input.BasisUIRaycast == null)
            {
                return;
            }

            BeginDrag(input);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_dragging)
            {
                return;
            }

            // A second device tapping the button mid-drag must not cancel the hand that owns it.
            if (_draggingInput != null && ResolveInput(eventData) != _draggingInput)
            {
                return;
            }

            EndDrag();
        }

        /// <summary>
        /// Finds which device pressed us, by the same route the reset gesture uses to decide which
        /// hand a hovered control belongs to.
        /// </summary>
        private static BasisInput ResolveInput(PointerEventData eventData) =>
            BasisPanelResetGesture.ResolvePointerDevice(eventData);

        private void BeginDrag(BasisInput input)
        {
            Transform parent = _panelRect != null ? _panelRect.parent : null;
            if (parent == null)
            {
                return;
            }

            Ray ray = input.BasisUIRaycast.BasisPointRaycaster.ray;
            Vector3 panelLocal = _panelRect.localPosition;

            // A mouse has no depth axis to aim along, so desktop drags are pinned to the plane the
            // panel is already on. Same test the placement store keys off, so a drag can only ever
            // change the axes that platform saves.
            _lockDepth = BasisDeviceManagement.IsUserInDesktop();
            _lockedDepth = panelLocal.z;

            // Depth of the panel pivot measured along the ray, then the leftover sideways offset
            // held in local space. Keeping the offset means the panel does not snap its pivot onto
            // the pointer the moment the button is pressed.
            float scale = AvatarScale;
            _grabDistance = Mathf.Clamp(Vector3.Dot(_panelRect.position - ray.origin, ray.direction),
                MinGrabMetres * scale, MaxGrabMetres * scale);
            _grabLocalOffset = panelLocal - GetPointerLocal(parent, ray);


            _draggingInput = input;
            _dragging = true;

            // The stick that pushes the panel is also walk and snap-turn; while a drag owns it,
            // the player should not be doing either.
            _movementLock.Add(LockOwner);
            _lookLock.Add(LockOwner);

            BasisLocalPlayer.AfterSimulateOnRender.AddAction(TickPriority, Tick);
        }

        private void EndDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            _draggingInput = null;

            BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(TickPriority, Tick);

            _movementLock.Remove(LockOwner);
            _lookLock.Remove(LockOwner);

            if (_panelRect != null)
            {
                BasisPanelOffsetMemory.Set(_panelKey, _panelRect.localPosition - _defaultLocalPosition);
            }

            // A drag that actually went somewhere is what earns the panel its Reset.
            RefreshResetAvailability();
        }

        private void Tick()
        {
            if (!_dragging)
            {
                return;
            }

            // The panel can be released out from under a held drag (menu closed, provider switched),
            // and a controller can drop off mid-drag.
            if (_panel == null || _panel.IsReleased || _panelRect == null ||
                _draggingInput == null || !_draggingInput.HasRaycaster || _draggingInput.BasisUIRaycast == null)
            {
                EndDrag();
                return;
            }

            Transform parent = _panelRect.parent;
            if (parent == null)
            {
                EndDrag();
                return;
            }

            float scale = AvatarScale;
            _grabDistance = Mathf.Clamp(_grabDistance + ReadPushInput() * PushMetresPerSecond * scale * Time.unscaledDeltaTime,
                MinGrabMetres * scale, MaxGrabMetres * scale);


            Ray ray = _draggingInput.BasisUIRaycast.BasisPointRaycaster.ray;
            Vector3 target = GetPointerLocal(parent, ray) + _grabLocalOffset;
            if (_lockDepth)
            {
                target.z = _lockedDepth;
            }

            SetPanelLocalPosition(target);
        }

        /// <summary>
        /// Where the pointer is aiming, in the panel parent's local space. Depth-locked drags
        /// intersect the plane the panel sits on so it tracks the cursor exactly; free drags take a
        /// point along the ray at the distance the panel was grabbed from, which is what the
        /// thumbstick then reels in and out.
        /// </summary>
        private Vector3 GetPointerLocal(Transform parent, Ray ray)
        {
            if (_lockDepth)
            {
                // Local Z maps to the parent's forward axis even under the menu's non-uniform scale,
                // because that scale is diagonal in the parent's own frame.
                Plane plane = new Plane(parent.forward, parent.TransformPoint(new Vector3(0f, 0f, _lockedDepth)));
                if (plane.Raycast(ray, out float enter))
                {
                    return parent.InverseTransformPoint(ray.GetPoint(enter));
                }
            }

            return parent.InverseTransformPoint(ray.GetPoint(_grabDistance));
        }

        /// <summary>
        /// Forward/back on the thumbstick of the hand holding the button. Hands only: on desktop the
        /// same axis is the movement stick, and depth is locked there anyway.
        /// </summary>
        private float ReadPushInput()
        {
            if (_lockDepth || !_draggingInput.TryGetRole(out BasisBoneTrackedRole role))
            {
                return 0f;
            }

            if (role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand)
            {
                return 0f;
            }

            float stick = _draggingInput.CurrentInputState.Primary2DAxisDeadZoned.y;
            return Mathf.Abs(stick) < StickDeadZone ? 0f : stick;
        }

        private void SetPanelLocalPosition(Vector3 localPosition)
        {
            localPosition = _defaultLocalPosition +
                BasisPanelOffsetMemory.Clamp(localPosition - _defaultLocalPosition);

            Transform parent = _panelRect.parent;
            if (parent != null)
            {
                localPosition = ClampToScreen(parent, localPosition);
            }

            if (_panelRect.localPosition == localPosition)
            {
                return;
            }

            _panelRect.localPosition = localPosition;
            // Physics.autoSyncTransforms is off project-wide, so the panel's box collider would keep
            // answering rays from where the panel used to be and the drag would chase itself.
            BasisPhysicsSyncGate.MarkColliderMoved();
        }

        /// <summary>At most this much of the panel may hang past the left, right or bottom edge.</summary>
        private const float MaxOffScreenFraction = 0.2f;

        /// <summary>
        /// The top edge gets a far tighter allowance than the rest, because the move control lives
        /// in the panel's header: let the top slide off and there is nothing left on screen to pick
        /// the panel back up by. On a standard page this leaves roughly half the header row — and
        /// with it enough of the move button to grab — still on screen at the limit.
        /// </summary>
        private const float MaxOffScreenTopFraction = 0.05f;

        private static readonly Vector3[] _worldCorners = new Vector3[4];

        /// <summary>
        /// Stops a desktop drag from throwing the panel off the edge of the screen. VR has no screen
        /// edges to fall off, so this only applies where the panel is placed against a flat display.
        /// <para>
        /// Works in screen pixels, then converts the correction back through the panel's own
        /// on-screen size. That conversion is exact while the panel holds a fixed depth and stays
        /// parallel to the display — which is what the desktop depth lock guarantees.
        /// </para>
        /// </summary>
        private Vector3 ClampToScreen(Transform parent, Vector3 localPosition)
        {
            if (!BasisDeviceManagement.IsUserInDesktop() || !BasisLocalCameraDriver.HasInstance)
            {
                return localPosition;
            }

            Camera camera = BasisLocalCameraDriver.CameraInstance;
            if (camera == null)
            {
                return localPosition;
            }

            _panelRect.GetWorldCorners(_worldCorners);

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int Index = 0; Index < 4; Index++)
            {
                Vector3 point = camera.WorldToScreenPoint(_worldCorners[Index], Camera.MonoOrStereoscopicEye.Mono);
                if (point.z <= 0f)
                {
                    // Behind the camera — there is no meaningful screen rect to clamp against.
                    return localPosition;
                }
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            Vector2 size = max - min;
            Rect rect = _panelRect.rect;
            if (size.x <= 0f || size.y <= 0f || rect.width <= 0f || rect.height <= 0f)
            {
                return localPosition;
            }

            Vector3 currentScreen = camera.WorldToScreenPoint(_panelRect.position, Camera.MonoOrStereoscopicEye.Mono);
            Vector3 targetScreen = camera.WorldToScreenPoint(parent.TransformPoint(localPosition), Camera.MonoOrStereoscopicEye.Mono);
            Vector2 move = new Vector2(targetScreen.x - currentScreen.x, targetScreen.y - currentScreen.y);

            float slackX = size.x * MaxOffScreenFraction;
            float correctionX = PushInside(min.x + move.x, max.x + move.x, -slackX, Screen.width + slackX);
            // Screen Y runs bottom-up, so max.y is the panel's top edge and gets the tight allowance.
            float correctionY = PushInside(min.y + move.y, max.y + move.y,
                -size.y * MaxOffScreenFraction,
                Screen.height + size.y * MaxOffScreenTopFraction,
                pinUpper: true);
            if (correctionX == 0f && correctionY == 0f)
            {
                return localPosition;
            }

            localPosition.x += correctionX / (size.x / rect.width);
            localPosition.y += correctionY / (size.y / rect.height);
            return localPosition;
        }

        /// <summary>
        /// Pixels needed to push a span back inside [lower, upper]; zero when it already fits. A
        /// panel too big to fit at all violates both ends, so <paramref name="pinUpper"/> says which
        /// one wins: the vertical axis pins the top, keeping the header — and with it the move
        /// control — on screen rather than the footer.
        /// </summary>
        private static float PushInside(float min, float max, float lower, float upper, bool pinUpper = false)
        {
            if (pinUpper)
            {
                if (max > upper)
                {
                    return upper - max;
                }
                if (min < lower)
                {
                    return lower - min;
                }
                return 0f;
            }

            if (min < lower)
            {
                return lower - min;
            }
            if (max > upper)
            {
                return upper - max;
            }
            return 0f;
        }

        private static float AvatarScale
        {
            get
            {
                float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
                return float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f ? 1f : scale;
            }
        }
    }
}
