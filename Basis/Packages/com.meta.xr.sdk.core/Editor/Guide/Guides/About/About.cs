/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.BuildingBlocks.Editor;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.Settings;
using Meta.XR.Editor.StatusMenu;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using Meta.XR.Editor.Utils;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Utils;
using Styles = Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.Guides.Editor
{
    [InitializeOnLoad]
    internal static class About
    {
        public const string PackageName = "com.meta.xr.sdk.core";

        // It may be too early to retrieve version, so we use a nullable int to add the flag of
        // whether or not we could retrieve it
        private static int? _version;
        public static int? Version => _version ??= ComputePackageVersion(PackageName);

        private static AboutGuidedSetup _guide;
        public static AboutGuidedSetup Guide => _guide ??= new AboutGuidedSetup();

        private static int? _latestVersion;
        public static int? LatestVersion => _latestVersion ??= ComputeLatestPackageVersion(PackageName);

        public static ToolDescriptor ToolDescriptor = new()
        {
            Order = -1,
            Icon = Styles.Contents.MetaWhiteIcon,
            Name = "Welcome to Meta XR SDK",
            MenuDescription = "Get Started",
            AddToStatusMenu = true,
            AddToMenu = false,
            OnClickDelegate = (origin) => Guide.ShowWindow(origin, true),
            InfoTextDelegate = ComputeInfoText,
            PillIcon = ComputePillIcon,
            IsStatusMenuItemDarker = true
        };

        private static readonly OnlyOncePerSessionBool _shouldShow = new()
        {
            Uid = "ShowAbout",
            Owner = ToolDescriptor,
            SendTelemetry = false
        };

        static About()
        {
            EditorApplication.update += Update;
        }

        private static void Update()
        {
            if (!OVREditorUtils.IsMainEditor()) return;

            EditorApplication.update -= Update;
            if (_shouldShow.Value)
            {
                Guide.ShowWindow(Origins.Self);
            }
        }

        private static (string, Color?) ComputeInfoText()
        {
            if (Version < LatestVersion)
            {
                return ($"Version {Version} (New Version {LatestVersion} Available!)", Styles.Colors.NewColor);
            }
            else
            {
                return ($"Version {Version}", Styles.Colors.DisabledColor);
            }
        }

        private static (TextureContent, Color?, bool) ComputePillIcon()
        {
            if (Version < LatestVersion)
            {
                return (Styles.Contents.UpdateIcon, Styles.Colors.NewColor, true);
            }
            else
            {
                return (null, null, false);
            }
        }

        private static int? ComputePackageVersion(string packageName)
            => ComputePackageVersion(packageName, packageInfo => packageInfo.version);

        private static int? ComputeLatestPackageVersion(string packageName)
            => ComputePackageVersion(packageName, packageInfo => packageInfo.versions.latest);

        private static int? ComputePackageVersion(string packageName,
            Func<UnityEditor.PackageManager.PackageInfo, string> extractVersionFromPackageInfo)
        {
            // Returning null as an indicator that it has not been retrieved yet
            if (!PackageList.PackageManagerListAvailable) return null;

            var version = 0;
            var package = PackageList.GetPackage(packageName);

            if (package == null) return version;

            var versionParts = extractVersionFromPackageInfo.Invoke(package).Split('.');
            if (versionParts.Length > 0)
            {
                int.TryParse(versionParts[0], out version);
            }

            return version;
        }
    }

    [GuideItems]
    internal class AboutGuidedSetup : GuidedSetup
    {
        private static GuideWindow _window;
        private readonly GUIContent Title =
            new("Building with Meta XR SDK");
        private readonly GUIContent Description =
            new("Use our development tools to create immersive mixed reality experiences for users in Unity.");

        private const string _releaseNotesUrl = "https://developers.meta.com/horizon/downloads/package/meta-xr-core-sdk";

        [MenuItem("Meta/About Meta XR SDK", false, 2000)]
        private static void SetupGuide()
        {
            About.Guide.ShowWindow(Origins.Menu, true);
        }

        internal override GuideWindow CreateWindow()
        {
            if (_window != null) return _window;

            const int width = 780;
            const int height = 1100;
            const int headerHeight = 192;
            var options = new GuideWindow.GuideOptions(GuideWindow.DefaultOptions)
            {
                ShowCloseButton = true,
                MinWindowWidth = width,
                MaxWindowWidth = width,
                MinWindowHeight = height,
                MaxWindowHeight = height,
                HeaderImage = GuideStyles.Contents.MetaCoreSDKHeaderImage,
                ShowDontShowAgainOption = true,
                InvertDontShowAgain = true,
                OverrideDontShowAgainContent = new GUIContent("Show on Startup"),
                HeaderHeight = headerHeight
            };

            _window = Guide.Create(About.ToolDescriptor.Name, null, this, options);
            return _window;
        }

        [Init]
        private void InitializeWindow(GuideWindow window)
        {
            _window = window;

            _window.DrawCustomHeaderTitle = DrawCustomHeaderTitle;
            _window.DrawCustomNotice = DrawCustomNotice;
            _window.AddAdditionalTelemetryAnnotations = marker =>
                marker.AddAnnotation(OVRTelemetryConstants.GuidedSetup.AnnotationType.HasNewVersionAvailable,
                    About.Version < About.LatestVersion);
        }

        private void DrawCustomHeaderTitle(Rect rect)
        {
            using var indentScope = new IndentScope(0);
            EditorGUILayout.Space(rect.height - Styles.Constants.LargeMargin * 2 - UIStyles.GUIStyles.HeaderLargeHorizontal.fixedHeight);
            EditorGUILayout.BeginVertical(UIStyles.GUIStyles.HeaderLargeVertical);
            EditorGUILayout.BeginHorizontal(UIStyles.GUIStyles.HeaderLargeHorizontal, GUILayout.ExpandHeight(false));
            var maxWidth = UIStyles.GUIStyles.HeaderBoldLabelLarge.CalcSize(Title).x +
                           UIStyles.GUIStyles.HeaderIconStyleLarge.fixedWidth + Styles.Constants.LargeMargin;
            EditorGUILayout.LabelField(GuideStyles.Contents.HeaderIcon, UIStyles.GUIStyles.HeaderIconStyleLarge,
                GUILayout.Width(UIStyles.GUIStyles.HeaderIconStyleLarge.fixedWidth), GUILayout.ExpandWidth(false));
            EditorGUILayout.LabelField(Title, UIStyles.GUIStyles.HeaderBoldLabelLarge);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(Description, UIStyles.GUIStyles.SubtitleLabelLarge, GUILayout.Width(maxWidth), GUILayout.ExpandHeight(false));
            EditorGUILayout.EndVertical();
        }

        private void DrawCustomNotice()
        {
            var updateAvailable = About.Version < About.LatestVersion;
            var versionMessage = updateAvailable
                ? $"<b>Meta XR SDK version {About.LatestVersion}</b> is available! We recommend upgrading to the latest version to get access to the latest features and tools to create immersive experiences for Meta XR devices."
                : $"Welcome to <b>Meta XR SDK version {About.Version}</b>! You're ready to use the latest features and tools to create immersive experiences for Meta XR devices.";

            var checkOutMessage = new GUIContent("Check out the latest");
            var checkOutMessageWidth = UIStyles.GUIStyles.Label.CalcSize(checkOutMessage).x;

            var noticeItems = new List<IUserInterfaceItem>()
            {
                new AddSpace(Styles.Constants.DoubleMargin),
                new Label(checkOutMessage.text, UIStyles.GUIStyles.Label,
                    GUILayout.MaxWidth(checkOutMessageWidth), GUILayout.ExpandWidth(false)),
                new LinkLabel(new GUIContent("Release Notes"), _releaseNotesUrl, _window),
                new Label("for more information."),
                new AddSpace(true),
            };

            if (updateAvailable)
            {
                noticeItems.Add(new Button(new ActionLinkDescription()
                {
                    Content = new GUIContent("Open Package Manager to Update"),
                    Origin = Origins.GuidedSetup,
                    OriginData = this,
                    Action = () => UnityEditor.PackageManager.UI.Window.Open(About.PackageName),
                    ActionData = null,
                    Id = "OpenPackageManagerButton"
                }));
            }

            var group = new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(
                    versionMessage,
                    UIStyles.GUIStyles.Label, updateAvailable ? UIStyles.ContentStatusType.Warning : UIStyles.ContentStatusType.Success),
                new GroupedItem(noticeItems)
            }, UIStyles.GUIStyles.NoticeGroup, UIItemPlacementType.Vertical);

            group.Draw();
        }

        [GuideItems]
        private List<IUserInterfaceItem> GetItems()
        {
            return new List<IUserInterfaceItem>
            {
                BuildWelcomeGuideItem()
            };
        }

        private IUserInterfaceItem BuildWelcomeGuideItem()
        {
            var group = new GroupedItem(new List<IUserInterfaceItem>
            {
                BuildSection("Get Started", GuideStyles.Contents.DeveloperOculusCom,
                    BuildToolDescription("Building With Unity",
                        "Get started with Unity and Meta XR SDKs through our documentation portal.",
                        "https://developers.meta.com/horizon/develop/unity"),
                    BuildToolDescription("Set Up Unity for XR Development",
                        "Learn how to quickly set up a Unity project for Meta XR development.",
                        "https://developers.meta.com/horizon/documentation/unity/unity-project-setup"),
                    BuildToolDescription("Hello World",
                        "Create your first Meta XR app for Unity with the Meta XR All-in-One SDK.",
                        "https://developers.meta.com/horizon/documentation/unity/unity-tutorial-hello-vr"),
                    BuildToolDescription("Building Blocks",
                        "Building Blocks are the easiest way to kickstart your ideas. Simply drag and drop Meta XR SDK features directly into your scene.",
                        () => BuildingBlocksWindow.ShowWindow(Origins.GuidedSetup, this), BuildingBlocks.Editor.Utils.ToolDescriptor)),
                BuildSection("Develop", GuideStyles.Contents.BuildingBlocks,
                    BuildToolDescription("Meta XR Tools",
                        "Discover Meta Quest developer tools within Unity to help you throughout the development. Check out the <b>Meta XR Tools</b> menu in your toolbar.",
                        StatusIcon.ShowDropdown, null),
                    BuildToolDescription("Samples and Showcases",
                        "Explore common usages of the Meta XR SDK features with our sample projects, and take inspiration from our Store-ready showcases.",
                        "https://developers.meta.com/horizon/code-samples/unity"),
                    BuildToolDescription("Release Notes",
                        $"Every version ships with new features, improvements, and bug fixes. Keep up to date with the latest additions to Meta XR SDKs.",
                        _releaseNotesUrl),
                    BuildToolDescription("API Reference",
                        "For detailed reference information on Meta XR SDK classes and methods, see the API Reference.",
                        "https://developers.meta.com/horizon/reference")),
                BuildSection("Test & Iterate", GuideStyles.Contents.MetaQuestDeveloperHub,
                    BuildToolDescription("Meta XR Simulator",
                        "Simulate a headset and its features, letting you test and debug your application without putting on and taking off a headset.",
                        StatusIcon.ShowDropdown, null),
                    BuildToolDescription("Meta Quest Link",
                        "Iterate directly on your headset from within Unity with the Meta Quest Link application.",
                        "https://developers.meta.com/horizon/documentation/unity/unity-link"),
                    BuildToolDescription("Meta Quest Developer Hub",
                        "Manage your devices, analyze app performance, and submit apps to Store. From MQDH, you can also access code samples, distribute your apps, and discover even more tools.",
                        "https://developers.meta.com/horizon/documentation/unity/ts-odh")),
                new AddSpace(true)
            }, UIItemPlacementType.Vertical);

            return new GroupedItem(new List<IUserInterfaceItem> { group }, UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem BuildSection(string name, TextureContent image, params IUserInterfaceItem[] toolDescriptions)
        {
            return new GroupedItem(new List<IUserInterfaceItem>()
            {
                new AddSpace(Styles.Constants.LargeMargin),
                new Label(name, UIStyles.GUIStyles.Title),
                new GroupedItem(new List<IUserInterfaceItem>()
                {
                    new GroupedItem(toolDescriptions.ToList(), UIItemPlacementType.Vertical),
                    new AddSpace(Styles.Constants.DoubleMargin),
                    new Image(image, UIStyles.GUIStyles.Image,
                        GUILayout.Width(UIStyles.GUIStyles.Image.fixedWidth))
                })
            }, UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem BuildToolDescription(string name, string description, string url)
        {
            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLinkLabel(new UrlLinkDescription()
                {
                    Content = new GUIContent(name),
                    URL = url,
                    Origin = Origins.GuidedSetup,
                    OriginData = _window,
                    Style = Styles.GUIStyles.BoldLinkLabelStyle,
                    Underline = true
                }),
                new Label(description, UIStyles.GUIStyles.Label)
            }, UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem BuildToolDescription(string name, string description, Action action, IIdentified actionData)
        {
            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLinkLabel(new ActionLinkDescription()
                {
                    Content = new GUIContent(name),
                    Action = action,
                    ActionData = actionData,
                    Origin = Origins.GuidedSetup,
                    OriginData = _window,
                    Style = Styles.GUIStyles.BoldLinkLabelStyle,
                    Underline = true
                }),
                new Label(description, UIStyles.GUIStyles.Label)
            }, UIItemPlacementType.Vertical);
        }

    }
}
