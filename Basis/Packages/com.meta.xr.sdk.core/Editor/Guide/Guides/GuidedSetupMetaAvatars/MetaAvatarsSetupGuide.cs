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

#if USING_META_XR_PLATFORM_SDK

using System.Collections.Generic;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using Oculus.Platform;
using UnityEditor;
using UnityEngine;
using Styles = Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.Guides.Editor
{
    [GuideItems]
    internal class MetaAvatarsSetupGuide : GuidedSetup
    {
        private const string AppIDRetrieveDocURL = "https://developer.oculus.com/documentation/unity/unity-platform-entitlements/#retrieve-the-appid-from-the-developer-portal";
        private const string DataUseDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-app-config#enable-app-to-access-meta-avatars";
        private const string ProjectConfigDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-config-project#configuring-your-project";
        private const string AvatarLoadingDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-config-project#configuring-your-project";
        private const string OtherIssuesDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-config-project#configuring-your-project";
        private const string CrossPlayDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-cross-play";
        private const string BestPracticesDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-load-avatars#best-practices-on-loading-user-created-avatar";
        private const string DesignGreatExpDocURL = "https://developers.meta.com/horizon/documentation/unity/meta-avatars-best-practices/";

        private static GuideWindow _window;

        private static readonly GUIStyle Indent = new() { margin = new RectOffset(20, 0, 0, 0) };
        private static readonly GUIStyle LabelIndent = new(UIStyles.GUIStyles.Label) { margin = new RectOffset(22, 0, 0, 0) };

        private static TextFieldWithButton _appIdField;
        private static bool _appIdSet;
        private static Icon _appIdValidateField;
        private static Icon _appIdStatusLabel;
        private const string SectionColor = "#ffffff";

        [MenuItem("Meta/Tools/Guides/Meta Avatars Setup Guide")]
        private static void SetupGuide()
        {
            new MetaAvatarsSetupGuide().ShowWindow(Origins.Menu, true);
        }

        internal override GuideWindow CreateWindow()
        {
            if (_window != null) return _window;

            var title = "Meta Avatars Setup Guide";
            var description = "This guide will assist you in setting up your Meta developer account to use Meta Avatars in your project.";
            var width = GuideStyles.Constants.DefaultHeight + 20;
            var height = GuideStyles.Constants.DefaultHeight + 120;
            var options = new GuideWindow.GuideOptions(GuideWindow.DefaultOptions)
            {
                ShowCloseButton = false,
                MinWindowWidth = width,
                MaxWindowWidth = width,
                MinWindowHeight = height,
                MaxWindowHeight = height,
            };

            _window = Guide.Create(title, description, this, options);
            return _window;
        }

        [Init]
        private void InitializeWindow(GuideWindow window)
        {
            _window = window;

            _window.OnWindowDraw = () =>
            {
                _appIdValidateField.Hide = Common.ValidAppId(_appIdField.Text) || _appIdField.Text.Equals(Common.DefaultAppIdFieldText);
            };
            _window.AddAdditionalTelemetryAnnotations = marker =>
                marker.AddAnnotation(OVRTelemetryConstants.GuidedSetup.AnnotationType.HasAppId,
                Common.HasAppId());
        }

        [GuideItems]
        private List<IUserInterfaceItem> GetItems()
        {
            _appIdStatusLabel = new Icon(GuideStyles.Contents.SuccessIcon, Styles.Colors.OffWhite, "");
            UpdateAppIdStatus();

            return new List<IUserInterfaceItem>
            {
                AppID(),
                ConfigureProject(),
                AdditionalInfo(),
                new AddSpace(flexibleSpace: true),
                _appIdStatusLabel,
                Common.PlatformSettingsButtonGroup(this, _window)
            };
        }

        private IUserInterfaceItem AdditionalInfo()
        {
            return new GroupedItem(new List<IUserInterfaceItem>()
            {
                new Label(FormattedSectionLabel("Additional Info:")),
                new BulletedLinkLabel(new GUIContent("Meta Avatars Cross-Play with Non-Meta Environments"), CrossPlayDocURL, this),
                new BulletedLinkLabel(new GUIContent("Best practices on loading user-created avatar"), BestPracticesDocURL, this),
                new BulletedLinkLabel(new GUIContent("Designing Great Experiences with Meta Avatars SDK"), DesignGreatExpDocURL, this),
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem ConfigureProject()
        {
            return new GroupedItem(new List<IUserInterfaceItem>()
            {
                new Label(FormattedSectionLabel("Common Errors and Troubleshooting Steps:")),
                new BulletedLinkLabel(new GUIContent("Project configuration"), ProjectConfigDocURL, this),
                new BulletedLinkLabel(new GUIContent("Avatar loading issues"), AvatarLoadingDocURL, this),
                new BulletedLinkLabel(new GUIContent("Other possible issues"), OtherIssuesDocURL, this),
                new AddSpace()
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem AppID()
        {
            var requirementLabel = new Label(FormattedSectionLabel("Meta App Setup:"));
            var subLabel = new Label("To enable Meta Avatars for the Meta Horizon platform, you must complete the following two steps.");

            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var createIdLabel = new GUIContent($"{FormattedSubSectionLabel("1. Create App ID:")} Follow this to");
            var createIdLabelWidth = labelStyle.CalcSize(createIdLabel).x;
            var linkContent = new GUIContent("create and retrieve a Meta App ID.");

            var createAppPart = new GroupedItem(new List<IUserInterfaceItem>
            {
                new Label(createIdLabel.text, labelStyle, GUILayout.Width(createIdLabelWidth)),
                new LinkLabel(linkContent, AppIDRetrieveDocURL, _window),
                new AddSpace(flexibleSpace: true)
            }, Indent);

            _appIdField = new TextFieldWithButton("", Common.DefaultAppIdFieldText, "Set", _ =>
            {
                _appIdSet = Common.SetAppId(_appIdField.Text);
                UpdateAppIdStatus();
            });
            _appIdValidateField = new Icon(GuideStyles.Contents.StatusIcon, Styles.Colors.ErrorColor, "Invalid AppID.");
            var appIdFieldPart = new GroupedItem(new List<IUserInterfaceItem>
            {
                _appIdField,
                new AddSpace(4),
                _appIdValidateField
            }, Indent, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);

            var dataUseDescLabel = new Label($"{FormattedSubSectionLabel("2. Enable App to Access Meta Avatars:")} To enable access to the Meta Avatars, you must complete a Data Use Checkup on each of your apps.", LabelIndent);
            var dataUseLabel = new GUIContent("See this");
            var dataUseLabelWidth = labelStyle.CalcSize(dataUseLabel).x;
            linkContent = new GUIContent("to enable Avatars in your app.");

            var dataUsePart = new GroupedItem(new List<IUserInterfaceItem>
            {
                new Label(dataUseLabel.text, labelStyle, GUILayout.Width(dataUseLabelWidth)),
                new LinkLabel(linkContent, DataUseDocURL, _window),
                new AddSpace(flexibleSpace: true)
            }, Indent);

            return new GroupedItem(new List<IUserInterfaceItem>
            {
                requirementLabel,
                subLabel,
                new AddSpace(),
                createAppPart,
                appIdFieldPart,
                new AddSpace(),
                dataUseDescLabel,
                dataUsePart,
                new AddSpace()
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private void UpdateAppIdStatus()
        {
            _appIdStatusLabel.Hide = !Common.HasAppId();

            var appId = "";
#if UNITY_ANDROID
            appId = PlatformSettings.MobileAppID;
#else
            appId = PlatformSettings.AppID;
#endif

            if (Common.HasAppId() && !_appIdSet)
            {
                _appIdStatusLabel.LabelText = $"Your project already has an AppID: {appId}";
            }
            else if(_appIdSet)
            {
                _appIdStatusLabel.LabelText = $"Platform settings has been set with AppID: {appId}";
            }
        }

        private static string FormattedSectionLabel(string label) => $"<b>{label}</b>";
        private static string FormattedSubSectionLabel(string label) => $"<color={SectionColor}>{label}</color>";
    }
}
#endif // USING_META_XR_PLATFORM_SDK
