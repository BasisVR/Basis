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
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Provides functionality for an [XR_EXT_future](https://registry.khronos.org/OpenXR/specs/1.1/html/xrspec.html#XR_EXT_future)
/// </summary>
/// <remarks>
/// `XR_EXT_future` is an OpenXR extension that standardizes working with async operations. An OpenXR function that
/// provides an `XrFutureEXT` as output can then be polled each frame for completion.
///
/// The OpenXR extension which introduces an async operation using `XrFutureEXT` must also provide a bespoke getter
/// function for its promised result. Such getters must be suffixed with `Complete`. For example, if there is an async
/// function called `xrCreateFooAsync`, then there should also be a "completion" function called `xrFooComplete` that
/// provides the result of the async operation.
///
/// The <see cref="When"/> method is awaitable and cancelable.
/// </remarks>
internal static class OVRFuture
{
    /// <summary>
    /// Turns an `XrFutureEXT` handle into an awaitable task.
    /// </summary>
    /// <remarks>
    /// This method logs if `xrPollFutureEXT` or `xrCancelFutureEXT` fail unexpectedly.
    ///
    /// <example>
    /// Use <see cref="When"/> to implement a public-facing async method, as in this example:
    /// <code><![CDATA[
    /// async OVRTask<Foo?> CreateAsync() {
    ///   // Call async function in OVRPlugin which returns a handle to an XrFutureEXT
    ///   OVRPlugin.CreateFooAsync(inputParams, out ulong future);
    ///
    ///   // Now we can simply await the future
    ///   var result = await OVRFuture.When(future);
    ///
    ///   if (result.IsSuccess()) {
    ///     // Call Foo's "completion function" specific to 'CreateFoo'
    ///     OVRPlugin.FooComplete(out var completion);
    ///     return new Foo(completion);
    ///   } else {
    ///     Debug.LogError($"Something went wrong: {result}");
    ///     return null;
    ///   }
    /// }
    /// ]]></code>
    /// </example>
    ///
    /// Note: The completion object must contain a field called `FutureResult` which indicates the success or failure
    /// of the future. Therefore, if <see cref="When"/> succeeds, it means the async operation completed, but it doesn't
    /// tell you whether the operation succeeded.
    /// </remarks>
    public static async OVRTask<OVRPlugin.Result> When(ulong future, CancellationToken cancellationToken = default)
    {
        // It's possible the caller passed in an already canceled token, so check before entering the loop.
        CheckCancellationAndThrow(future, cancellationToken);

        OVRPlugin.Result result;
        while ((result = LogIfNotSuccess(
                   OVRPlugin.PollFuture(future, out var state), "Unable to poll for future state: {0}")).IsSuccess()
               && state == OVRPlugin.FutureState.Pending)
        {
            await Task.Yield(); // Wait one frame
            CheckCancellationAndThrow(future, cancellationToken);
        }

        return result;

        OVRPlugin.Result LogIfNotSuccess(OVRPlugin.Result value, string msg)
        {
            if (!value.IsSuccess())
            {
                Debug.LogError(string.Format(msg, value));
            }

            return value;
        }

        void CheckCancellationAndThrow(ulong futureToCancel, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                LogIfNotSuccess(OVRPlugin.CancelFuture(futureToCancel), "Unable to cancel future: {0}");
                throw new OperationCanceledException("Future was canceled.", token);
            }
        }
    }
}
