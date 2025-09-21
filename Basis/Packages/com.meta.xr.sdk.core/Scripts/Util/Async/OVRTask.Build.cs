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
using Unity.Collections.LowLevel.Unsafe;

static partial class OVRTask
{
    /// <summary>
    /// (Internal) Use this to construct an <see cref="OVRTask{T}"/> from an asynchronous OpenXR call.
    /// </summary>
    /// <remarks>
    /// A common pattern is to call a method that starts an asynchronous operation, and then either
    /// 1. If it succeeds, return a pending task (e.g., <see cref="OVRTask.FromRequest{TResult}(ulong)"/>).
    /// 2. If it fails, return completed task with a value representing the failure (e.g., <see cref="OVRTask.FromResult{TResult}"/>).
    ///
    /// This can lead to lots of repeated type information. This utility simplifies this pattern using a fluent-style
    /// syntax:
    ///
    /// <![CDATA[
    /// return OVRTask
    ///   .Build(OVRPlugin.SomeAsyncOperation(out var requestId), requestId) // no type information
    ///   .ToTask<TValue, TStatus>(); // <-- returns an OVRResult<TValue, TStatus>
    /// ]]>
    ///
    /// Note the type information is only required once.
    /// </remarks>
    internal readonly struct Builder
    {
        private readonly OVRPlugin.Result _synchronousResult;

        private readonly Guid _taskId;

        /// <summary>
        /// Use <see cref="OVRTask.Build"/> instead of this constructor directly.
        /// </summary>
        public Builder(OVRPlugin.Result synchronousResult, Guid taskId)
        {
            _synchronousResult = synchronousResult;
            _taskId = taskId;
        }

        /// <summary>
        /// Creates a new <see cref="OVRTask{TResult}"/> of type <see cref="OVRPlugin.Result"/>.
        /// Typically used as an implementation detail.
        /// </summary>
        public OVRTask<OVRPlugin.Result> ToTask() => ToTask(_synchronousResult);

        /// <summary>
        /// Creates a new <see cref="OVRTask{TStatus}"/>. <typeparamref name="TStatus"/> must be castable
        /// from an <see cref="OVRPlugin.Result"/>.
        /// </summary>
        public OVRTask<TStatus> ToTask<TStatus>() where TStatus : struct, Enum => ToTask(CastResult<TStatus>());

        /// <summary>
        /// Creates a new <see cref="OVRTask{TResult}"/>. <typeparamref name="TResult"/> can be any type,
        /// but you must specify the value to use for failure.
        /// </summary>
        /// <param name="failureValue">The value to use when <see cref="_synchronousResult"/> indicates failure.</param>
        public OVRTask<TResult> ToTask<TResult>(TResult failureValue) => _synchronousResult.IsSuccess()
            ? FromGuid<TResult>(_taskId)
            : FromResult(failureValue);

        /// <summary>
        /// Same as <see cref="ToTask{TStatus}()"/> but returns an <see cref="OVRTask{TResult}"/> whose result is an
        /// <see cref="OVRResult{TStatus}"/>.
        /// </summary>
        public OVRTask<OVRResult<TStatus>> ToResultTask<TStatus>() where TStatus : struct, Enum
            => ToTask(_synchronousResult.IsSuccess() ? default : OVRResult<TStatus>.FromFailure(CastResult<TStatus>()));

        /// <summary>
        /// Creates a new <see cref="OVRTask{TResult}"/> where `TResult` is an <see cref="OVRResult{TValue,TStatus}"/>.
        /// <typeparamref name="TStatus"/> must be castable from an <see cref="OVRPlugin.Result"/>.
        /// </summary>
        public OVRTask<OVRResult<TValue, TStatus>> ToTask<TValue, TStatus>() where TStatus : struct, Enum
            => ToTask(_synchronousResult.IsSuccess() ? default : OVRResult<TValue, TStatus>.FromFailure(CastResult<TStatus>()));

        private TResult CastResult<TResult>() where TResult : struct, Enum
        {
            var underlyingType = typeof(TResult).GetEnumUnderlyingType();
            if (underlyingType != typeof(int) && underlyingType != typeof(uint))
                throw new InvalidCastException($"{typeof(TResult).Name} must have an underlying type of {nameof(Int32)} or {nameof(UInt32)}.");

            var value = _synchronousResult;
            return UnsafeUtility.As<OVRPlugin.Result, TResult>(ref value);
        }
    }
}
