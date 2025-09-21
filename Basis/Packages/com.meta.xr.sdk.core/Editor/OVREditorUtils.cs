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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.StatusMenu;
using Meta.XR.Editor.ToolingSupport;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;
using static Meta.XR.Editor.UserInterface.Utils;

[InitializeOnLoad]
internal static class OVREditorUtils
{
    internal const string MetaXRPublicName = "Meta XR";
    internal static readonly string MetaXRSettingsName = $"Preferences";

    internal static double LastUpdateTime;
    internal static float DeltaTime { get; private set; }

    internal static readonly ToolDescriptor SettingsToolDescriptor = new ToolDescriptor()
    {
        Name = MetaXRSettingsName,
        MenuDescription = "Customize your tools",
        MqdhCategoryId = "1046393670222453",
        Color = HexToColor("#c4c4c4"),
        Icon = TextureContent.CreateContent("ovr_icon_settings.png", TextureContent.Categories.Generic),
        OnClickDelegate = OnStatusMenuClick,
        Order = 99,
        AddToStatusMenu = true,
        AddToMenu = false,
        ShowHeader = false
    };

    static OVREditorUtils()
    {
        EditorApplication.update -= UpdateEditor;
        EditorApplication.update += UpdateEditor;

        OVRUserSettingsProvider.Register("Toolbar", StatusIcon.OnSettingsGUI);
    }

    internal static void UpdateEditor()
    {
        var deltaTimeThreshold = 0.33f; // A delta time threshold in case we have a large delta from system
        var timeSinceStartup = EditorApplication.timeSinceStartup;
        DeltaTime = Mathf.Min(deltaTimeThreshold, (float)(timeSinceStartup - LastUpdateTime));
        LastUpdateTime = timeSinceStartup;
    }

    private static void OnStatusMenuClick(Origins origins)
    {
        OVRUserSettingsProvider.OpenSettingsWindow(origins);
    }

    public static string ChoosePlural(int number, string singular, string plural)
    {
        return number > 1 ? plural : singular;
    }

    public static bool IsUnityVersionCompatible()
    {
#if UNITY_2021_3_OR_NEWER
        return true;
#else
        return false;
#endif
    }

    public static string VersionCompatible => "2021.3";

    public static bool IsMainEditor()
    {
        // Early Return when the process service is not the Editor itself
#if UNITY_2021_1_OR_NEWER
        return (uint)UnityEditor.MPE.ProcessService.level != (uint)UnityEditor.MPE.ProcessLevel.Secondary;
#else
        return (uint)UnityEditor.MPE.ProcessService.level != (uint)UnityEditor.MPE.ProcessLevel.Slave;
#endif
    }

    public readonly struct UndoScope : System.IDisposable
    {
        private readonly int _group;
        private readonly string _name;

        public UndoScope(string name)
        {
            Undo.IncrementCurrentGroup();
            _group = Undo.GetCurrentGroup();
            _name = name;
        }

        public void Dispose()
        {
            Undo.SetCurrentGroupName(_name);
            Undo.CollapseUndoOperations(_group);
        }
    }
}
