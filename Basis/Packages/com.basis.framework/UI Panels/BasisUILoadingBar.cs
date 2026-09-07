
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using TMPro;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    [Serializable]
    public class LoadingOperationData
    {
        public string Key;
        public float Percentage;
        public string Display;
        public float ExpiresAt;

        public LoadingOperationData(string key, float percentage, string display)
        {
            Key = key;
            Percentage = percentage;
            Display = display;
        }
    }

    public class BasisUILoadingBar : BasisUIBase
    {
        public TextMeshPro TextMeshPro;
        public SpriteRenderer Renderer;
        public static BasisUILoadingBar Instance;
        public const string LoadingBar = "Packages/com.basis.sdk/Prefabs/UI/Loading Bar.prefab";

        public Vector3 Position = new Vector3(12, -1.6f, 0);
        public Quaternion Rotation;
        public Vector3 Scale = new Vector3(4, 4, 4);

        public static event Action<string, float, bool> OnDisplayChanged;

        public static string CurrentDisplay { get; private set; } = string.Empty;
        public static float CurrentPercentage { get; private set; }
        public static bool HasDisplay { get; private set; }

        private static readonly List<LoadingOperationData> loadingOperations = new List<LoadingOperationData>();
        private static bool hudSuppressed;
        private static bool displaySuppressed;

        private static bool IsRoutedElsewhere => hudSuppressed && OnDisplayChanged != null;

        public const float StaleOperationTimeout = 30f;
        private static Coroutine expirySweepCoroutine;
        private static MonoBehaviour expirySweepHost;
        private static readonly WaitForSeconds expirySweepInterval = new WaitForSeconds(1f);

        public static void Initialize()
        {
            BasisSceneLoad.progressCallback.OnProgressReport += ProgressReport;
            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
        }

        public static void DeInitialize()
        {
            BasisSceneLoad.progressCallback.OnProgressReport -= ProgressReport;
            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
        }

        // Cached delegate + queue avoids per-call closure allocation (~80 bytes GC per call)
        static readonly ConcurrentQueue<(string UniqueID, float Progress, string Info, float Lifetime)> _pendingReports = new();
        static readonly Action _processPendingReports = ProcessPendingReports;
        static readonly Action _closeLoadingBarNow = CloseLoadingBarNow;

        public static void ProgressReport(string UniqueID, float progress, string info)
        {
            ProgressReportTransient(UniqueID, progress, info, StaleOperationTimeout);
        }

        public static void ProgressReportTransient(string UniqueID, float progress, string info, float lifetimeSeconds)
        {
            _pendingReports.Enqueue((UniqueID, progress, info, lifetimeSeconds));
            BasisDeviceManagement.EnqueueOnMainThread(_processPendingReports);
        }

        static void ProcessPendingReports()
        {
            while (_pendingReports.TryDequeue(out var report))
            {
                if (report.Progress >= BasisProgressReport.MaxValue)
                {
                    RemoveDisplayNow(report.UniqueID);
                }
                else
                {
                    AddOrUpdateDisplay(report.UniqueID, report.Progress, report.Info, report.Lifetime);
                }
            }
        }

        public static void SetHudSuppressed(bool suppressed)
        {
            if (hudSuppressed == suppressed)
            {
                return;
            }
            hudSuppressed = suppressed;

            if (suppressed)
            {
                DestroyHud();
            }
            else if (HasDisplay)
            {
                ProcessQueue();
            }
        }

        public static void SetDisplaySuppressed(bool suppressed)
        {
            if (displaySuppressed == suppressed)
            {
                return;
            }
            displaySuppressed = suppressed;

            if (suppressed)
            {
                DestroyHud();
                SetDisplayState(string.Empty, 0f, false);
            }
            else
            {
                ProcessPendingReports();
                if (!HasDisplay)
                {
                    ProcessQueue();
                }
            }
        }

        public static void CloseLoadingBar()
        {
            BasisDeviceManagement.EnqueueOnMainThread(_closeLoadingBarNow);
        }

        private static void CloseLoadingBarNow()
        {
            StopExpirySweep();
            loadingOperations.Clear();
            DestroyHud();
            SetDisplayState(string.Empty, 0f, false);
        }

        public static void AddOrUpdateDisplay(string key, float percentage, string display)
        {
            AddOrUpdateDisplay(key, percentage, display, StaleOperationTimeout);
        }

        public static void AddOrUpdateDisplay(string key, float percentage, string display, float lifetimeSeconds)
        {
            LoadingOperationData operation = FindOperation(key);
            bool changed;
            if (operation == null)
            {
                operation = new LoadingOperationData(key, percentage, display);
                loadingOperations.Add(operation);
                changed = true;
            }
            else
            {
                changed = operation.Percentage != percentage || operation.Display != display;
                operation.Percentage = percentage;
                operation.Display = display;
            }
            operation.ExpiresAt = Time.time + lifetimeSeconds;
            StartExpirySweep();
            if (changed || (Instance == null && !IsRoutedElsewhere))
            {
                ProcessQueue();
            }
        }

        public static void RemoveDisplay(string key)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() => RemoveDisplayNow(key));
        }

        private static void RemoveDisplayNow(string key)
        {
            LoadingOperationData operation = FindOperation(key);
            if (operation == null)
            {
                return;
            }
            loadingOperations.Remove(operation);

            if (loadingOperations.Count > 0)
            {
                ProcessQueue();
            }
            else
            {
                CloseLoadingBarNow();
            }
        }

        private static LoadingOperationData FindOperation(string key)
        {
            int count = loadingOperations.Count;
            for (int Index = 0; Index < count; Index++)
            {
                if (loadingOperations[Index].Key == key)
                {
                    return loadingOperations[Index];
                }
            }
            return null;
        }

        private static void ProcessQueue()
        {
            LoadingOperationData operation = GetDisplayedOperation();
            if (operation == null || displaySuppressed)
            {
                return;
            }

            if (!IsRoutedElsewhere && Instance == null)
            {
                BasisUIBase.OpenMenuNow(LoadingBar);
            }

            SetDisplayState(operation.Display, operation.Percentage, true);
        }

        private static LoadingOperationData GetDisplayedOperation()
        {
            int count = loadingOperations.Count;
            return count > 0 ? loadingOperations[count - 1] : null;
        }

        private static void SetDisplayState(string display, float percentage, bool active)
        {
            CurrentDisplay = display ?? string.Empty;
            CurrentPercentage = percentage;
            HasDisplay = active;

            if (active && Instance != null)
            {
                Instance.UpdateDisplay(percentage, CurrentDisplay);
            }

            OnDisplayChanged?.Invoke(CurrentDisplay, percentage, active);
        }

        private static void DestroyHud()
        {
            if (Instance != null)
            {
                Instance.CloseThisMenu();
                Instance = null;
            }
        }

        private void UpdateDisplay(float percentage, string display)
        {
            if (TextMeshPro == null || Renderer == null)
            {
                return;
            }
            TextMeshPro.text = FormatDisplay(percentage, display);
            float value = percentage / 4f;
            Renderer.size = new Vector2(value, 2);
        }

        public static string FormatDisplay(float percentage, string display)
        {
            return $"{display}  {Mathf.RoundToInt(percentage)}%";
        }

        public override void InitializeEvent()
        {
            Instance = this;
            if (BasisLocalCameraDriver.HasInstance)
            {
                InstanceExists();
            }
            BasisLocalCameraDriver.InstanceExists += InstanceExists;

            if (HasDisplay)
            {
                UpdateDisplay(CurrentPercentage, CurrentDisplay);
            }
        }

        private void InstanceExists()
        {
            this.transform.parent = BasisLocalCameraDriver.Instance.ParentOfUI;
            this.transform.SetLocalPositionAndRotation(Position, Rotation);
            this.transform.localScale = Scale;
        }

        public override void DestroyEvent()
        {
        }

        public void OnDestroy()
        {
            BasisLocalCameraDriver.InstanceExists -= InstanceExists;
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private static void StartExpirySweep()
        {
            if (expirySweepCoroutine != null && expirySweepHost != null && expirySweepHost.isActiveAndEnabled)
            {
                return;
            }

            MonoBehaviour host = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance : (MonoBehaviour)Instance;
            if (host == null || !host.isActiveAndEnabled)
            {
                expirySweepCoroutine = null;
                expirySweepHost = null;
                return;
            }

            expirySweepHost = host;
            expirySweepCoroutine = host.StartCoroutine(ExpirySweep());
        }

        private static void StopExpirySweep()
        {
            if (expirySweepCoroutine != null && expirySweepHost != null)
            {
                expirySweepHost.StopCoroutine(expirySweepCoroutine);
            }
            expirySweepCoroutine = null;
            expirySweepHost = null;
        }

        private static System.Collections.IEnumerator ExpirySweep()
        {
            while (loadingOperations.Count > 0)
            {
                yield return expirySweepInterval;
                bool removed = false;
                for (int Index = loadingOperations.Count - 1; Index >= 0; Index--)
                {
                    if (Time.time >= loadingOperations[Index].ExpiresAt)
                    {
                        loadingOperations.RemoveAt(Index);
                        removed = true;
                    }
                }
                if (!removed)
                {
                    continue;
                }
                if (loadingOperations.Count > 0)
                {
                    ProcessQueue();
                }
                else
                {
                    CloseLoadingBarNow();
                    yield break;
                }
            }
            expirySweepCoroutine = null;
            expirySweepHost = null;
        }
    }
}
