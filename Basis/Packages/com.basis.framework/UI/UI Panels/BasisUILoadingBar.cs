using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
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
        private const float AutoDestroyTimeout = 5f;

        public static void Initalize()
        {
            BasisSceneLoadDriver.progressCallback.OnProgressReport += ProgressReport;
            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport += ProgressReport;
        }

        public static void DeInitalize()
        {
            BasisSceneLoadDriver.progressCallback.OnProgressReport -= ProgressReport;
            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport -= ProgressReport;
        }

        public static void ProgressReport(string UniqueID, float progress, string info)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (progress == 100)
                {
                    Instance?.RemoveDisplay(UniqueID);
                }
                else
                {
                    if (Instance == null)
                    {
                        AddressableGenericResource resource = new AddressableGenericResource(LoadingBar, AddressableExpectedResult.SingleItem);
                        BasisUIBase.OpenMenuNow(resource);
                    }
                    Instance.AddOrUpdateDisplay(UniqueID, progress, info);
                }
            });
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
            if (loadingOperations.Count > 0 && Instance != null)
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
            TextMeshPro.text = display;
            float value = percentage / 4f;
            Renderer.size = new Vector2(value, 2);
        }

        public override void InitalizeEvent()
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
