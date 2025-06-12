using Basis.Scripts.Addressable_Driver.DebugError;
using Basis.Scripts.Addressable_Driver.Resource;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
namespace Basis.Scripts.Addressable_Driver.Loading
{
    public class AddressableManagement : MonoBehaviour
    {
        public Coroutine CheckTaskStatus;
        public static List<AddressableLoadResourceBase> AsyncHandles = new List<AddressableLoadResourceBase>();
        public static List<Task> LoadingTasks = new List<Task>();
        public static AddressableManagement Instance;
        public void OnEnable()
        {
            Instance = this;
            CheckTaskStatus = StartCoroutine(LoopCheckTask());
        }
        public static string GetFileNameFromUrl(string url)
        {
            Uri uri = new Uri(url);
            return Path.GetFileName(uri.LocalPath);
        }
        private IEnumerator LoopCheckTask()
        {
            while (true)
            {
                if (AsyncHandles.Count != 0)
                {
                    AsyncHandles.RemoveAll(item => item == null);
                    for (int Index = 0; Index < AsyncHandles.Count; Index++)
                    {
                        AddressableLoadResourceBase loadRequest = AsyncHandles[Index];
                        // Calculate and update the overall progress
                        float totalProgress = 0f;
                        foreach (var handle in loadRequest.Handles)
                        {
                            totalProgress += handle.PercentComplete;
                        }
                        totalProgress /= loadRequest.Handles.Count;

                        // Update progress or perform other actions based on the progress
                        AddressableLoadProcess.UpdateProgress(loadRequest, totalProgress);
                    }
                }
                yield return new WaitForSeconds(1f); // Wait for 1 second
            }
        }
        public static void RemoveFromLoadingList(AddressableLoadResourceBase RemoveMe)
        {
            if (RemoveMe != null)
            {
                AddressableLoadProcess.UpdateProgress(RemoveMe, 1);
                AsyncHandles.Remove(RemoveMe);
            }
        }
        public static async Task<bool> AwaitLoading(AddressableLoadResourceBase loadRequest)
        {
            try
            {
                loadRequest.OnLoaded.AddListener(RemoveFromLoadingList);
                foreach (AsyncOperationHandle asyncOperationHandle in loadRequest.Handles)
                {
                    if (!asyncOperationHandle.IsValid())
                    {
                        Debug.Log($"Invalid Async Handle In Key {loadRequest.Key}");
                        continue;
                    }
                    LoadingTasks.Add(asyncOperationHandle.Task);
                }
                AsyncHandles.Add(loadRequest);
                // Wait for all loading tasks to complete
                await Task.WhenAll(LoadingTasks);

                // Stop the progress check task after all loading tasks are completed
                AddressableLoadProcess.StopProgressCheck(loadRequest);

                AddressableLoadProcess.OnCompleteLoad(loadRequest);
                return true;
            }
            catch (Exception e)
            {
                AddressableDebug.DebugError($"{e.StackTrace} {e.Message}", loadRequest);
                return false;
            }
        }
    }
}