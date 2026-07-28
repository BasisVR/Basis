
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
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

        [SerializeField]
        private List<LoadingOperationData> loadingOperations = new List<LoadingOperationData>();

        private Coroutine autoDestroyCoroutine;
        private const float AutoDestroyTimeout = 1.5f;

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
        static readonly ConcurrentQueue<(string UniqueID, float Progress, string Info)> _pendingReports = new();
        static readonly Action _processPendingReports = ProcessPendingReports;

        public static void ProgressReport(string UniqueID, float progress, string info)
        {
            _pendingReports.Enqueue((UniqueID, progress, info));
            BasisDeviceManagement.EnqueueOnMainThread(_processPendingReports);
        }

        static void ProcessPendingReports()
        {
            while (_pendingReports.TryDequeue(out var report))
            {
                if (report.Progress == 100)
                {
                    Instance?.RemoveDisplay(report.UniqueID);
                }
                else
                {
                    if (Instance == null)
                    {
                        BasisUIBase.OpenMenuNow(LoadingBar);
                    }
                    Instance?.AddOrUpdateDisplay(report.UniqueID, report.Progress, report.Info);
                }
            }
        }

        public static void CloseLoadingBar()
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (Instance != null)
                {
                    GameObject.Destroy(Instance.gameObject);
                    Instance = null;
                }
            });
        }

        public void AddOrUpdateDisplay(string key, float percentage, string display)
        {
            var operation = loadingOperations.Find(op => op.Key == key);
            if (operation != null)
            {
                operation.Percentage = percentage;
                operation.Display = display;
            }
            else
            {
                loadingOperations.Add(new LoadingOperationData(key, percentage, display));
            }
            ProcessQueue();

            // Reset the auto-destroy coroutine
            ResetAutoDestroyCoroutine();
        }

        public void RemoveDisplay(string key)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (this == null)
                {
                    return;
                }
                var operation = loadingOperations.Find(op => op.Key == key);
                if (operation != null)
                {
                    loadingOperations.Remove(operation);
                }

                if (loadingOperations.Count > 0)
                {
                    ProcessQueue();
                }
                else
                {
                    CloseLoadingBar();
                }
            });
        }

        private void ProcessQueue()
        {
            if (this == null)
            {
                return;
            }
            if (loadingOperations.Count > 0)
            {
                var operation = GetFirstLoadingOperation();
                if (operation != null)
                {
                    UpdateDisplay(operation.Percentage, operation.Display);
                }
            }
        }

        private LoadingOperationData GetFirstLoadingOperation()
        {
            return loadingOperations.FirstOrDefault(op => op.Percentage > 0);
        }

        private void UpdateDisplay(float percentage, string display)
        {
            if (TextMeshPro == null || Renderer == null)
            {
                return;
            }
            TextMeshPro.text = $"{display}  {Mathf.RoundToInt(percentage)}%";
            float value = percentage / 4f;
            Renderer.size = new Vector2(value, 2);
        }

        public override void InitializeEvent()
        {
            Instance = this;
            if (BasisLocalCameraDriver.HasInstance)
            {
                InstanceExists();
            }
            BasisLocalCameraDriver.InstanceExists += InstanceExists;
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
        }

        private void ResetAutoDestroyCoroutine()
        {
            if (autoDestroyCoroutine != null)
            {
                StopCoroutine(autoDestroyCoroutine);
            }
            autoDestroyCoroutine = StartCoroutine(AutoDestroyAfterTimeout());
        }

        private System.Collections.IEnumerator AutoDestroyAfterTimeout()
        {
            yield return new WaitForSeconds(AutoDestroyTimeout);
            CloseLoadingBar();
        }
    }
}
