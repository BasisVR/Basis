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
using Meta.XR.ImmersiveDebugger.UserInterface;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    /// Retrieve debugging requests, update runtime values
    internal class DebugManager : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            Instance = null;
        }

        public static DebugManager Instance { get; private set; }

        public static event Action<DebugManager> OnReady;
        public event Action OnFocusLostAction;
        public event Action OnDisableAction;
        public event Action OnUpdateAction;

        internal delegate bool ShouldRetrieveInstanceDelegate();
        internal event ShouldRetrieveInstanceDelegate CustomShouldRetrieveInstanceCondition;

        protected readonly InstanceCache InstanceCache = new();
        protected readonly List<IDebugManager> SubDebugManagers = new();

        internal bool ShouldRetrieveInstances;
        private const float RetrievalIntervalInSec = 1.0f;
        private float _lastRetrievedTime;
        private readonly OVRSampledEventSender _frameUpdateRecorder = new(
            Telemetry.MarkerId.FrameUpdate,
            0.1f,
            marker => marker.AddPlayModeOrigin()
            );

        public IDebugUIPanel UiPanel { get; private set; }

        private void Awake()
        {
            Instance = this;
            InstanceCache.OnCacheChangedForTypeEvent += ProcessLoadedTypeBySubManagers;
            InstanceCache.OnInstanceRemoved += UnregisterInspector;
        }

        private void Start()
        {
            var telemetryTracker = Telemetry.TelemetryTracker.Init(Telemetry.Method.Attributes, SubDebugManagers, InstanceCache, this);

            UiPanel = GetComponentInChildren<IDebugUIPanel>(true);
            InitSubManagers();

            // Gather data source of types to monitor in instance cache
            AssemblyParser.RegisterAssemblyTypes(InstanceCache.RegisterClassTypes);
            RegisterTypesFromInspectedData();

            ShouldRetrieveInstances = true;
            RetrieveInstancesIfNeeded();

            OnReady?.Invoke(this);
            telemetryTracker.OnStart();

            var _ = Hierarchy.Manager.Instance; // Kicks off the Hierarchy Manager
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                OnFocusLostAction?.Invoke();
            }
        }

        private void OnDisable()
        {
            OnDisableAction?.Invoke();
        }

        private void OnDestroy()
        {
            AssemblyParser.Unregister(InstanceCache.RegisterClassTypes);
        }

        private void Update()
        {
            RetrieveInstancesIfNeeded();

            OnUpdateAction?.Invoke();
            _frameUpdateRecorder.Send();
        }

        private void RetrieveInstancesIfNeeded()
        {
            if (Time.time - _lastRetrievedTime > RetrievalIntervalInSec)
            {
                ShouldRetrieveInstances = true;
            }

            if (ShouldRetrieveInstances && CustomShouldRetrieveInstanceCondition != null)
            {
                ShouldRetrieveInstances = CustomShouldRetrieveInstanceCondition.Invoke();
            }

            if (!ShouldRetrieveInstances)
            {
                return;
            }

            _frameUpdateRecorder.Start();

            InstanceCache.RetrieveInstances();
            _lastRetrievedTime = Time.time;
            ShouldRetrieveInstances = false;
        }

        protected virtual void InitSubManagers()
        {
            RegisterManager<GizmoManager>();
            RegisterManager<WatchManager>();
            RegisterManager<ActionManager>();
            RegisterManager<TweakManager>();
        }

        private void RegisterManager<TManagerType>()
            where TManagerType : IDebugManager, new()
        {
            var manager = new TManagerType();
            manager.Setup(UiPanel, InstanceCache);
            SubDebugManagers.Add(manager);
        }

        private void ProcessLoadedTypeBySubManagers(Type type)
        {
            foreach (var manager in SubDebugManagers)
            {
                manager.ProcessType(type);
            }
        }

        private void UnregisterInspector(InstanceHandle handle)
        {
            UiPanel.UnregisterInspector(handle, Category.Default, true);
        }

        private void RegisterTypesFromInspectedData()
        {
            InspectedDataRegistry.Reset(); // in case of domain reload

            // load types from InspectedData ScriptableObject asset
            var dataAsset = RuntimeSettings.Instance.InspectedDataAssets;
            var dataEnabled = RuntimeSettings.Instance.InspectedDataEnabled;
            for (var i = 0; i < dataAsset.Count; i++)
            {
                if (!dataEnabled[i]) continue;
                InstanceCache.RegisterClassTypes(dataAsset[i].ExtractTypesFromInspectedMembers());
            }
        }
    }
}

