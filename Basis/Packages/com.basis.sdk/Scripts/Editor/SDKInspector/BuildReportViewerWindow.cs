using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Linq;
using UnityEditor.Build.Reporting;
using UnityEditor.UIElements;
using System.IO;
using static AssetBundleBuilder;
using System;
using static AssetBundleBuilder.SerializableBuildReport;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Editor.Localization;

public class BuildReportViewerWindow : EditorWindow
{
    private Dictionary<BuildTarget, SerializableBuildReport> platformReports = new();
    private List<string> reportPaths = new();
    private List<SerializableBuildReport> allReports = new();
    private PopupField<BuildTarget> platformSelector;
    private Button removePlatformButton;
    private VisualElement tabContent;

    [MenuItem("Basis/Bundles/Build Report")]
    public static void ShowWindow()
    {
        GenerateWindow();
    }

    public static async void GenerateWindow()
    {
        BuildReportViewerWindow wnd = GetWindow<BuildReportViewerWindow>(BasisEditorLocalization.Get("sdk.buildReport.window.tabTitle"));
        wnd.titleContent = new GUIContent(BasisEditorLocalization.Get("sdk.buildReport.window.title"));
        wnd.minSize = new Vector2(600, 400);
      await  wnd.GenerateReportUI();
    }
    public async Task GenerateReportUI()
    {
        rootVisualElement.Clear();
        platformReports.Clear();
        reportPaths.Clear();
        allReports.Clear();

        string reportDir = AssetBundleBuilder.ReportDirectoryPath;
        if (!Directory.Exists(reportDir))
        {
            rootVisualElement.Add(new Label(BasisEditorLocalization.Get("sdk.buildReport.noDirectory")));
            return;
        }

        foreach (string file in Directory.GetFiles(reportDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = await File.ReadAllTextAsync(file);
                SerializableBuildReport report = JsonUtility.FromJson<SerializableBuildReport>(json);
                if (report != null && !platformReports.ContainsKey(report.summary.platform))
                {
                    platformReports[report.summary.platform] = report;
                    reportPaths.Add(file);
                    allReports.Add(report);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to load report from {file}: {e.Message}");
            }
        }

        if (platformReports.Count == 0)
        {
            var label = new Label(BasisEditorLocalization.Get("sdk.buildReport.noReports"));
            label.style.color = Color.red;
            rootVisualElement.Add(label);
            return;
        }

        var platformList = platformReports.Keys.ToList();
        BuildTarget defaultTarget = platformList.Contains(EditorUserBuildSettings.activeBuildTarget)
            ? EditorUserBuildSettings.activeBuildTarget
            : platformList[0];

        var platformSelectorContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };

        platformSelector = new PopupField<BuildTarget>(BasisEditorLocalization.Get("sdk.buildReport.platform"), platformList, defaultTarget);
        platformSelector.RegisterValueChangedCallback(evt => RefreshReportView(evt.newValue));
        platformSelectorContainer.Add(platformSelector);

        removePlatformButton = new Button(() => RemoveSelectedPlatform()) { text = BasisEditorLocalization.Get("sdk.buildReport.removePlatform"), style = { marginLeft = 10 } };
        platformSelectorContainer.Add(removePlatformButton);

        rootVisualElement.Add(platformSelectorContainer);

        var toolbar = new Toolbar();
        tabContent = new VisualElement { style = { flexGrow = 1, marginTop = 5 } };

        void SwitchTab(Action contentBuilder)
        {
            tabContent.Clear();
            contentBuilder();
        }

        var summaryButton = new ToolbarButton(() => SwitchTab(() => tabContent.Add(BuildSummaryTab(platformReports[platformSelector.value])))) { text = BasisEditorLocalization.Get("sdk.buildReport.tab.summary") };
        var packedAssetsButton = new ToolbarButton(() => SwitchTab(() => tabContent.Add(PackedAssetsTab(platformReports[platformSelector.value])))) { text = BasisEditorLocalization.Get("sdk.buildReport.tab.packed") };
        var advancedButton = new ToolbarButton(() => SwitchTab(() => tabContent.Add(AdvancedTab(platformReports[platformSelector.value])))) { text = BasisEditorLocalization.Get("sdk.buildReport.tab.advanced") };

        toolbar.Add(summaryButton);
        toolbar.Add(packedAssetsButton);
        toolbar.Add(advancedButton);
        rootVisualElement.Add(toolbar);
        rootVisualElement.Add(tabContent);

        SwitchTab(() => tabContent.Add(BuildSummaryTab(platformReports[defaultTarget])));
    }

    private void RefreshReportView(BuildTarget platform)
    {
        if (!platformReports.ContainsKey(platform)) return;
        tabContent.Clear();
        tabContent.Add(BuildSummaryTab(platformReports[platform]));
    }
    private VisualElement BuildSummaryTab(SerializableBuildReport report)
    {
        var scrollView = new ScrollView();
        var summaryBox = new VisualElement();
        summaryBox.style.paddingLeft = 10;
        summaryBox.style.paddingTop = 10;

        var title = new Label(BasisEditorLocalization.Get("sdk.buildReport.summary.header")) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14 } };
        summaryBox.Add(title);

        Color statusColor = report.summary.result switch
        {
            BuildResult.Succeeded => Color.green,
            BuildResult.Failed => Color.red,
            BuildResult.Cancelled => new Color(1f, 0.65f, 0f),
            _ => Color.gray
        };

        void AddLine(string label, string value, Color? color = null)
        {
            var line = new Label($"{label}: {value}");
            if (color.HasValue) line.style.color = color.Value;
            summaryBox.Add(line);
        }

        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.result"), report.summary.result.ToString(), statusColor);
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.totalSize"), FormatSize(report.summary.totalSize));
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.totalTime"), report.summary.totalTime.ToString("g"));
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.totalErrors"), report.summary.totalErrors.ToString());
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.totalWarnings"), report.summary.totalWarnings.ToString());
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.platform"), report.summary.platform.ToString());
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.platformGroup"), report.summary.platformGroup.ToString());
        AddLine(BasisEditorLocalization.Get("sdk.buildReport.summary.timeOfCompletion"), report.TimeTaken);

        if (!string.IsNullOrWhiteSpace(report.SummarizeErrors))
        {
            var errorLabel = new Label(BasisEditorLocalization.Get("sdk.buildReport.summary.errorsHeader", report.SummarizeErrors));
            errorLabel.style.whiteSpace = WhiteSpace.Normal;
            errorLabel.style.color = Color.red;
            errorLabel.style.marginTop = 5;
            summaryBox.Add(errorLabel);
        }

        scrollView.Add(summaryBox);
        return scrollView;
    }

    private VisualElement PackedAssetsTab(SerializableBuildReport report)
    {
        var container = new VisualElement();
        var searchField = new ToolbarSearchField();
        var scrollView = new ScrollView();

        container.Add(searchField);
        container.Add(scrollView);

        void Refresh(string search = "")
        {
            scrollView.Clear();
            foreach (BasisPackedAssets packedAsset in report.packedAssets)
            {
                var bundleFoldout = new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.bundle.label", packedAsset.shortPath), value = false };

                var assetList = packedAsset.contents
                    .Where(info => string.IsNullOrEmpty(search) || info.sourceAssetPath.ToLower().Contains(search.ToLower()))
                    .OrderByDescending(info => info.packedSize)
                    .ToList();

                foreach (var info in assetList)
                {
                    Type type = Type.GetType(info.type) ?? GetTypeFromAllAssemblies(info.type);
                    Texture icon = EditorGUIUtility.ObjectContent(null, type).image;

                    var itemContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2, marginLeft = 2, cursor = new StyleCursor(StyleKeyword.Auto) } };

                    if (icon is Texture2D tex)
                    {
                        var iconElement = new VisualElement();
                        iconElement.style.width = 16;
                        iconElement.style.height = 16;
                        iconElement.style.backgroundImage = new StyleBackground(tex);
                        iconElement.style.marginRight = 4;
                        itemContainer.Add(iconElement);
                    }

                    var label = new Label($"{info.sourceAssetPath} ({info.type}) - {FormatSize(info.packedSize)}") { style = { unityTextAlign = TextAnchor.MiddleLeft } };
                    itemContainer.Add(label);

                    itemContainer.RegisterCallback<ClickEvent>(_ =>
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(info.sourceAssetPath);
                        if (asset != null) EditorGUIUtility.PingObject(asset);
                        else Debug.LogWarning($"Asset not found at path: {info.sourceAssetPath}");
                    });

                    bundleFoldout.Add(itemContainer);
                }

                if (assetList.Count > 0)
                    scrollView.Add(bundleFoldout);
            }
        }

        searchField.RegisterValueChangedCallback(evt => Refresh(evt.newValue));
        Refresh();

        return container;
    }

    private VisualElement AdvancedTab(SerializableBuildReport report)
    {
        var scrollView = new ScrollView();
        var stepsFoldout = new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.steps.header"), value = false };

        foreach (var step in report.steps)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2, paddingLeft = step.depth * 12 } };

            var stepLabel = new Label($"- {step.name}") { style = { flexGrow = 1, whiteSpace = WhiteSpace.Normal } };
            var durationLabel = new Label($"{step.duration.TotalSeconds:F2} s")
            {
                style = { unityTextAlign = TextAnchor.MiddleRight, color = Color.gray, minWidth = 60 }
            };

            row.Add(stepLabel);
            row.Add(durationLabel);
            stepsFoldout.Add(row);
        }

        var messagesFoldout = new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.messages.header"), value = false };
        var errors = new VisualElement();
        var warnings = new VisualElement();
        var infos = new VisualElement();

        foreach (var step in report.steps)
        {
            foreach (var message in step.messages)
            {
                var label = new Label($"[{step.name}] {message.content}") { style = { whiteSpace = WhiteSpace.Normal } };

                switch (message.type)
                {
                    case LogType.Error:
                    case LogType.Exception:
                        label.style.color = Color.red;
                        errors.Add(label);
                        break;
                    case LogType.Warning:
                        label.style.color = new Color(1f, 0.65f, 0f);
                        warnings.Add(label);
                        break;
                    default:
                        label.style.color = Color.gray;
                        infos.Add(label);
                        break;
                }
            }
        }

        if (errors.childCount > 0)
            messagesFoldout.Add(new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.messages.errors"), value = false, style = { unityFontStyleAndWeight = FontStyle.Bold } }.AddAndReturn(errors));
        if (warnings.childCount > 0)
            messagesFoldout.Add(new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.messages.warnings"), value = false }.AddAndReturn(warnings));
        if (infos.childCount > 0)
            messagesFoldout.Add(new Foldout { text = BasisEditorLocalization.Get("sdk.buildReport.messages.infos"), value = false }.AddAndReturn(infos));
        if (messagesFoldout.childCount == 0)
            messagesFoldout.Add(new Label(BasisEditorLocalization.Get("sdk.buildReport.messages.empty")));

        scrollView.Add(stepsFoldout);
        scrollView.Add(messagesFoldout);

        return scrollView;
    }

    private Type GetTypeFromAllAssemblies(string typeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(typeName);
            if (type != null) return type;
        }
        return null;
    }

    private static string FormatSize(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
    private async void RemoveSelectedPlatform()
    {
        if (platformSelector == null || !platformReports.ContainsKey(platformSelector.value))
            return;

        BuildTarget selectedPlatform = platformSelector.value;

        // Show confirmation dialog
        bool confirmDelete = EditorUtility.DisplayDialog(
            BasisEditorLocalization.Get("sdk.buildReport.delete.title"),
            BasisEditorLocalization.Get("sdk.buildReport.delete.body", selectedPlatform),
            BasisEditorLocalization.Get("sdk.common.dialog.yes"),
            BasisEditorLocalization.Get("sdk.common.dialog.no")
        );

        if (!confirmDelete)
            return;

        SerializableBuildReport reportToRemove = platformReports[selectedPlatform];
        string reportPath = reportPaths.FirstOrDefault(path => path.EndsWith($"{reportToRemove.summary.platform}.json"));

        if (!string.IsNullOrEmpty(reportPath) && File.Exists(reportPath))
        {
            File.Delete(reportPath);
            Debug.Log($"Deleted report for platform {selectedPlatform} at {reportPath}");
        }

        platformReports.Remove(selectedPlatform);
        reportPaths.RemoveAll(path => path.EndsWith($"{selectedPlatform}.json"));
        allReports.RemoveAll(r => r.summary.platform == selectedPlatform);

        if (platformReports.Count == 0)
        {
            rootVisualElement.Clear();
            var label = new Label(BasisEditorLocalization.Get("sdk.buildReport.noReports"));
            label.style.color = Color.red;
            rootVisualElement.Add(label);
            return;
        }

       await GenerateReportUI(); // Rebuild UI to reflect changes
    }
}

// Utility extension
public static class VisualElementExtensions
{
    public static T AddAndReturn<T>(this T foldout, VisualElement child) where T : VisualElement
    {
        foldout.Add(child);
        return foldout;
    }
}
