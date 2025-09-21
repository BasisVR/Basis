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
using Meta.XR.Editor.UserInterface;
using Meta.XR.Editor.ToolingSupport;
using Oculus.Platform;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.Guides.Editor
{
    [GuideItems]
    internal class MetaAccountSetupGuide : GuidedSetup
    {
        private const string MetaDashboardURL = "https://developer.oculus.com/manage";
        private const string AppIDRetrieveDocURL = "https://developer.oculus.com/documentation/unity/unity-platform-entitlements/#retrieve-the-appid-from-the-developer-portal";
        private const string CreateOrgDocURL = "https://developer.oculus.com/resources/publish-account-management-intro/";
        private const string AddTestUserDocURL = "https://developer.oculus.com/resources/test-users/";
        private const string AddPlatformFeaturesDocURL = "https://developer.oculus.com/documentation/unity/unity-shared-spatial-anchors/#prerequisites";
        private static UIStyles.ContentStatusType AppIdStatusType => Common.HasAppId() ? UIStyles.ContentStatusType.Normal : UIStyles.ContentStatusType.Warning;

        private const int SubcontentMargin = 20;

        private static bool _appIdSet;
        private static TextFieldWithButton _appIdField;
        private static Icon _appIdValidateField;
        private static Icon _appIdStatusLabel;
        private static GuideWindow _window;

        internal override GuideWindow CreateWindow()
        {
            if (_window != null) return _window;

            var title = "Meta Account Setup Guide";
            var description = "This will assist you in setting up your Meta developer account and guide you to retrieve the AppID to use it in your project.";
            var options = new GuideWindow.GuideOptions(GuideWindow.DefaultOptions)
            {
                ShowCloseButton = false
            };

            _window = Guide.Create(title, description, this, options);
            return _window;
        }

        [Init]
        private void InitializeWindow(GuideWindow window)
        {
            _window = window;

            window.OnWindowDraw += () =>
            {
                _appIdValidateField.Hide = Common.ValidAppId(_appIdField.Text) || _appIdField.Text.Equals(Common.DefaultAppIdFieldText);
            };

            window.AddAdditionalTelemetryAnnotations += marker =>
                marker.AddAnnotation(OVRTelemetryConstants.GuidedSetup.AnnotationType.HasAppId,
                    Common.HasAppId());
        }

        [MenuItem("Meta/Tools/Guides/Meta Account Setup Guide")]
        private static void SetupGuide()
        {
            new MetaAccountSetupGuide().ShowWindow(Origins.Menu, true);
        }

        [GuideItems]
        private List<IUserInterfaceItem> GetItems()
        {
            _appIdStatusLabel = new Icon(GuideStyles.Contents.SuccessIcon, Colors.OffWhite, "");
            UpdateAppIdStatus();

            return new List<IUserInterfaceItem>
            {
                OpenDashboardUI(),
                new AddSpace(),
                AppIdSetUI(),
                new AddSpace(),
                DataUseCheckUI(),
                new AddSpace(),
                TestUserAddUI(),
                new AddSpace(flexibleSpace: true),
                _appIdStatusLabel,
                new AddSpace(),
                Common.PlatformSettingsButtonGroup(this, _window)
            };
        }

        private IUserInterfaceItem OpenDashboardUI()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label)
            {
                wordWrap = false
            };
            var textContent = new GUIContent("Click here to open");
            var widthText = labelStyle.CalcSize(textContent).x;

            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(textContent.text, labelStyle, UIStyles.ContentStatusType.Normal, GUILayout.Width(widthText)),
                new LinkLabel(new GUIContent("Meta Quest Developer Dashboard"), MetaDashboardURL, _window),
                new AddSpace(flexibleSpace: true)
            });
        }

        private IUserInterfaceItem AppIdSetUI()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var t0 = new GUIContent("Follow the steps to");
            var w0 = labelStyle.CalcSize(t0).x;

            var t1 = new GUIContent("If you already have an Organization follow these");
            var w1 = labelStyle.CalcSize(t1).x;

            var link0 = new GUIContent("create an Organization.");
            var link1 = new GUIContent("steps to retrieve AppID.");

            var part1 = new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(t0.text, labelStyle, UIStyles.ContentStatusType.Normal, GUILayout.Width(w0)),
                new LinkLabel(link0, CreateOrgDocURL, _window),
                new AddSpace(flexibleSpace: true)
            });

            var groupStyle = new GUIStyle() { margin = new RectOffset(SubcontentMargin, 0, 0, 0) };

            var part2 = new GroupedItem(new List<IUserInterfaceItem>
            {
                new Label(t1.text, labelStyle, GUILayout.Width(w1)),
                new LinkLabel(link1, AppIDRetrieveDocURL, _window),
                new AddSpace(flexibleSpace: true)
            }, groupStyle);

            _appIdField = new TextFieldWithButton("", Common.DefaultAppIdFieldText, "Set", _ =>
            {
                _appIdSet = Common.SetAppId(_appIdField.Text);
                UpdateAppIdStatus();
                OVRTelemetry.Start(OVRTelemetryConstants.GuidedSetup.MarkerId.SetAppIdFromGuidedSetup).Send();
            });

            _appIdValidateField = new Icon(GuideStyles.Contents.StatusIcon, Colors.ErrorColor, "Invalid AppID.");

            var part3 = new GroupedItem(new List<IUserInterfaceItem>
            {
                _appIdField,
                new AddSpace(4),
                _appIdValidateField
            }, groupStyle, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);

            return new GroupedItem(new List<IUserInterfaceItem> { part1, part2, part3 }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem DataUseCheckUI()
        {
            var dataUseLabel = new BulletedLabel("To use the Anchor And Space Sharing, the <b>UserID</b> " +
                                                 "and <b>UserProfile</b> Platform\n" +
                                                 "features must be enabled in <b>Data Use Checkup</b>.");

            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var t0 = new GUIContent("Please refer to the");
            var w0 = labelStyle.CalcSize(t0).x - Constants.TextWidthOffset;

            var t1 = new GUIContent("section for more details.");
            var w1 = labelStyle.CalcSize(t1).x - Constants.TextWidthOffset;

            var linkText = new GUIContent("App Configuration");

            var appConfigGroup = new GroupedItem(new List<IUserInterfaceItem>
            {
                new AddSpace(SubcontentMargin),
                new Label(t0.text, labelStyle, GUILayout.Width(w0)),
                new LinkLabel(linkText, AddPlatformFeaturesDocURL, _window),
                new Label(t1.text, labelStyle, GUILayout.Width(w1)),
                new AddSpace(flexibleSpace: true)
            });

            var wrappedLabelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = true };
            var t2 = new GUIContent("If you're using the Colocation block with useColocationSession option enabled, you can skip this step.\n");

            var disclaimerGroup = new GroupedItem(new List<IUserInterfaceItem>
            {
                new AddSpace(SubcontentMargin),
                new Label(t2.text, wrappedLabelStyle),
                new AddSpace(flexibleSpace: true)
            });

            return new GroupedItem(new List<IUserInterfaceItem> {
                dataUseLabel,
                appConfigGroup,
                disclaimerGroup }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem TestUserAddUI()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var t0 = new GUIContent("Follow these");
            var w0 = labelStyle.CalcSize(t0).x - Constants.TextWidthOffset;

            var linkText = "steps to add test users in Members Management";

            var part1 = new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(t0.text, labelStyle, UIStyles.ContentStatusType.Normal, GUILayout.Width(w0)),
                new LinkLabel(new GUIContent(linkText), AddTestUserDocURL, _window),
                new AddSpace(true)
            });

            var part2Style = new GUIStyle(UIStyles.GUIStyles.Label);
            part2Style.margin.left = SubcontentMargin;
            var part2 = new Label("to test your Anchor And Space Sharing app before publishing it publicly.", part2Style);

            return new GroupedItem(new List<IUserInterfaceItem> { part1, part2 }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
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
                _appIdStatusLabel.LabelText = $"Plaform settings has been set with AppID: {appId}";
            }
        }
    }
}

#endif // USING_META_XR_PLATFORM_SDK
