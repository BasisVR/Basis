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
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal abstract class DebugManagerAddon<Type>
        where Type : DebugManagerAddon<Type>, new()
    {
        static DebugManagerAddon() // reset static fields in case of domain reload disabled
        {
            _instance = null;
            _uiPanel = null;
        }

        private static Type _instance;
        protected static IDebugUIPanel _uiPanel;

        public static Type Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new Type();
                    _instance.Setup();
                }

                return _instance;
            }
        }

        protected readonly InstanceCache _instanceCache = new();
        protected readonly List<IDebugManager> _subDebugManagers = new();

        private void Setup()
        {
            if (DebugManager.Instance == null)
            {
                DebugManager.OnReady -= OnReady; // avoid duplicated registration when domain reload disabled
                DebugManager.OnReady += OnReady;
            }
            else
            {
                OnReady(DebugManager.Instance);
            }
        }

        internal static void Destroy()
        {
            if (_instance != null)
            {
                DebugManager.OnReady -= _instance.OnReady;
            }
        }

        private void InitSubManagers()
        {
            foreach (var subManager in _subManagersToInitialize)
            {
                subManager.Setup(_uiPanel, _instanceCache);
                _subDebugManagers.Add(subManager);
            }
        }

        private void OnReady(DebugManager debugManager)
        {
            var telemetryTracker = Telemetry.TelemetryTracker.Init(Method, _subDebugManagers, _instanceCache, debugManager);
            _uiPanel = debugManager.UiPanel;

            InitSubManagers();

            OnReadyInternal();

            telemetryTracker.OnStart();
        }

        protected abstract Telemetry.Method Method { get; }

        private static List<IDebugManager> _subManagersToInitialize => new()
        {
            new GizmoManagerForAddon(),
            new WatchManagerForAddon(),
            new ActionManagerForAddon(),
            new TweakManagerForAddon(),
        };

        protected virtual void OnReadyInternal() { }
    }
}

