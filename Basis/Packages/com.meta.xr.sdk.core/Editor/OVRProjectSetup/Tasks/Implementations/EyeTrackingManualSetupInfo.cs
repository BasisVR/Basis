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
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEngine;
using Styles = Meta.XR.Editor.UserInterface.Styles;
using Meta.XR.Guides.Editor;

[GuideItems]
internal class EyeTrackingManualSetupInfo : UPSTGuidedSetup
{
    private static GuideWindow _window;
    private const int ContentHeight = 276;

    [GuideItems]
    private List<IUserInterfaceItem> GetItems()
    {
        BulletedLabel Bullet(string text) => new BulletedLabel(text, UIStyles.ContentStatusType.Normal);
        return new List<IUserInterfaceItem>
        {
            Bullet("Navigate to <b>Settings</b> in your headset."),
            Bullet("Select <b>Movement tracking</b>."),
            Bullet("Select <b>Eye Tracking</b>."),
            Bullet("Toggle Eye tracking to <b>on</b>."),
        };
    }

    internal override GuideWindow CreateWindow()
    {
        if (_window != null) return _window;

        var title = "Setup Guide - Eye Tracking";
        var description = "Eye tracking technology for Meta Quest Pro detects eye movements to control an avatar’s eye transformations as the user looks around. The Meta Quest Pro headset is the only device that supports this feature. To enable:";
        var options = new GuideWindow.GuideOptions(GuideWindow.DefaultOptions)
        {
            ShowDontShowAgainOption = false,
            MinWindowHeight = ContentHeight,
            MaxWindowHeight = ContentHeight,
            BottomMargin = 4,
        };
        _window = Guide.Create(title, description, this, options);
        return _window;
    }
}
