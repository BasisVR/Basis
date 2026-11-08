using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Basis;
using Basis.BasisUI;
using Basis.BasisUI.Styling;
using Basis.Scripts.Drivers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class BasisInspector : MonoBehaviour
{
    public const string SpawnUrl = "Inspector";
    const float PanelWide = 1600f, PanelHigh = 1000f, HandleArea = 80f, WorldWidth = 0.96f;
    const float Pad = 24f, BarHigh = 64f, TreeWide = 620f, RowHigh = 44f, RowGap = 4f, ScrollWide = 28f, Indent = 24f, FoldSize = 36f, IconSize = 26f;
    const float FieldInterval = 0.2f, NameInterval = 2f, ReadableDistance = 12f;
    const float TreeRefresh = 0.25f, ComponentAudit = 1f;
    const int WheelRows = 3, PreviewRowCount = 6, MaxListItems = 1024, MaxViews = 16;
    const float StripHigh = 44f;
    const double WarmBudgetMs = 1.0;
    enum EntryKind { Header, Field }
    struct Entry
    {
        public EntryKind Kind;
        public UnityEngine.Object Target;
        public BasisInspectorMember Member;
        public string Title;
    }
    sealed class Segment
    {
        public UnityEngine.Object Target;
        public Type Type;
        public string Title;
        public BasisInspectorMember[] Members;
        public BasisInspectorCategory Category;
        public bool Collapsed;
        public int Start;
    }
    sealed class View
    {
        public UnityEngine.Object Asset;
        public object[] Items;
        public BasisInspectorMember[] ItemMembers;
        public string Title;
        public int ReturnOffset;
    }
    sealed class TreeRow
    {
        public BasisWorldPanelKit.WorldButton Button, Fold;
        public Image Icon;
        public RectTransform LabelRect, IconRect;
        public Transform Shown;
        public int Depth = -1;
        public bool Selected, Expanded, Active = true, HasChildren;
        public BasisInspectorCategory Category = (BasisInspectorCategory)255;
    }
    sealed class FieldRow
    {
        public BasisWorldPanelKit.WorldButton Button;
        public Image Fold, Icon, Accent;
        public TextMeshProUGUI Value;
        public RectTransform NameRect, ValueRect;
        public EntryKind Kind = (EntryKind)(-1);
        public bool Collapsed, HasRaw;
        public string ShownValue;
        public UnityEngine.Object Target;
        public BasisInspectorMember Member;
        public object Raw;
        public Color Plain;
        public BasisInspectorTone Tone = (BasisInspectorTone)255;
        public BasisInspectorCategory Category = (BasisInspectorCategory)255;
    }
    public BoxCollider Handle;
    readonly BasisInspectorHierarchy hierarchy = new BasisInspectorHierarchy();
    readonly List<Scene> scenes = new List<Scene>(8);
    readonly List<Segment> segments = new List<Segment>(64);
    readonly Stack<Segment> segmentPool = new Stack<Segment>(64);
    readonly List<Component> components = new List<Component>(64), shownComponents = new List<Component>(64);
    readonly HashSet<UnityEngine.Object> toggled = new HashSet<UnityEngine.Object>();
    readonly Stopwatch warmClock = new Stopwatch();
    readonly HashSet<Type> warmSet = new HashSet<Type>();
    readonly List<Type> warmList = new List<Type>(64);
    Task warmTask;
    CancellationTokenSource prewarm;
    readonly List<TreeRow> treeRows = new List<TreeRow>(24);
    readonly List<FieldRow> fieldRows = new List<FieldRow>(24);
    GameObject root;
    BasisWorldPanelKit.WorldButton sceneButton;
    TextMeshProUGUI title, treeEmpty, fieldEmpty;
    Image titleIcon;
    BasisWorldPanelKit.WorldButton backButton;
    RawImage preview;
    Image previewCard;
    TextMeshProUGUI previewNote;
    Vector2 previewArea;
    Texture previewTexture;
    bool previewShown;
    int previewRows;
    readonly List<View> views = new List<View>(4);
    readonly BasisInspectorGizmos gizmos = new BasisInspectorGizmos();
    readonly List<BasisWorldPanelKit.WorldButton> gizmoButtons = new List<BasisWorldPanelKit.WorldButton>(8);
    readonly BasisInspectorGizmoKind[] gizmoOrder = { BasisInspectorGizmoKind.Axes, BasisInspectorGizmoKind.Bounds, BasisInspectorGizmoKind.Colliders, BasisInspectorGizmoKind.Ranges, BasisInspectorGizmoKind.Camera, BasisInspectorGizmoKind.Children, BasisInspectorGizmoKind.Label };
    readonly string[] gizmoKeys = { "inspector.gizmo.axes", "inspector.gizmo.bounds", "inspector.gizmo.colliders", "inspector.gizmo.ranges", "inspector.gizmo.camera", "inspector.gizmo.children", "inspector.gizmo.label" };
    BasisInspectorGizmoKind gizmoKinds = BasisInspectorGizmoKind.Axes | BasisInspectorGizmoKind.Bounds;
    string textShaderProperties, textNoPreview;
    View CurrentView => views.Count > 0 ? views[views.Count - 1] : null;
    readonly List<Component> categoryScratch = new List<Component>(16);
    Slider treeScroll, fieldScroll;
    readonly Plane[] frustum = new Plane[6];
    Scene current, dontDestroy, shownScene;
    Transform selected, headingFor;
    bool hadSelection, built, languageHooked, hidden, hasHeading;
    int treeOffset, fieldOffset, shownSceneIndex = -2, shownSceneCount = -1, entryCount, pendingTypes;
    float nextTree, nextField, nextNames, nextHeading, nextAudit;
    bool collapseComponents;
    string textTitle, textSelectPrompt, textSceneEmpty, textMissingScript;

    void Awake()
    {
        GameObject probe = new GameObject("Basis Inspector Probe");
        DontDestroyOnLoad(probe);
        dontDestroy = probe.scene;
        Destroy(probe);
        current = SceneManager.GetActiveScene();
        if (Handle == null) TryGetComponent(out Handle);
    }

    void CacheText()
    {
        textTitle = BasisLocalization.Get("library.embedded.inspector");
        textSelectPrompt = BasisLocalization.Get("inspector.selectPrompt");
        textSceneEmpty = BasisLocalization.Get("inspector.sceneEmpty");
        textMissingScript = BasisLocalization.Get("inspector.missingScript");
        textShaderProperties = BasisLocalization.Get("inspector.shaderProperties");
        textNoPreview = BasisLocalization.Get("inspector.noPreview");
    }

    void Start()
    {
        CacheText();
#if !UNITY_WEBGL || UNITY_EDITOR
        prewarm = new CancellationTokenSource();
        BasisInspectorReflection.PrewarmAsync(prewarm.Token);
#endif
        Build();
        BasisLocalization.OnLanguageChanged += OnLanguageChanged;
        languageHooked = true;
        RefreshTree();
        RebuildEntries();
        RefreshFields();
    }

    void OnDestroy()
    {
        if (languageHooked) BasisLocalization.OnLanguageChanged -= OnLanguageChanged;
        languageHooked = false;
        gizmos.Dispose();
        if (prewarm != null)
        {
            prewarm.Cancel();
            prewarm.Dispose();
            prewarm = null;
        }
        UnRegisterLoadedNetID(gameObject.name);
    }

    static async void UnRegisterLoadedNetID(string loadedNetId)
    {
        if (string.IsNullOrEmpty(loadedNetId)) return;
        if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(loadedNetId, out GameObject go) || !go) return;
        if (!await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(loadedNetId)) BasisDebug.LogError($"failed to remove item = {loadedNetId} from registry");
    }

    void Update()
    {
        if (!built) return;
        gizmos.Draw(selected, shownComponents, gizmoKinds, BasisLocalCameraDriver.Position);
        if (!InView())
        {
            hidden = true;
            return;
        }
        if (hidden)
        {
            hidden = false;
            nextTree = nextField = nextNames = nextHeading = 0f;
        }
        float now = Time.unscaledTime;
        if (now >= nextTree) RefreshTree();
        if (pendingTypes > 0 && WarmTypes()) nextField = 0f;
        if (now < nextField) return;
        if (hadSelection && selected == null) ClearSelection();
        else if (ComponentsChanged(now)) RebuildEntries();
        RefreshFields();
    }

    bool InView()
    {
        Camera view = BasisLocalCameraDriver.CameraInstance;
        if (view == null) return true;
        Transform self = transform;
        float size = WorldWidth * Mathf.Abs(self.lossyScale.x);
        Vector3 center = self.position;
        float reach = ReadableDistance * Mathf.Max(size / WorldWidth, 0.1f) + size;
        if ((view.transform.position - center).sqrMagnitude > reach * reach) return false;
        GeometryUtility.CalculateFrustumPlanes(view, frustum);
        return GeometryUtility.TestPlanesAABB(frustum, new Bounds(center, new Vector3(size, size, size)));
    }

    static void FixedSize(TextMeshProUGUI label)
    {
        label.enableAutoSizing = false;
        label.fontSize = label.fontSizeMax;
    }

    void OnLanguageChanged()
    {
        BasisInspectorReflection.ResetLabels();
        CacheText();
        hasHeading = false;
        shownSceneIndex = -2;
        nextNames = 0f;
        previewTexture = null;
        previewShown = false;
        RefreshGizmoButtons(true);
        RebuildEntries();
        foreach (FieldRow row in fieldRows)
        {
            row.ShownValue = null;
            row.HasRaw = false;
        }
        RefreshTree();
        RefreshFields();
    }

    void Build()
    {
        built = true;
        float scale = WorldWidth / PanelWide;
        RectTransform canvas = BasisWorldPanelKit.CreateCanvas("Inspector Panel", transform, new Vector2(PanelWide, PanelHigh + HandleArea), out root);
        canvas.localScale = Vector3.one * scale;
        canvas.localPosition = Vector3.zero;
        canvas.localRotation = Quaternion.identity;
        RectTransform body = BasisWorldPanelKit.CreateRect("Body", canvas, new Vector2(0f, HandleArea * 0.5f), new Vector2(PanelWide, PanelHigh));
        RectTransform window = BasisWorldPanelKit.CreateWindow(body);
        Image grip = BasisWorldPanelKit.CreateStyledImage("Handle", canvas, new Vector2(0f, -(PanelHigh + HandleArea) * 0.5f + HandleArea * 0.5f), new Vector2(360f, 36f), BasisWorldPanelKit.StyleButton, new Color(0.43f, 0.41f, 0.57f, 1f));
        if (Handle != null)
        {
            Handle.center = new Vector3(0f, grip.rectTransform.anchoredPosition.y * scale, 0f);
            Handle.size = new Vector3(400f * scale, 60f * scale, 0.03f);
        }
        float half = PanelWide * 0.5f, top = PanelHigh * 0.5f, barY = top - Pad - BarHigh * 0.5f;
        sceneButton = BasisWorldPanelKit.CreateButton(window, new Vector2(-half + Pad + TreeWide * 0.5f, barY), new Vector2(TreeWide, BarHigh), BasisInspectorStyle.Icon(BasisInspectorCategory.Scene), string.Empty, BasisWorldPanelKit.StyleButton, OnScenePressed);
        if (sceneButton.Icon != null) sceneButton.Icon.color = BasisInspectorStyle.Tint(BasisInspectorCategory.Scene);
        float closeX = half - Pad - BarHigh * 0.5f;
        BasisWorldPanelKit.CreateButton(window, new Vector2(closeX, barY), new Vector2(BarHigh, BarHigh), BasisWorldPanelKit.AddressableIcon("Packages/com.basis.sdk/Textures/Runtime/xWhite.png"), null, BasisWorldPanelKit.StyleButtonDanger, OnClosePressed);
        float titleLeft = -half + Pad + TreeWide + Pad, titleRight = closeX - BarHigh * 0.5f - 16f;
        titleIcon = CreateIcon(window, new Vector2(titleLeft + BarHigh * 0.5f, barY), 36f);
        backButton = BasisWorldPanelKit.CreateButton(window, new Vector2(titleLeft + BarHigh * 0.5f, barY), new Vector2(BarHigh, BarHigh), BasisWorldPanelKit.PlayIcon, null, BasisWorldPanelKit.StyleButton, OnBackPressed);
        if (backButton.Icon != null) backButton.Icon.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
        backButton.Background.gameObject.SetActive(false);
        float titleStart = titleLeft + BarHigh + 12f;
        title = BasisWorldPanelKit.CreateLabel(window, new Vector2((titleStart + titleRight) * 0.5f, barY), new Vector2(titleRight - titleStart, BarHigh), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Left, 1.3f);
        float paneTop = top - Pad - BarHigh - 12f, paneBottom = -top + Pad, paneHigh = paneTop - paneBottom, paneY = (paneTop + paneBottom) * 0.5f;
        RectTransform treePane = CreatePane("Hierarchy", window, new Vector2(-half + Pad + TreeWide * 0.5f, paneY), new Vector2(TreeWide, paneHigh), OnTreeWheel);
        float fieldWide = PanelWide - Pad * 3f - TreeWide;
        float stripGap = 8f, gizmoWide = (fieldWide - stripGap * (gizmoOrder.Length - 1)) / gizmoOrder.Length;
        for (int i = 0; i < gizmoOrder.Length; i++)
        {
            int index = i;
            gizmoButtons.Add(BasisWorldPanelKit.CreateButton(window, new Vector2(titleLeft + gizmoWide * 0.5f + i * (gizmoWide + stripGap), paneTop - StripHigh * 0.5f), new Vector2(gizmoWide, StripHigh), null, string.Empty, BasisWorldPanelKit.StyleButton, () => OnGizmoPressed(index)));
        }
        RefreshGizmoButtons(true);
        float fieldTop = paneTop - StripHigh - stripGap, fieldHigh = fieldTop - paneBottom, fieldY = (fieldTop + paneBottom) * 0.5f;
        RectTransform fieldPane = CreatePane("Components", window, new Vector2(titleLeft + fieldWide * 0.5f, fieldY), new Vector2(fieldWide, fieldHigh), OnFieldWheel);
        int rows = Mathf.Max(1, Mathf.FloorToInt((paneHigh - 16f + RowGap) / (RowHigh + RowGap))), fieldRowCount = Mathf.Max(PreviewRowCount + 2, Mathf.FloorToInt((fieldHigh - 16f + RowGap) / (RowHigh + RowGap)));
        float treeRowWide = TreeWide - 24f - ScrollWide, fieldRowWide = fieldWide - 24f - ScrollWide;
        for (int i = 0; i < rows; i++) treeRows.Add(CreateTreeRow(treePane, new Vector2(-TreeWide * 0.5f + 8f + treeRowWide * 0.5f, paneHigh * 0.5f - 8f - RowHigh * 0.5f - i * (RowHigh + RowGap)), treeRowWide, i));
        for (int i = 0; i < fieldRowCount; i++) fieldRows.Add(CreateFieldRow(fieldPane, new Vector2(-fieldWide * 0.5f + 8f + fieldRowWide * 0.5f, fieldHigh * 0.5f - 8f - RowHigh * 0.5f - i * (RowHigh + RowGap)), fieldRowWide, i));
        float previewHigh = PreviewRowCount * (RowHigh + RowGap) - RowGap;
        previewCard = BasisWorldPanelKit.CreateStyledImage("Preview", fieldPane, new Vector2(-fieldWide * 0.5f + 8f + fieldRowWide * 0.5f, fieldHigh * 0.5f - 8f - previewHigh * 0.5f), new Vector2(fieldRowWide, previewHigh), BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
        previewArea = new Vector2(fieldRowWide - 16f, previewHigh - 16f);
        preview = BasisWorldPanelKit.CreateRect("Texture", previewCard.rectTransform, Vector2.zero, previewArea).gameObject.AddComponent<RawImage>();
        preview.raycastTarget = false;
        previewNote = BasisWorldPanelKit.CreateLabel(previewCard.rectTransform, Vector2.zero, new Vector2(previewArea.x, RowHigh), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        previewCard.gameObject.SetActive(false);
        treeScroll = CreateScrollbar(treePane, new Vector2(TreeWide * 0.5f - 8f - ScrollWide * 0.5f, 0f), paneHigh - 16f, OnTreeScroll);
        fieldScroll = CreateScrollbar(fieldPane, new Vector2(fieldWide * 0.5f - 8f - ScrollWide * 0.5f, 0f), fieldHigh - 16f, OnFieldScroll);
        treeEmpty = BasisWorldPanelKit.CreateLabel(treePane, Vector2.zero, new Vector2(TreeWide - 48f, RowHigh), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        fieldEmpty = BasisWorldPanelKit.CreateLabel(fieldPane, Vector2.zero, new Vector2(fieldWide - 48f, RowHigh), BasisWorldPanelKit.StyleSubtitle, TextAlignmentOptions.Center);
        root.SetActive(true);
    }

    static Image CreateIcon(Transform parent, Vector2 position, float size)
    {
        RectTransform rect = BasisWorldPanelKit.CreateRect("Icon", parent, position, new Vector2(size, size));
        Image image = rect.gameObject.AddComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;
        image.type = Image.Type.Simple;
        image.enabled = false;
        return image;
    }

    static void SetIcon(Image image, BasisInspectorCategory category)
    {
        Sprite sprite = BasisInspectorStyle.Icon(category);
        if (image.sprite != sprite) image.sprite = sprite;
        image.color = BasisInspectorStyle.Tint(category);
        image.enabled = sprite != null;
    }

    RectTransform CreatePane(string name, Transform parent, Vector2 position, Vector2 size, Action<float> wheel)
    {
        Image pane = BasisWorldPanelKit.CreateStyledImage(name, parent, position, size, BasisWorldPanelKit.StyleTrack, new Color(0.09f, 0.09f, 0.11f, 1f), true);
        pane.gameObject.AddComponent<BasisInspectorScroll>().Scrolled = wheel;
        return pane.rectTransform;
    }

    Slider CreateScrollbar(Transform parent, Vector2 position, float height, UnityAction<float> changed)
    {
        RectTransform rect = BasisWorldPanelKit.CreateRect("Scrollbar", parent, position, new Vector2(ScrollWide, height));
        Image hit = rect.gameObject.AddComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, 0f);
        hit.raycastTarget = true;
        Image track = BasisWorldPanelKit.CreateStyledImage("Track", rect, Vector2.zero, Vector2.zero, BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
        track.rectTransform.anchorMin = new Vector2(0.5f, 0f);
        track.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        track.rectTransform.sizeDelta = new Vector2(10f, 0f);
        track.pixelsPerUnitMultiplier = 512f / 10f * 0.5f;
        RectTransform area = BasisWorldPanelKit.CreateRect("Handle Area", rect, Vector2.zero, Vector2.zero);
        BasisWorldPanelKit.Stretch(area);
        area.offsetMin = new Vector2(0f, 36f);
        area.offsetMax = new Vector2(0f, -36f);
        Image handle = BasisWorldPanelKit.CreateStyledImage("Handle", area, Vector2.zero, new Vector2(ScrollWide - 4f, 72f), BasisWorldPanelKit.StyleButton, new Color(0.43f, 0.41f, 0.57f, 1f), true);
        handle.pixelsPerUnitMultiplier = 4f;
        handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        handle.rectTransform.sizeDelta = new Vector2(ScrollWide - 4f, 72f);
        Slider slider = rect.gameObject.AddComponent<Slider>();
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.BottomToTop;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.SetValueWithoutNotify(1f);
        slider.onValueChanged.AddListener(changed);
        rect.gameObject.AddComponent<UiStyleSelectable>().SetStyle(BasisWorldPanelKit.StyleHover);
        return slider;
    }

    TreeRow CreateTreeRow(Transform parent, Vector2 position, float wide, int index)
    {
        TreeRow row = new TreeRow();
        row.Button = BasisWorldPanelKit.CreateButton(parent, position, new Vector2(wide, RowHigh), null, string.Empty, BasisWorldPanelKit.StyleCard, () => OnTreeRowPressed(index));
        row.Button.Label.alignment = TextAlignmentOptions.Left;
        FixedSize(row.Button.Label);
        row.LabelRect = row.Button.Label.rectTransform;
        row.Fold = BasisWorldPanelKit.CreateButton(row.Button.Background.transform, Vector2.zero, new Vector2(FoldSize, FoldSize), BasisWorldPanelKit.PlayIcon, null, null, () => OnFoldPressed(index));
        row.Fold.Background.color = new Color(0f, 0f, 0f, 0f);
        row.Icon = CreateIcon(row.Button.Background.transform, Vector2.zero, IconSize);
        row.IconRect = (RectTransform)row.Icon.transform;
        row.Button.Background.gameObject.SetActive(false);
        return row;
    }

    FieldRow CreateFieldRow(Transform parent, Vector2 position, float wide, int index)
    {
        FieldRow row = new FieldRow();
        row.Button = BasisWorldPanelKit.CreateButton(parent, position, new Vector2(wide, RowHigh), null, string.Empty, BasisWorldPanelKit.StyleCard, () => OnFieldRowPressed(index));
        row.Button.Label.alignment = TextAlignmentOptions.Left;
        FixedSize(row.Button.Label);
        row.NameRect = row.Button.Label.rectTransform;
        Transform background = row.Button.Background.transform;
        row.Accent = BasisWorldPanelKit.CreateRect("Accent", background, new Vector2(-wide * 0.5f + 5f, 0f), new Vector2(6f, RowHigh - 10f)).gameObject.AddComponent<Image>();
        row.Accent.raycastTarget = false;
        row.Accent.gameObject.SetActive(false);
        row.Fold = BasisWorldPanelKit.CreateStyledImage("Fold", background, new Vector2(-wide * 0.5f + 26f, 0f), new Vector2(24f, 24f), BasisWorldPanelKit.StyleIcon, Color.white);
        row.Fold.sprite = BasisWorldPanelKit.PlayIcon;
        row.Fold.type = Image.Type.Simple;
        row.Fold.preserveAspect = true;
        row.Icon = CreateIcon(background, new Vector2(-wide * 0.5f + 58f, 0f), IconSize);
        row.Value = BasisWorldPanelKit.CreateLabel(background, Vector2.zero, new Vector2(wide * 0.5f, RowHigh - 6f), BasisWorldPanelKit.StyleButtonLabel, TextAlignmentOptions.Left);
        FixedSize(row.Value);
        row.Plain = row.Value.color;
        if (row.Value.TryGetComponent(out UiStyleLabel style)) Destroy(style);
        row.ValueRect = row.Value.rectTransform;
        row.Button.Background.gameObject.SetActive(false);
        return row;
    }

    static void Place(RectTransform rect, float left, float right)
    {
        rect.anchoredPosition = new Vector2((left + right) * 0.5f, 0f);
        rect.sizeDelta = new Vector2(Mathf.Max(10f, right - left), RowHigh - 6f);
    }

    void RefreshScenes()
    {
        scenes.Clear();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isLoaded) scenes.Add(scene);
        }
        if (dontDestroy.IsValid() && dontDestroy.isLoaded && !scenes.Contains(dontDestroy)) scenes.Add(dontDestroy);
        if (!scenes.Contains(current)) current = scenes.Count > 0 ? scenes[0] : default;
    }

    void RefreshTree() => RefreshTree(true);

    void RefreshTree(bool retry)
    {
        float now = Time.unscaledTime;
        nextTree = now + TreeRefresh;
        bool names = now >= nextNames;
        if (names) nextNames = now + NameInterval;
        RefreshScenes();
        hierarchy.Refresh(current, now);
        int sceneIndex = scenes.IndexOf(current);
        if (names || sceneIndex != shownSceneIndex || scenes.Count != shownSceneCount || current != shownScene)
        {
            shownScene = current;
            shownSceneIndex = sceneIndex;
            shownSceneCount = scenes.Count;
            string sceneName = current.IsValid() ? current.name : BasisInspectorReflection.TextNone;
            sceneButton.SetText(scenes.Count > 1 ? BasisLocalization.Get("inspector.scene.count", sceneName, sceneIndex + 1, scenes.Count) : BasisLocalization.Get("inspector.scene", sceneName));
        }
        int max = Mathf.Max(0, hierarchy.Count - treeRows.Count);
        treeOffset = Mathf.Clamp(treeOffset, 0, max);
        SyncScrollbar(treeScroll, treeOffset, max);
        string empty = hierarchy.Count == 0 ? textSceneEmpty : string.Empty;
        if (!ReferenceEquals(treeEmpty.text, empty) && treeEmpty.text != empty) treeEmpty.text = empty;
        for (int i = 0; i < treeRows.Count; i++)
        {
            int index = treeOffset + i;
            TreeRow row = treeRows[i];
            GameObject rowObject = row.Button.Background.gameObject;
            if (!hierarchy.TryGet(index, out BasisInspectorHierarchy.Node node))
            {
                if (rowObject.activeSelf) rowObject.SetActive(false);
                row.Shown = null;
                continue;
            }
            if (!rowObject.activeSelf) rowObject.SetActive(true);
            Transform target = node.Transform;
            bool isSelected = target == selected, expanded = node.HasChildren && hierarchy.Expanded.Contains(target), active = target.gameObject.activeInHierarchy;
            if (row.Depth != node.Depth || row.HasChildren != node.HasChildren)
            {
                float wide = row.Button.Background.rectTransform.sizeDelta.x, left = -wide * 0.5f + 8f + node.Depth * Indent;
                ((RectTransform)row.Fold.Background.transform).anchoredPosition = new Vector2(left + FoldSize * 0.5f, 0f);
                row.IconRect.anchoredPosition = new Vector2(left + FoldSize + 4f + IconSize * 0.5f, 0f);
                Place(row.LabelRect, left + FoldSize + IconSize + 12f, wide * 0.5f - 12f);
                row.Fold.Background.gameObject.SetActive(node.HasChildren);
                row.Depth = node.Depth;
                row.HasChildren = node.HasChildren;
            }
            bool moved = row.Shown != target;
            if (moved || row.Expanded != expanded) row.Fold.Background.transform.localRotation = Quaternion.Euler(0f, 0f, expanded ? -90f : 0f);
            row.Expanded = expanded;
            row.Shown = target;
            if (moved || names)
            {
                row.Button.SetText(BasisInspectorReflection.Clip(target.name));
                BasisInspectorCategory category = BasisInspectorStyle.GameObjectCategory(target.gameObject, categoryScratch);
                if (category != row.Category)
                {
                    row.Category = category;
                    SetIcon(row.Icon, category);
                }
            }
            if (row.Selected != isSelected) row.Button.SetStyle(isSelected ? BasisWorldPanelKit.StyleButtonAccent : BasisWorldPanelKit.StyleCard);
            row.Selected = isSelected;
            if (row.Active != active)
            {
                float alpha = active ? 1f : 0.45f;
                row.Button.Label.alpha = alpha;
                row.Icon.canvasRenderer.SetAlpha(alpha);
            }
            row.Active = active;
        }
        if (retry && hierarchy.RootsStale && hierarchy.RefillIfStale(now)) RefreshTree(false);
    }

    bool ComponentsChanged(float now)
    {
        if (selected == null) return false;
        GameObject go = selected.gameObject;
        int count = go.GetComponentCount();
        if (count != shownComponents.Count) return true;
        if (now < nextAudit) return false;
        nextAudit = now + ComponentAudit;
        for (int i = 0; i < count; i++) if (go.GetComponentAtIndex(i) != shownComponents[i]) return true;
        return false;
    }

    void RentSegment(UnityEngine.Object target, Type type, string title, BasisInspectorMember[] members, BasisInspectorCategory category)
    {
        Segment segment = segmentPool.Count > 0 ? segmentPool.Pop() : new Segment();
        segment.Target = target;
        segment.Type = type;
        segment.Title = title;
        segment.Members = members;
        segment.Category = category;
        segment.Collapsed = target != null && (collapseComponents && !(target is GameObject) && !(target is Transform)) != toggled.Contains(target);
        segments.Add(segment);
    }

    void RebuildEntries()
    {
        foreach (Segment segment in segments)
        {
            segment.Target = null;
            segment.Members = null;
            segmentPool.Push(segment);
        }
        segments.Clear();
        pendingTypes = 0;
        entryCount = 0;
        View view = CurrentView;
        if (view != null)
        {
            BuildView(view);
            return;
        }
        shownComponents.Clear();
        if (selected == null) return;
        GameObject go = selected.gameObject;
        components.Clear();
        go.GetComponents(components);
        shownComponents.AddRange(components);
        collapseComponents = true;
        RentSegment(go, typeof(GameObject), nameof(GameObject), BasisInspectorReflection.GameObjectMembers, BasisInspectorCategory.GameObject);
        foreach (Component component in components)
        {
            if (component == null)
            {
                RentSegment(null, null, textMissingScript, Array.Empty<BasisInspectorMember>(), BasisInspectorCategory.Missing);
                continue;
            }
            Type type = component.GetType();
            bool cached = BasisInspectorReflection.IsCached(type);
            if (!cached) pendingTypes++;
            RentSegment(component, type, BasisInspectorReflection.TypeName(type), cached ? BasisInspectorReflection.GetMembers(type) : null, BasisInspectorStyle.Classify(type));
        }
        Recount();
        StartWarm();
    }

    void BuildView(View view)
    {
        collapseComponents = false;
        if (view.Items != null)
        {
            RentSegment(this, null, view.Title, view.ItemMembers, BasisInspectorCategory.Asset);
            Recount();
            return;
        }
        UnityEngine.Object asset = view.Asset;
        if (asset == null)
        {
            Recount();
            return;
        }
        Type type = asset.GetType();
        bool cached = BasisInspectorReflection.IsCached(type);
        if (!cached) pendingTypes++;
        RentSegment(asset, type, BasisInspectorReflection.TypeName(type), cached ? BasisInspectorReflection.GetMembers(type) : null, BasisInspectorStyle.Classify(type));
        if (asset is Material material) RentSegment(material, null, textShaderProperties, BasisInspectorReflection.ShaderMembers(material.shader), BasisInspectorCategory.Material);
        Recount();
        StartWarm();
    }

    void PushView(View view)
    {
        if (views.Count >= MaxViews) views.RemoveAt(0);
        view.ReturnOffset = fieldOffset;
        views.Add(view);
        fieldOffset = 0;
        hasHeading = false;
        RebuildEntries();
        RefreshFields();
    }

    void OnBackPressed()
    {
        if (views.Count == 0) return;
        View view = views[views.Count - 1];
        views.RemoveAt(views.Count - 1);
        fieldOffset = view.ReturnOffset;
        hasHeading = false;
        RebuildEntries();
        RefreshFields();
    }

    void OnGizmoPressed(int index)
    {
        gizmoKinds ^= gizmoOrder[index];
        RefreshGizmoButtons(false);
    }

    void RefreshGizmoButtons(bool text)
    {
        for (int i = 0; i < gizmoButtons.Count; i++)
        {
            if (text) gizmoButtons[i].SetText(BasisLocalization.Get(gizmoKeys[i]));
            gizmoButtons[i].SetStyle((gizmoKinds & gizmoOrder[i]) != 0 ? BasisWorldPanelKit.StyleButtonAccent : BasisWorldPanelKit.StyleButton);
        }
    }

    void RefreshPreview()
    {
        Texture texture = CurrentView?.Asset as Texture;
        bool show = texture != null;
        previewRows = show ? PreviewRowCount : 0;
        if (show == previewShown && texture == previewTexture) return;
        previewShown = show;
        previewTexture = texture;
        previewCard.gameObject.SetActive(show);
        if (!show)
        {
            preview.texture = null;
            return;
        }
        bool flat = texture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D;
        preview.gameObject.SetActive(flat);
        previewNote.text = flat ? string.Empty : textNoPreview;
        if (!flat)
        {
            preview.texture = null;
            return;
        }
        preview.texture = texture;
        float aspect = texture.height > 0 ? texture.width / (float)texture.height : 1f;
        Vector2 size = previewArea;
        if (size.x / size.y > aspect) size.x = size.y * aspect;
        else size.y = size.x / aspect;
        preview.rectTransform.sizeDelta = size;
    }

    void StartWarm()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (pendingTypes == 0 || warmTask != null && !warmTask.IsCompleted) return;
        warmSet.Clear();
        warmList.Clear();
        foreach (Segment segment in segments) if (segment.Members == null && segment.Type != null && warmSet.Add(segment.Type)) warmList.Add(segment.Type);
        if (warmList.Count > 0) warmTask = BasisInspectorReflection.WarmAsync(warmList.ToArray());
#endif
    }

    void Recount()
    {
        int start = 0;
        foreach (Segment segment in segments)
        {
            segment.Start = start;
            start += 1 + (segment.Collapsed || segment.Members == null ? 0 : segment.Members.Length);
        }
        entryCount = start;
    }

    int SegmentAt(int index)
    {
        int low = 0, high = segments.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) >> 1;
            if (segments[mid].Start <= index) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    bool TryGetEntry(int index, out Entry entry, out Segment segment)
    {
        entry = default;
        segment = null;
        if (index < 0 || index >= entryCount || segments.Count == 0) return false;
        segment = segments[SegmentAt(index)];
        int member = index - segment.Start - 1;
        if (member < 0) entry = new Entry { Kind = EntryKind.Header, Target = segment.Target, Title = segment.Title };
        else entry = new Entry { Kind = EntryKind.Field, Target = segment.Target, Member = segment.Members[member] };
        return true;
    }

    bool WarmTypes()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        if (warmTask != null)
        {
            if (!warmTask.IsCompleted) return false;
            if (warmTask.IsFaulted) BasisDebug.LogWarning($"[Inspector] background type scan failed: {warmTask.Exception?.GetBaseException().Message}");
            warmTask = null;
            bool filled = false;
            foreach (Segment segment in segments)
            {
                if (segment.Members != null || segment.Type == null || !BasisInspectorReflection.IsCached(segment.Type)) continue;
                segment.Members = BasisInspectorReflection.GetMembers(segment.Type);
                pendingTypes--;
                filled = true;
            }
            if (filled) Recount();
            if (pendingTypes == 0 || filled)
            {
                StartWarm();
                return filled;
            }
        }
#endif
        warmClock.Restart();
        int first = segments.Count > 0 ? SegmentAt(fieldOffset) : 0;
        bool changed = false;
        for (int n = 0; n < segments.Count && pendingTypes > 0; n++)
        {
            Segment segment = segments[(first + n) % segments.Count];
            if (segment.Members != null || segment.Type == null) continue;
            BasisInspectorMember[] members = BasisInspectorReflection.GetMembers(segment.Type);
            foreach (Segment other in segments)
            {
                if (other.Members != null || other.Type != segment.Type) continue;
                other.Members = members;
                pendingTypes--;
            }
            changed = true;
            if (warmClock.Elapsed.TotalMilliseconds >= WarmBudgetMs) break;
        }
        if (changed) Recount();
        return changed;
    }

    void RefreshFields()
    {
        float now = Time.unscaledTime;
        nextField = now + FieldInterval;
        if (selected != headingFor || !hasHeading || now >= nextHeading)
        {
            headingFor = selected;
            hasHeading = true;
            nextHeading = now + NameInterval;
            View view = CurrentView;
            string heading = view != null ? BasisInspectorReflection.Clip(view.Asset != null ? view.Asset.name : view.Title) : selected != null ? BasisInspectorReflection.Clip(selected.name) : textTitle;
            if (title.text != heading) title.text = heading;
            backButton.Background.gameObject.SetActive(view != null);
            if (view != null) titleIcon.enabled = false;
            else SetIcon(titleIcon, selected != null ? BasisInspectorStyle.GameObjectCategory(selected.gameObject, categoryScratch) : BasisInspectorCategory.None);
            string empty = selected == null ? textSelectPrompt : string.Empty;
            if (fieldEmpty.text != empty) fieldEmpty.text = empty;
        }
        RefreshPreview();
        int max = Mathf.Max(0, entryCount - (fieldRows.Count - previewRows));
        fieldOffset = Mathf.Clamp(fieldOffset, 0, max);
        SyncScrollbar(fieldScroll, fieldOffset, max);
        for (int i = 0; i < fieldRows.Count; i++)
        {
            int index = fieldOffset + i - previewRows;
            FieldRow row = fieldRows[i];
            GameObject rowObject = row.Button.Background.gameObject;
            if (i < previewRows || !TryGetEntry(index, out Entry entry, out Segment owner))
            {
                if (rowObject.activeSelf) rowObject.SetActive(false);
                continue;
            }
            if (!rowObject.activeSelf) rowObject.SetActive(true);
            float wide = row.Button.Background.rectTransform.sizeDelta.x, half = wide * 0.5f;
            if (row.Kind != entry.Kind)
            {
                row.Kind = entry.Kind;
                bool header = entry.Kind == EntryKind.Header;
                row.Fold.gameObject.SetActive(header);
                row.Accent.gameObject.SetActive(header);
                row.Icon.gameObject.SetActive(header);
                row.Button.SetStyle(header ? BasisWorldPanelKit.StyleButton : BasisWorldPanelKit.StyleCard);
                if (header)
                {
                    Place(row.NameRect, -half + 80f, half * 0.4f);
                    Place(row.ValueRect, half * 0.4f + 8f, half - 16f);
                    row.Value.alignment = TextAlignmentOptions.Right;
                }
                else
                {
                    Place(row.NameRect, -half + 48f, -half + wide * 0.4f);
                    Place(row.ValueRect, -half + wide * 0.4f + 12f, half - 16f);
                    row.Value.alignment = TextAlignmentOptions.Left;
                }
                row.ShownValue = null;
            }
            if (!ReferenceEquals(row.Target, entry.Target) || row.Member != entry.Member)
            {
                row.Target = entry.Target;
                row.Member = entry.Member;
                row.HasRaw = false;
                row.Raw = null;
                row.ShownValue = null;
            }
            string value;
            BasisInspectorTone tone;
            if (entry.Kind == EntryKind.Header)
            {
                row.Button.SetText(entry.Title);
                bool isCollapsed = owner.Collapsed;
                if (row.Collapsed != isCollapsed || row.ShownValue == null) row.Fold.rectTransform.localRotation = Quaternion.Euler(0f, 0f, isCollapsed ? 0f : -90f);
                row.Collapsed = isCollapsed;
                if (row.Category != owner.Category)
                {
                    row.Category = owner.Category;
                    SetIcon(row.Icon, owner.Category);
                    row.Accent.color = BasisInspectorStyle.Tint(owner.Category);
                }
                if (entry.Target is Component component)
                {
                    value = BasisInspectorReflection.ComponentState(component);
                    tone = BasisInspectorStyle.StateTone(component);
                }
                else
                {
                    value = string.Empty;
                    tone = BasisInspectorTone.Plain;
                }
            }
            else
            {
                row.Button.SetText(entry.Member.Label);
                if (entry.Target == null)
                {
                    value = BasisInspectorReflection.TextMissing;
                    tone = BasisInspectorTone.Missing;
                }
                else if (BasisInspectorReflection.TryGet(entry.Member, entry.Target, out object raw, out value))
                {
                    if (row.HasRaw && BasisInspectorReflection.Same(row.Raw, raw)) continue;
                    row.HasRaw = true;
                    row.Raw = raw;
                    value = BasisInspectorReflection.Format(raw);
                    tone = BasisInspectorStyle.ToneOf(raw);
                    if (raw is Color || raw is Color32)
                    {
                        Color swatch = raw is Color color ? color : (Color)(Color32)raw;
                        swatch.a = 1f;
                        row.Value.color = swatch;
                        row.Tone = (BasisInspectorTone)254;
                        if (row.ShownValue == value) continue;
                        row.ShownValue = value;
                        row.Value.text = value;
                        continue;
                    }
                }
                else
                {
                    row.HasRaw = false;
                    tone = entry.Member.Hidden ? BasisInspectorTone.Hidden : BasisInspectorTone.Unreadable;
                }
            }
            if (row.Tone != tone)
            {
                row.Tone = tone;
                row.Value.color = BasisInspectorStyle.ToneColor(tone, row.Plain);
            }
            if (row.ShownValue == value) continue;
            row.ShownValue = value;
            row.Value.text = value;
        }
    }

    static void SyncScrollbar(Slider slider, int offset, int max)
    {
        bool show = max > 0;
        if (slider.gameObject.activeSelf != show) slider.gameObject.SetActive(show);
        if (show) slider.SetValueWithoutNotify(1f - offset / (float)max);
    }

    void OnTreeScroll(float value)
    {
        int max = Mathf.Max(0, hierarchy.Count - treeRows.Count);
        int offset = Mathf.RoundToInt((1f - value) * max);
        if (offset == treeOffset) return;
        treeOffset = offset;
        RefreshTree();
    }

    void OnFieldScroll(float value)
    {
        int max = Mathf.Max(0, entryCount - (fieldRows.Count - previewRows));
        int offset = Mathf.RoundToInt((1f - value) * max);
        if (offset == fieldOffset) return;
        fieldOffset = offset;
        RefreshFields();
    }

    void OnTreeWheel(float delta)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        treeOffset -= (int)Mathf.Sign(delta) * WheelRows;
        RefreshTree();
    }

    void OnFieldWheel(float delta)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        fieldOffset -= (int)Mathf.Sign(delta) * WheelRows;
        RefreshFields();
    }

    void OnScenePressed()
    {
        RefreshScenes();
        if (scenes.Count == 0) return;
        current = scenes[(scenes.IndexOf(current) + 1) % scenes.Count];
        treeOffset = 0;
        RefreshTree();
    }

    void OnClosePressed() => Destroy(gameObject);

    void OnTreeRowPressed(int index)
    {
        TreeRow row = treeRows[index];
        if (row.Shown == null) return;
        Select(row.Shown, false);
    }

    void OnFoldPressed(int index)
    {
        TreeRow row = treeRows[index];
        if (row.Shown == null) return;
        hierarchy.Toggle(row.Shown);
        RefreshTree();
    }

    void OnFieldRowPressed(int index)
    {
        if (index < previewRows) return;
        int entryIndex = fieldOffset + index - previewRows;
        if (!TryGetEntry(entryIndex, out Entry entry, out Segment owner)) return;
        if (entry.Kind == EntryKind.Header)
        {
            if (entry.Target == null) return;
            if (!toggled.Remove(entry.Target)) toggled.Add(entry.Target);
            toggled.RemoveWhere(o => o == null);
            owner.Collapsed = !owner.Collapsed;
            Recount();
            RefreshFields();
            return;
        }
        if (entry.Target == null || entry.Member.Hidden) return;
        object value;
        try
        {
            value = entry.Member.Getter(entry.Target);
        }
        catch (Exception)
        {
            return;
        }
        Transform target = value is GameObject go && go != null ? go.transform : value is Component component && component != null ? component.transform : null;
        if (target != null)
        {
            Select(target, true);
            return;
        }
        if (value is UnityEngine.Object asset && asset != null)
        {
            PushView(new View { Asset = asset, Title = asset.name });
            return;
        }
        if (value is System.Collections.IList list && !(value is string) && list.Count > 0) PushView(ListView(list, entry.Member.Label));
    }

    static View ListView(System.Collections.IList list, string title)
    {
        int count = Mathf.Min(list.Count, MaxListItems);
        object[] items = new object[count];
        for (int i = 0; i < count; i++) items[i] = list[i];
        BasisInspectorMember[] members = new BasisInspectorMember[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            members[i] = new BasisInspectorMember("[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]", _ => items[index]);
        }
        return new View { Items = items, ItemMembers = members, Title = title + " [" + list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]" };
    }

    void Select(Transform target, bool reveal)
    {
        if (target == null) return;
        if (selected != target || views.Count > 0)
        {
            selected = target;
            hadSelection = true;
            fieldOffset = 0;
            views.Clear();
            RebuildEntries();
        }
        if (reveal)
        {
            Scene scene = target.gameObject.scene;
            if (scene.IsValid()) current = scene;
            hierarchy.Reveal(target);
            hierarchy.Refresh(current, Time.unscaledTime);
            int index = hierarchy.IndexOf(target);
            if (index >= 0 && (index < treeOffset || index >= treeOffset + treeRows.Count)) treeOffset = Mathf.Max(0, index - treeRows.Count / 2);
        }
        RefreshTree();
        RefreshFields();
    }

    void ClearSelection()
    {
        selected = null;
        hadSelection = false;
        fieldOffset = 0;
        views.Clear();
        RebuildEntries();
    }
}
