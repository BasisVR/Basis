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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine.UIElements;

namespace Meta.XR.Editor.ToolingSupport
{
    internal abstract class ToolSettingsProvider : SettingsProvider
    {
        private readonly ToolDescriptor _tool;
        private readonly Action<Origins, string> _onGUIDelegate;
        private Origins? _lastOrigin;

        protected abstract Origins SelfOrigin { get; }
        protected abstract void OpenInternal();

        private static ToolSettingsProvider _expectedSettingProvider = null;
        private bool _hasBeenOpened = false;
        private bool _hasBeenVisible = false;

        protected ToolSettingsProvider
            (ToolDescriptor tool, Action<Origins, string> onGUI, string path, SettingsScope scope)
            : base(path, scope)
        {
            _tool = tool;
            _onGUIDelegate = onGUI;
        }

        public override void OnGUI(string searchContext)
        {
            _hasBeenVisible = true;

            _tool.DrawDescriptionHeader(_tool.Description, _lastOrigin ?? SelfOrigin);

            EditorGUILayout.Space();

            using var indentScope = new UserInterface.Utils.IndentScope(EditorGUI.indentLevel + 1);
            using var labelWidthSCope = new UserInterface.Utils.LabelWidthScope(UserInterface.Styles.Constants.LabelWidth);

            _onGUIDelegate?.Invoke(SelfOrigin, searchContext);
        }

        public override void OnTitleBarGUI()
        {
            _tool.DrawHeaderFromSettingProvider(SelfOrigin);
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            base.OnActivate(searchContext, rootElement);

            // This path is automated, as opposed to when calling the Open method.
            // But being automated, we're not sure we should really trigger the telemetry
            // As Unity may just be temporarily activating this settings provider to close it later
            // There are a few ways to make sure that this is a legit activation:
            // - It has never been opened before (similarly : it has been closed properly)
            // - There is not expected setting provider (we don't have a currently opened setting that
            // fully went through)
            if (!_hasBeenOpened && _expectedSettingProvider == null)
            {
                OnOpened();
            }
        }

        public override void OnDeactivate()
        {
            if (_hasBeenVisible)
            {
                OnClosed();
            }

            base.OnDeactivate();
        }

        public void Open(Origins origin)
        {
            _lastOrigin = origin;

            OnOpened();
            OpenInternal();
        }

        private void OnOpened()
        {
            OVRTelemetry.Start(Telemetry.MarkerId.PageOpen)
                .AddAnnotation(Telemetry.AnnotationType.Origin, (_lastOrigin ?? SelfOrigin).ToString())
                .AddAnnotation(Telemetry.AnnotationType.OriginData, (string)null)
                .AddAnnotation(Telemetry.AnnotationType.Action, SelfOrigin.ToString())
                .AddAnnotation(Telemetry.AnnotationType.ActionData, _tool.Id)
                .AddAnnotation(Telemetry.AnnotationType.ActionType, GetType().Name)
                .Send();

            _expectedSettingProvider = this;
            _hasBeenVisible = false;
            _hasBeenOpened = true;
        }

        private void OnClosed()
        {
            OVRTelemetry.Start(Telemetry.MarkerId.PageClose)
                .AddAnnotation(Telemetry.AnnotationType.Origin, (_lastOrigin ?? SelfOrigin).ToString())
                .AddAnnotation(Telemetry.AnnotationType.OriginData, (string)null)
                .AddAnnotation(Telemetry.AnnotationType.Action, SelfOrigin.ToString())
                .AddAnnotation(Telemetry.AnnotationType.ActionData, _tool.Id)
                .AddAnnotation(Telemetry.AnnotationType.ActionType, GetType().Name)
                .Send();

            _lastOrigin = null;
            _hasBeenVisible = false;
            _hasBeenOpened = false;
            _expectedSettingProvider = null;
        }
    }

    internal class ToolSettingsProviderRegistry<T>
        where T : ToolSettingsProvider
    {
        private readonly Dictionary<ToolDescriptor, ToolSettingsProvider> _registry = new();
        private bool _initialized;

        public string Path;
        public SettingsScope Scope;
        public Func<ToolDescriptor, Action<Origins, string>> FetchOnGuiDelegate;

        private void Initialize()
        {
            if (_initialized) return;

            _registry.Clear();

            foreach (var tool in ToolRegistry.Registry)
            {
                var onGuiDelegate = FetchOnGuiDelegate?.Invoke(tool);
                if (onGuiDelegate == null) continue;

                if (Activator.CreateInstance(typeof(T), tool, onGuiDelegate, $"{Path}{tool.Name}", Scope)
                    is not ToolSettingsProvider provider) continue;

                _registry.Add(tool, provider);
            }

            _initialized = true;
        }

        public ToolSettingsProvider FetchSettingsProviderInternal(ToolDescriptor tool)
        {
            Initialize();

            return _registry.TryGetValue(tool, out var settings) ? settings : null;
        }

        public SettingsProvider[] CreateSettingProviders()
        {
            Initialize();

            return _registry.Values.Select(provider => provider as SettingsProvider).ToArray();
        }
    }

    internal class ProjectSettingsProvider : ToolSettingsProvider
    {
        private static readonly ToolSettingsProviderRegistry<ProjectSettingsProvider> Registry = new()
        {
            Path = $"Project/{Utils.MetaXRPublicName}/",
            Scope = SettingsScope.Project,
            FetchOnGuiDelegate = tool => tool.OnProjectSettingsGUI,
        };

        [SettingsProviderGroup]
        public static SettingsProvider[] CreateProjectSettings() =>
            Registry.CreateSettingProviders();

        private static ToolSettingsProvider FetchSettingsProvider(ToolDescriptor tool) =>
            Registry.FetchSettingsProviderInternal(tool);

        protected override Origins SelfOrigin => Origins.ProjectSettings;

        protected override void OpenInternal()
            => SettingsService.OpenProjectSettings(settingsPath);

        public static void Open(ToolDescriptor tool, Origins origin)
            => FetchSettingsProvider(tool).Open(origin);

        public ProjectSettingsProvider
            (ToolDescriptor tool, Action<Origins, string> onGUI, string path, SettingsScope scope)
            : base(tool, onGUI, path, scope)
        {
        }
    }

    internal class UserSettingsProvider : ToolSettingsProvider
    {
        private static readonly ToolSettingsProviderRegistry<UserSettingsProvider> Registry = new()
        {
            Path = $"Preferences/{Utils.MetaXRPublicName}/",
            Scope = SettingsScope.User,
            FetchOnGuiDelegate = tool => tool.OnUserSettingsGUI,
        };

        [SettingsProviderGroup]
        public static SettingsProvider[] CreateUserSettings()
            => Registry.CreateSettingProviders();

        private static ToolSettingsProvider FetchSettingsProvider(ToolDescriptor tool)
            => Registry.FetchSettingsProviderInternal(tool);

        protected override Origins SelfOrigin
            => Origins.UserSettings;

        protected override void OpenInternal()
            => SettingsService.OpenUserPreferences(settingsPath);

        public static void Open(ToolDescriptor tool, Origins origin)
            => FetchSettingsProvider(tool).Open(origin);

        public UserSettingsProvider
            (ToolDescriptor tool, Action<Origins, string> onGUI, string path, SettingsScope scope)
            : base(tool, onGUI, path, scope)
        {
        }
    }

}
