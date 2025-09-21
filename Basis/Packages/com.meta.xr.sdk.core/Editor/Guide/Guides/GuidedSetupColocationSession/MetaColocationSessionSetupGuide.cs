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

using System.Collections.Generic;
using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using Meta.XR.Editor.ToolingSupport;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Styles;

namespace Meta.XR.Guides.Editor
{
    [GuideItems]
    internal class MetaColocationSessionSetupGuide : GuidedSetup
    {
        private const string MetaDashboardURL = "https://developer.oculus.com/manage";
        private const string CreateOrgDocURL = "https://developers.meta.com/horizon/resources/publish-account-management-intro";
        private const string VerifyOrgDocURL = "https://developers.meta.com/horizon/resources/publish-organization-verification";
        private const string AddTestUserDocURL = "https://developers.meta.com/horizon/resources/test-users";
        private static GuideWindow _window;
        private static Icon _infoLabel;
        private const int ContentHeight = 600;
        private const int SubcontentMargin = 20;

        internal override GuideWindow CreateWindow()
        {
            if (_window != null) return _window;

            var title = "Meta Colocation Session Setup Guide";
            var description = "This will assist you in setting up your verified Meta developer account and org to test your project for Colocation Session.";

            var options = new GuideWindow.GuideOptions(GuideWindow.DefaultOptions)
            {
                MinWindowHeight = ContentHeight,
                MaxWindowHeight = ContentHeight
            };

            _window = Guide.Create(title, description, this, options);
            return _window;
        }

        [MenuItem("Meta/Tools/Guides/Meta Colocation Session Setup Guide")]
        private static void SetupGuide()
        {
            new MetaColocationSessionSetupGuide().ShowWindow(Origins.Menu, true);
        }

        [GuideItems]
        private List<IUserInterfaceItem> GetItems()
        {
            _infoLabel = new Icon(GuideStyles.Contents.InfoIcon, Colors.OffWhite,
                "This feature doesn't require App ID to be filled in at the development stage to test in headset, " +
                "as long as your account used to test in headset is in a verified developer org.");
            return new List<IUserInterfaceItem>
            {
                _infoLabel,
                new AddSpace(),
                CreateOrg(),
                new AddSpace(),
                CheckVerifiedOrg(),
                new AddSpace(),
                VerifyOrg(),
                new AddSpace(),
                TestUserAddUI(),
            };
        }

        private IUserInterfaceItem CreateOrg()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var t0 = new GUIContent("If you don't have organization yet, follow these steps to");
            var w0 = labelStyle.CalcSize(t0).x;
            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new Label(t0.text),
                new LinkLabel(new GUIContent("create an organization"), CreateOrgDocURL, _window),
                new AddSpace(flexibleSpace: true)
            });
        }

        private IUserInterfaceItem CheckVerifiedOrg()
        {
            return new GroupedItem(new List<IUserInterfaceItem>()
            {
                new Label("If you already have organization setup, check if your account used in the headset is verified:"),
                new AddSpace(),
                OpenDashboardUI(),
                new AddSpace(),
                new BulletedLabel("Navigate to any of the organization belong to the account (top right corner) > Organization (bottom left corner) > <b>Org Verification</b> tab. " +
                                  "You will see verification status of your organization.")
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }

        private IUserInterfaceItem OpenDashboardUI()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label)
            {
                wordWrap = false
            };
            var textContent = new GUIContent("Login with the account, click here to open");
            var widthText = labelStyle.CalcSize(textContent).x;

            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(textContent.text, labelStyle, UIStyles.ContentStatusType.Normal, GUILayout.Width(widthText)),
                new LinkLabel(new GUIContent("Meta Quest Developer Dashboard"), MetaDashboardURL, _window),
                new AddSpace(flexibleSpace: true)
            });
        }

        private IUserInterfaceItem VerifyOrg()
        {
            return new GroupedItem(new List<IUserInterfaceItem>()
            {
                new Label("If not verified, verify your org in <b>Org Verification</b> page:"),
                new AddSpace(),
                new BulletedLabel("For <b>individual developers</b>: verify identity of the admin in Admin verification."),
                new AddSpace(),
                new BulletedLabel("For <b>businesses</b>: verify identity of the business in Business verification."),
                new AddSpace(),
                new BulletedLinkLabel(new GUIContent("Check Developer Org Verification doc for any issue"), VerifyOrgDocURL, _window)
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }


        private IUserInterfaceItem TestUserAddUI()
        {
            var labelStyle = new GUIStyle(UIStyles.GUIStyles.Label) { wordWrap = false };
            var t0 = new GUIContent("You can follow these steps to");
            var w0 = labelStyle.CalcSize(t0).x - Constants.TextWidthOffset;

            var linkText = "add test users in Members Management";

            var part1 = new GroupedItem(new List<IUserInterfaceItem>
            {
                new BulletedLabel(t0.text, labelStyle, UIStyles.ContentStatusType.Normal, GUILayout.Width(w0)),
                new LinkLabel(new GUIContent(linkText), AddTestUserDocURL, _window),
                new AddSpace(true)
            });

            var part2Style = new GUIStyle(UIStyles.GUIStyles.Label);
            part2Style.margin.left = SubcontentMargin;
            var part2 = new Label("to test your apps with test users.", part2Style);

            return new GroupedItem(new List<IUserInterfaceItem>
            {
                new Label("After organization verified:"),
                part1,
                part2,
                new BulletedLabel("For accounts that cannot be a member of the verified org, you can also add them to any release channel " +
                                  "of your application for them to test. This case will need to go through app entitlement " +
                                  "process and app id would be needed.")
            }, XR.Editor.UserInterface.Utils.UIItemPlacementType.Vertical);
        }
    }
}
