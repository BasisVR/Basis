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

#if META_PLATFORM_SDK_DEFINED
using Oculus.Platform.Models;
using Oculus.Platform;
using System;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Shared
{
    public enum BBPlatformInitStatus
    {
        NotStarted = 0,
        Initializing,
        Succeeded,
        Failed
    }

    public struct PlatformInfo
    {
        public bool IsEntitled;
        public string Token;
        public User OculusUser;
    }

    public class PlatformInit_ : MonoBehaviour
    {
        // empty class
    }

    public static class PlatformInit
    {
        public static BBPlatformInitStatus status { get; private set; } = BBPlatformInitStatus.NotStarted;

        private static PlatformInfo _info;

        public static void GetEntitlementInformation(Action<PlatformInfo> callback)
        {
            if (status == BBPlatformInitStatus.Succeeded)
            {
                callback(_info);
                return;
            }

            try
            {
                status = BBPlatformInitStatus.Initializing;
                Core.AsyncInitialize().OnComplete(InitializeComplete);

                void InitializeComplete(Message<PlatformInitialize> msg)
                {
                    if (msg.Data?.Result != PlatformInitializeResult.Success)
                    {
                        status = BBPlatformInitStatus.Failed;
                        Debug.LogError($"Failed to initialize OvrPlatform - {msg.GetError().Message}");
                        _info.IsEntitled = false;
                        callback(_info);
                    }
                    else
                    {
                        Entitlements.IsUserEntitledToApplication().OnComplete(CheckEntitlement);
                    }
                }

                void CheckEntitlement(Message msg)
                {
                    if (msg.IsError == false)
                    {
                        Users.GetAccessToken().OnComplete(GetAccessTokenComplete);
                    }
                    else
                    {
                        status = BBPlatformInitStatus.Failed;
                        var e = msg.GetError();
                        Debug.LogError($"Failed entitlement check: {e.Code} - {e.Message}");
                        _info.IsEntitled = false;
                        callback(_info);
                    }
                }

                void GetAccessTokenComplete(Message<string> msg)
                {
                    if (string.IsNullOrEmpty(msg.Data))
                    {
                        var output = "Token is null or empty.";
                        if (msg.IsError)
                        {
                            var e = msg.GetError();
                            output = $"{e.Code} - {e.Message}";
                        }

                        status = BBPlatformInitStatus.Failed;
                        Debug.LogError($"Failed to retrieve access token: {output}");
                        _info.IsEntitled = false;
                        callback(_info);
                    }
                    else
                    {
                        var accessToken = msg.Data;
                        Users.GetLoggedInUser().OnComplete(msg =>
                        {
                            if (!msg.IsError)
                            {
                                _info.IsEntitled = true;
                                _info.Token = accessToken;
                                _info.OculusUser = msg.Data;
                                callback(_info);
                                status = BBPlatformInitStatus.Succeeded;
                            }
                            else
                            {
                                var e = msg.GetError();
                                Debug.LogWarning("GetLoggedInUser: failed with message, " + e.Message);
                                _info.IsEntitled = false;
                                status = BBPlatformInitStatus.Failed;
                                callback(_info);
                            }
                        });
                    }
                }
            }
            catch (Exception e)
            {
                status = BBPlatformInitStatus.Failed;
                Debug.LogError($"{e.Message}\n{e.StackTrace}");
                callback(_info);
            }
        }
    }
}
#endif // META_PLATFORM_SDK_DEFINED
