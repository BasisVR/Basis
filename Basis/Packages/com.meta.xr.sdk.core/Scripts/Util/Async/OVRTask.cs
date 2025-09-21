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
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// Static methods related to <see cref="OVRTask"/>&lt;TResult&gt;
/// </summary>
/// <remarks>
/// The static class `OVRTask` provides some utilities for working with tasks.
///
/// Many asynchronous methods in the Core SDK return an awaitable <see cref="OVRTask"/>&lt;TResult&gt;. The methods in this
/// class provide functionality to combine multiple tasks (see <see cref="WhenAll{T1,T2}"/>) and create your own
/// <see cref="OVRTask"/>&lt;TResult&gt; (see <see cref="Create{TResult}"/>). For more information
/// on tasks, see [Asynchronous Tasks](https://developer.oculus.com/documentation/unity/unity-async-tasks/).
/// </remarks>
public static partial class OVRTask
{
    /// <summary>
    /// Creates a task that completes when all of the supplied tasks have completed.
    /// </summary>
    /// <remarks>
    /// This can be used to combine multiple tasks into a single task. The returned task completes when all tasks
    /// in <paramref name="tasks"/> complete.
    ///
    /// The result of the returned task is an array containing the results of each individual task. The results are
    /// arranged in the same order as the original <paramref name="tasks"/> list.
    /// </remarks>
    /// <param name="tasks">The tasks to combine</param>
    /// <typeparam name="TResult">The type of the result produced by the <paramref name="tasks"/>.</typeparam>
    /// <returns>Returns a new task which is completed when all <paramref name="tasks"/> have completed.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="tasks"/> is `null`.</exception>
    /// <seealso cref="OVRTask"/>
    public static OVRTask<TResult[]> WhenAll<TResult>(IEnumerable<OVRTask<TResult>> tasks)
        => OVRTask<TResult>.WhenAll(tasks);

    /// <summary>
    /// Creates a task that completes when all of the supplied tasks have completed.
    /// </summary>
    /// <remarks>
    /// This can be used to combine multiple tasks into a single task. The returned task completes when all tasks
    /// in <paramref name="tasks"/> complete.
    ///
    /// The result of each task in <paramref name="tasks"/> is added to <paramref name="results"/>. The results are in
    /// the same order as <paramref name="tasks"/>.
    ///
    /// The list in the combined task is a reference to <paramref name="results"/>. This allows the caller to own
    /// (and potentially reuse) the memory for the list of results. It is undefined behavior to access
    /// <paramref name="results"/> before the returned task completes.
    /// </remarks>
    /// <param name="tasks">The tasks to combine</param>
    /// <param name="results">A list to store the results in. The list is cleared before adding any results to it.</param>
    /// <typeparam name="TResult">The type of the result produced by the <paramref name="tasks"/>.</typeparam>
    /// <returns>Returns a new task which completes when all <paramref name="tasks"/> are complete.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="tasks"/> is `null`.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="results"/> is `null`.</exception>
    /// <seealso cref="OVRTask"/>
    public static OVRTask<List<TResult>> WhenAll<TResult>(IEnumerable<OVRTask<TResult>> tasks, List<TResult> results)
        => OVRTask<TResult>.WhenAll(tasks, results);

    private class MultiTaskData<T> : OVRObjectPool.IPoolObject
    {
        protected OVRTask<T> CombinedTask;

        protected T Result;

        protected HashSet<Guid> Remaining;

        void OVRObjectPool.IPoolObject.OnGet()
        {
            CombinedTask = FromGuid<T>(Guid.NewGuid());
            Result = default;
            Remaining = OVRObjectPool.HashSet<Guid>();
        }

        void OVRObjectPool.IPoolObject.OnReturn()
        {
            Result = default;
            OVRObjectPool.Return(Remaining);
        }

        protected void AddTask(Guid id) => Remaining.Add(id);

        protected void OnResult(Guid taskId)
        {
            Remaining.Remove(taskId);
            if (Remaining.Count != 0) return;

            try
            {
                CombinedTask.SetResult(Result);
            }
            finally
            {
                OVRObjectPool.Return(this);
            }
        }
    }

    /// \cond
    internal static OVRTask<TResult> FromGuid<TResult>(Guid id) => Create<TResult>(id);

    [Obsolete("Consider OVRTask.Build instead.")]
    internal static OVRTask<TResult> FromRequest<TResult>(ulong id) => Create<TResult>(GetId(id));

    [Obsolete("Consider OVRTask.Build instead.")]
    internal static OVRTask<TResult> FromRequest<TResult>(ulong id, OVRPlugin.EventType eventType)
        => Create<TResult>(GetId(id, eventType));

    internal static Builder Build(bool success, ulong requestId)
        => new(success ? OVRPlugin.Result.Success : OVRPlugin.Result.Failure, GetId(requestId));

    internal static Builder Build(OVRPlugin.Result result, ulong requestId)
        => new(result, GetId(requestId));

    internal static Builder Build(OVRPlugin.Result result, ulong requestId, OVRPlugin.EventType eventType)
        => new(result, GetId(requestId, eventType));
    /// \endcond

    /// <summary>
    /// Creates an already-complete task.
    /// </summary>
    /// <remarks>
    /// This creates a completed task whose result is <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The result of the task.</param>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <returns>Returns a new, completed task-like object whose result is <paramref name="result"/>.</returns>
    public static OVRTask<TResult> FromResult<TResult>(TResult result)
    {
        var task = Create<TResult>(Guid.NewGuid());
        task.SetResult(result);
        return task;
    }

    /// \cond
    [Obsolete("This method does not ensure the task exists; it just returns an OVRTask with the given id. Use TryGetPending instead.", error: true)]
    internal static OVRTask<TResult> GetExisting<TResult>(Guid id) => Get<TResult>(id);

    internal static bool TryGetPendingTask<TResult>(Guid id, out OVRTask<TResult> task)
    {
        task = Get<TResult>(id);
        return task.IsPending;
    }

    [Obsolete("This method does not ensure the task exists; it just returns an OVRTask with the given id. Use TryGetPending instead.", error: true)]
    internal static OVRTask<TResult> GetExisting<TResult>(ulong id) => Get<TResult>(GetId(id));

    internal static bool TryGetPendingTask<TResult>(ulong id, out OVRTask<TResult> task) => TryGetPendingTask(GetId(id), out task);
    /// \endcond

    /// <summary>
    /// Sets the result of a pending task.
    /// </summary>
    /// <remarks>
    /// Set the result of a task previously created with <see cref="Create{TResult}"/>.
    /// When this method returns, <see cref="OVRTask{TResult}.IsCompleted"/> will be true.
    ///
    /// <example><code><![CDATA[
    /// OVRTask<int> MyOpAsync() {
    ///   _id = Guid.NewGuid();
    ///   var task = OVRTask.Create<int>(id);
    ///   return task;
    /// }
    ///
    /// // later, when the task completes:
    /// void Update() {
    ///   if (operationComplete) {
    ///     OVRTask.SetResult(_id, result);
    ///   }
    /// }
    /// ]]></code></example>
    ///
    /// This allows you to await on `MyOpAsync`:
    ///
    /// <example><code><![CDATA[
    /// async void OnButtonPressed() {
    ///   var result = await MyOpAsync();
    /// }
    /// ]]></code></example>
    /// </remarks>
    /// <param name="id">The task's unique id.</param>
    /// <param name="result">The result the task should have.</param>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <exception cref="InvalidOperationException">Thrown if the task with id <paramref name="id"/> already has a
    /// result.</exception>
    public static void SetResult<TResult>(Guid id, TResult result)
    {
        var task = Get<TResult>(id);
        if (task.HasResult)
            throw new InvalidOperationException($"Task {id} already has a result.");

        task.SetResult(result);
    }

    /// \cond
    internal static void SetResult<TResult>(ulong id, TResult result) =>
        Get<TResult>(GetId(id)).SetResult(result);

    private static OVRTask<TResult> Get<TResult>(Guid id)
    {
        return new OVRTask<TResult>(id);
    }
    /// \endcond

    /// <summary>
    /// Creates a new task.
    /// </summary>
    /// <remarks>
    /// This method creates a new pending task. When the task completes, set its result with
    /// <see cref="SetResult{TResult}"/>.
    ///
    /// The returned task is in a pending state; that is, <see cref="OVRTask{TResult}.IsCompleted"/> is `false` until
    /// you later set its result with <see cref="SetResult{TResult}"/>.
    ///
    /// The <paramref name="taskId"/> must be unique to the new task. You may use any `Guid` as long as it has not
    /// previously been used to create a task. Use `Guid.NewGuid()` to generate a random task id.
    /// </remarks>
    /// <param name="taskId">The id used to assign the new task.</param>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <returns>Returns a new task which completes when you call <see cref="SetResult{TResult}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="taskId"/> refers to an existing task.</exception>
    public static OVRTask<TResult> Create<TResult>(Guid taskId)
    {
        RegisterType<TResult>();
        var task = Get<TResult>(taskId);
        if (!task.AddToPending())
            throw new ArgumentException($"The task with id {taskId} already exists.", nameof(taskId));

        return task;
    }

    /// \cond
    private const ulong HashModifier1 = 0x319642b2d24d8ec3;
    private const ulong HashModifier2 = 0x96de1b173f119089;

    internal static unsafe Guid GetId(ulong part1, ulong part2)
    {
        var values = stackalloc ulong[2];
        values[0] = unchecked(part1 + HashModifier1);
        values[1] = unchecked(part2 + HashModifier2);
        return *(Guid*)values;
    }

    internal static Guid GetId(ulong handle, OVRPlugin.EventType eventType) => GetId(handle, (ulong)eventType);

    internal static Guid GetId(ulong value) => GetId(value, 0ul);

    internal static ulong GetId(Guid value) => GetIdParts(value).Item1;

    internal static unsafe (ulong, ulong) GetIdParts(Guid id)
    {
        var values = stackalloc ulong[2];
        UnsafeUtility.MemCpy(values, &id, sizeof(Guid));
        return (unchecked(values[0] - HashModifier1), unchecked(values[1] - HashModifier2));
    }

    internal static void RegisterType<TResult>()
    {
#if UNITY_EDITOR
        DomainReloadMethods.Add(OVRTask<TResult>.Clear);
#endif
    }
    /// \endcond

#if UNITY_EDITOR
    private static readonly HashSet<Action> DomainReloadMethods = new HashSet<Action>();

    [UnityEditor.InitializeOnEnterPlayMode]
    internal static void OnEnterPlayMode()
    {
        foreach (var method in DomainReloadMethods)
        {
            method();
        }

        DomainReloadMethods.Clear();
    }
#endif
}

/// <summary>
/// Represents an awaitable task.
/// </summary>
/// <remarks>
/// The `OVRTask&lt;TResult&gt;` struct is a task-like object which supports the <c>await</c> pattern. Typically, you
/// do not need to create or use this object directly. Instead, you can either:
/// - `await` a method which returns an object of type <see cref="OVRTask"/>&lt;TResult&gt;, which will eventually
/// return a <typeparamref name="TResult"/>
/// - poll the <see cref="IsCompleted"/> property and then call <see cref="GetResult"/>
/// - pass a delegate by calling <see cref="ContinueWith(Action{TResult})"/>. Note that an additional state `object` can
/// get passed in and added as a parameter of the callback, see <see cref="ContinueWith{T}"/>
///
/// Note this requires the main thread to complete the await contract; therefore, blocking on an `OVRTask` can result in
/// an infinite loop.
///
/// You can also:
/// - Combine multiple tasks (<see cref="OVRTask.WhenAll{T1,T2}"/>)
/// - Create your own task (<see cref="OVRTask.Create{TResult}"/>)
///
/// For more details, including code samples, see
/// [Asynchronous Tasks](https://developer.oculus.com/documentation/unity/unity-async-tasks/).
/// </remarks>
/// <typeparam name="TResult">The type of result being awaited.</typeparam>
[AsyncMethodBuilder(typeof(OVRTaskBuilder<>))]
public readonly struct OVRTask<TResult> : IEquatable<OVRTask<TResult>>, IDisposable
{
    /// \cond
    #region static

    private static readonly HashSet<Guid> Pending = new();
    private static readonly Dictionary<Guid, TResult> Results = new();
    private static readonly Dictionary<Guid, Exception> Exceptions = new();
    private static readonly Dictionary<Guid, TaskSource> Sources = new();
    private static readonly Dictionary<Guid, AwaitableSource> AwaitableSources = new();
    private static readonly Dictionary<Guid, Action> Continuations = new();

    #region ContinueWith Data
    private delegate void ContinueWithInvoker(Guid guid, TResult result);
    private delegate bool ContinueWithRemover(Guid guid);
    private static readonly Dictionary<Guid, ContinueWithInvoker> ContinueWithInvokers = new();
    private static readonly Dictionary<Guid, ContinueWithRemover> ContinueWithRemovers = new();
    private static readonly HashSet<Action> ContinueWithClearers = new();
    #endregion

    #region InternalData Data
    private delegate bool InternalDataRemover(Guid guid);
    private static readonly Dictionary<Guid, InternalDataRemover> InternalDataRemovers = new();
    private static readonly HashSet<Action> InternalDataClearers = new();
    #endregion

    #region Incremental results data
    private static readonly Dictionary<Guid, Action<Guid>> IncrementalResultSubscriberRemovers = new();
    private static readonly HashSet<Action> IncrementalResultSubscriberClearers = new();
    #endregion

    /// <summary>
    /// Clears internal state for all tasks of type <typeparamref name="TResult"/>.
    /// </summary>
    /// <remarks>
    /// This is called by the testing framework and to handle Play in Editor when domain reload is disabled.
    /// </remarks>
    internal static readonly Action Clear = () =>
    {
        Results.Clear();
        Continuations.Clear();
        Pending.Clear();
        Exceptions.Clear();

        ContinueWithInvokers.Clear();
        foreach (var clearer in ContinueWithClearers)
        {
            clearer();
        }

        ContinueWithClearers.Clear();
        ContinueWithRemovers.Clear();

        foreach (var internalDataClearer in InternalDataClearers)
        {
            internalDataClearer();
        }

        InternalDataClearers.Clear();
        InternalDataRemovers.Clear();

        foreach (var clearer in IncrementalResultSubscriberClearers)
        {
            clearer();
        }

        IncrementalResultSubscriberClearers.Clear();
        IncrementalResultSubscriberRemovers.Clear();

        foreach (var source in Sources.Values)
        {
            OVRObjectPool.Return(source);
        }
        Sources.Clear();

        foreach (var source in AwaitableSources.Values)
        {
            OVRObjectPool.Return(source);
        }
        AwaitableSources.Clear();
    };

    #endregion

    internal readonly Guid _id;

    internal OVRTask(Guid id)
    {
        _id = id;
    }

    static OVRTask() => OVRTask.RegisterType<TResult>();

    internal bool AddToPending() => Pending.Add(_id);
    internal bool IsPending => Pending.Contains(_id);
    internal void SetInternalData<T>(T data) => InternalData<T>.Set(_id, data);

    internal OVRTask<TResult> WithInternalData<T>(T data)
    {
        if (!HasResult)
        {
            InternalData<T>.Set(_id, data);
        }
        return this;
    }

    internal bool TryGetInternalData<T>(out T data) => InternalData<T>.TryGet(_id, out data);

    /// <summary>
    /// (Internal) Set an exception that occurred during task execution.
    /// </summary>
    /// <remarks>
    /// Do not call this directly.
    ///
    /// OVRTasks were designed to service the needs of OpenXR APIs where one function initiates an asynchronous
    /// operation (identified by an `XrAsyncRequestId`) and some time later, the result is received. This process
    /// does not trigger exceptions, and you should not convert OpenXR errors to exceptions (use an error code instead).
    ///
    /// The exception handling provided here is to service the compiler-generated OVRTask, e.g.,
    /// <code><![CDATA[
    /// async OVRTask<int> ComputeAsync() {
    ///   var sum = await SomeOtherResultAsync() + 42; // <-- compiler generates an OVRTask here
    ///   DoSomethingThatThrows(); // exception thrown in C#
    ///   return sum;
    /// }
    /// ]]></code>
    ///
    /// This method should only be invoked by the <see cref="OVRTaskBuilder{T}"/> to provide an exception from an
    /// awaited C# method.
    /// </remarks>
    /// <param name="exception">The exception</param>
    internal void SetException(Exception exception)
    {
        if (AwaitableSources.Remove(_id, out var awaitableSource))
        {
            awaitableSource.SetException(exception);
        }
        else if (Sources.Remove(_id, out var source))
        {
            source.SetException(exception);
        }
        else if (TryRemoveInternalData())
        {
            if (ContinueWithInvokers.Remove(_id, out var invoker))
            {
                // When using ContinueWith, there is no way for the caller to catch the exception.
                // However, we discourage exceptions to signal anything other than API misuse.
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // Save the exception so that it can be caught by the await expression.
            Exceptions.Add(_id, exception);
            TryInvokeContinuation();
        }
        else
        {
            throw new InvalidOperationException(
                $"The exception {exception} cannot be set on task {_id} because it is not a valid task.", exception);
        }
    }

    /// <summary>
    /// (Internal) Removes internal data related to the task.
    /// </summary>
    /// <remarks>
    /// Removes incremental result subscribers and internal data. Call this when the task completes.
    /// </remarks>
    /// <returns>Returns `true` if the task was pending, otherwise `false`</returns>
    private bool TryRemoveInternalData()
    {
        if (!Pending.Remove(_id)) return false;

        if (InternalDataRemovers.Remove(_id, out var internalDataRemover))
        {
            internalDataRemover(_id);
        }

        if (IncrementalResultSubscriberRemovers.Remove(_id, out var subscriberRemover))
        {
            subscriberRemover(_id);
        }

        return true;
    }

    private bool TryInvokeContinuation()
    {
        if (Continuations.Remove(_id, out var continuation))
        {
            continuation();
            return true;
        }

        return false;
    }

    internal void SetResult(TResult result)
    {
        if (AwaitableSources.Remove(_id, out var awaitableSource))
        {
            awaitableSource.SetResultAndReturnToPool(result);
        }
        else if (Sources.Remove(_id, out var source))
        {
            source.SetResult(result);
        }
        // If false, no one was waiting on the task
        else if (TryRemoveInternalData())
        {
            if (ContinueWithInvokers.Remove(_id, out var invoker))
            {
                invoker(_id, result);
            }
            else
            {
                // Add to the results so that GetResult can retrieve it later.
                Results.Add(_id, result);
                TryInvokeContinuation();
            }
        }
    }

    /// <summary>
    /// (Internal) Represents additional data associated with the task
    /// </summary>
    /// <remarks>
    /// These "removers" and "clearers" offer a sort of type erasure so that we can store a typeless
    /// delegate to invoke that doesn't depend on <typeparamref name="T"/>.
    /// </remarks>
    private static class InternalData<T>
    {
        static readonly Dictionary<Guid, T> Data = new Dictionary<Guid, T>();

        public static bool TryGet(Guid taskId, out T data)
        {
            return Data.TryGetValue(taskId, out data);
        }

        public static void Set(Guid taskId, T data)
        {
            Data[taskId] = data;
            InternalDataRemovers.Add(taskId, Remover);
            InternalDataClearers.Add(Clearer);
        }

        static readonly InternalDataRemover Remover = Remove;
        static readonly Action Clearer = Clear;
        static bool Remove(Guid taskId) => Data.Remove(taskId);
        static void Clear() => Data.Clear();
    }

    /// <summary>
    /// A delegate to invoke when incremental data is received.
    /// </summary>
    /// <remarks>
    /// It is up to the task creator to provide incremental data, but this offers a way to store the delegates,
    /// if your API offers one to the caller of an async operation.
    /// </remarks>
    private static class IncrementalResultSubscriber<T>
    {
        private static readonly Dictionary<Guid, Action<T>> Subscribers = new();

        public static void Set(Guid taskId, Action<T> subscriber)
        {
            Subscribers[taskId] = subscriber;
            IncrementalResultSubscriberRemovers[taskId] = Remover;
            IncrementalResultSubscriberClearers.Add(Clearer);
        }

        public static void Notify(Guid taskId, T result)
        {
            if (Subscribers.TryGetValue(taskId, out var subscriber))
            {
                subscriber(result);
            }
        }

        private static readonly Action<Guid> Remover = Remove;

        private static void Remove(Guid id) => Subscribers.Remove(id);

        private static readonly Action Clearer = Clear;

        private static void Clear() => Subscribers.Clear();
    }

    /// <summary>
    /// (Internal) Sets the delegate to be invoked when an incremental result is available before the task is complete.
    /// </summary>
    /// <remarks>
    /// Some tasks may provide incremental results before the task is complete. In this case, you can use
    /// <see cref="SetIncrementalResultCallback{TIncrementalResult}"/> to receive those results as they become available.
    ///
    /// For example, the task may provide a list of results over some period of time and may be able to provide
    /// partial results as they become available, before the task completes.
    /// </remarks>
    /// <param name="onIncrementalResultAvailable">Invoked whenever <see cref="NotifyIncrementalResult{TIncrementalResult}"/>
    /// is called.</param>
    /// <typeparam name="TIncrementalResult">The type of the incremental result. This is typically different than the
    /// <typeparamref name="TResult"/>.</typeparam>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="onIncrementalResultAvailable"/> is `null`.</exception>
    internal void SetIncrementalResultCallback<TIncrementalResult>(
        Action<TIncrementalResult> onIncrementalResultAvailable)
    {
        if (onIncrementalResultAvailable == null)
            throw new ArgumentNullException(nameof(onIncrementalResultAvailable));

        IncrementalResultSubscriber<TIncrementalResult>.Set(_id, onIncrementalResultAvailable);
    }

    /// <summary>
    /// (Internal) Notifies a subscriber of an incremental result associated with an ongoing task.
    /// </summary>
    /// <remarks>
    /// Use this to provide partial results that may be available before the task fully completes.
    /// </remarks>
    /// <typeparam name="TIncrementalResult">The type of the result, usually different from <typeparamref name="TResult"/>.</typeparam>
    internal void NotifyIncrementalResult<TIncrementalResult>(TIncrementalResult incrementalResult)
        => IncrementalResultSubscriber<TIncrementalResult>.Notify(_id, incrementalResult);

    private readonly struct CombinedTaskData : IDisposable
    {
        public readonly OVRTask<List<TResult>> Task;

        private readonly HashSet<Guid> _remainingTaskIds;

        private readonly List<Guid> _originalTaskOrder;

        private readonly Dictionary<Guid, TResult> _completedTasks;

        private readonly List<TResult> _userOwnedResultList;

        private void OnSingleTaskCompleted(Guid taskId, TResult result)
        {
            _completedTasks.Add(taskId, result);
            _remainingTaskIds.Remove(taskId);

            if (_remainingTaskIds.Count == 0)
            {
                using (this)
                {
                    _userOwnedResultList.Clear();

                    // Sort the results so that they match the original task ordering
                    foreach (var id in _originalTaskOrder)
                    {
                        _userOwnedResultList.Add(_completedTasks[id]);
                    }

                    Task.SetResult(_userOwnedResultList);
                }
            }
        }

        private static readonly Action<TResult, CombinedTaskDataWithCompletedTaskId> _onSingleTaskCompleted = (result, data)
            => data.CombinedData.OnSingleTaskCompleted(data.CompletedTaskId, result);

        public CombinedTaskData(IEnumerable<OVRTask<TResult>> tasks, List<TResult> userOwnedResultList)
        {
            Task = OVRTask.FromGuid<List<TResult>>(Guid.NewGuid());
            _remainingTaskIds = OVRObjectPool.HashSet<Guid>();
            _originalTaskOrder = OVRObjectPool.List<Guid>();
            _completedTasks = OVRObjectPool.Dictionary<Guid, TResult>();
            _userOwnedResultList = userOwnedResultList;
            _userOwnedResultList.Clear();

            // Copy the provided tasks to a temp list to avoid double enumeration
            using (new OVRObjectPool.ListScope<OVRTask<TResult>>(out var taskList))
            {
                foreach (var task in tasks.ToNonAlloc())
                {
                    taskList.Add(task);
                    _remainingTaskIds.Add(task._id);
                    _originalTaskOrder.Add(task._id);
                }

                if (taskList.Count == 0)
                {
                    Task.SetResult(_userOwnedResultList);
                }
                else
                {
                    foreach (var task in taskList)
                    {
                        // If the task is already complete, this delegate will be invoked immediately, so make sure
                        // that all tasks have been added to the above collections before we start handling any
                        // completion events.
                        task.ContinueWith(_onSingleTaskCompleted, new CombinedTaskDataWithCompletedTaskId
                        {
                            CompletedTaskId = task._id,
                            CombinedData = this,
                        });
                    }
                }
            }
        }

        public void Dispose()
        {
            OVRObjectPool.Return(_remainingTaskIds);
            OVRObjectPool.Return(_originalTaskOrder);
            OVRObjectPool.Return(_completedTasks);
        }
    }

    private struct CombinedTaskDataWithCompletedTaskId
    {
        public Guid CompletedTaskId;
        public CombinedTaskData CombinedData;
    }

    internal static OVRTask<List<TResult>> WhenAll(IEnumerable<OVRTask<TResult>> tasks, List<TResult> results)
    {
        if (tasks == null)
            throw new ArgumentNullException(nameof(tasks));

        if (results == null)
            throw new ArgumentNullException(nameof(results));

        return new CombinedTaskData(tasks, results).Task;
    }

    internal static OVRTask<TResult[]> WhenAll(IEnumerable<OVRTask<TResult>> tasks)
    {
        if (tasks == null)
            throw new ArgumentNullException(nameof(tasks));

        var task = OVRTask.FromGuid<TResult[]>(Guid.NewGuid());
        var results = OVRObjectPool.List<TResult>();
        WhenAll(tasks, results).ContinueWith(_onCombinedTaskCompleted, task);
        return task;
    }

    private static readonly Action<List<TResult>, OVRTask<TResult[]>> _onCombinedTaskCompleted = (resultsFromPool, task) =>
    {
        var resultsArray = resultsFromPool.ToArray();
        OVRObjectPool.Return(resultsFromPool);
        task.SetResult(resultsArray);
    };
    /// \endcond

    #region Polling Implementation

    /// <summary>
    /// Indicates whether the task has completed.
    /// </summary>
    /// <remarks>
    /// Choose only one pattern out of the three proposed way of awaiting for the task completion:
    /// Polling,<c>async/await</c> or <see cref="ContinueWith(Action{TResult})"/>
    /// as all three patterns will end up calling the <see cref="GetResult"/> which can only be called once.
    /// </remarks>
    /// <returns><c>True</c> if the task has completed. <see cref="GetResult"/> can be called.</returns>
    public bool IsCompleted => !IsPending;

    /// <summary>
    /// Whether the task completed due to an unhandled exception
    /// </summary>
    /// <remarks>
    /// If the task is in a faulted state, then you can extract the exception with <see cref="GetException"/>.
    /// </remarks>
    public bool IsFaulted => Exceptions.ContainsKey(_id);

    /// <summary>
    /// Get the exception if the task is in a faulted state.
    /// </summary>
    /// <remarks>
    /// If <see cref="IsFaulted"/> is `true`, then this method gets the exception associated with this method. Similar
    /// to <see cref="GetResult"/>, you can only get the exception once and throws if there is no exception.
    ///
    /// When using `await` or <see cref="ContinueWith"/>, you do not need to explicitly get the exception. Use this
    /// method when you have an exception when it is implicitly created by the compiler and you query the task object
    /// directly, as in the following example:
    /// <example><code><![CDATA[
    /// async OVRTask<bool> DoSomethingAsync() {
    ///   var anchor = await OVRAnchor.CreateSpatialAnchorAsync(pose); // <-- implicitly generated OVRTask<bool>
    ///   SomeMethodThatThrows();
    ///   return true;
    /// }
    ///
    /// OVRTask<bool> task = DoSomethingAsync();
    ///
    /// // later...
    ///
    /// if (task.IsFaulted) {
    ///   throw task.GetException();
    /// }
    /// ]]></code></example>
    /// </remarks>
    /// <returns>Returns the `Exception` associated with the task.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="IsFaulted"/> is `false`.</exception>
    public Exception GetException() => Exceptions.Remove(_id, out var exception)
        ? exception
        : throw new InvalidOperationException($"Task {_id} is not in a faulted state. Check with {nameof(IsFaulted)}");

    /// <summary>
    /// Gets the result of the asynchronous operation.
    /// </summary>
    /// <remarks>
    /// This method should only be called once <see cref="IsCompleted"/> is true. Calling it multiple times
    /// will throw `InvalidOperationException`.
    ///
    /// Note that <see cref="GetResult"/> is called implicitly when using `await` or <see cref="ContinueWith"/>. You
    /// should not call this method explicitly when using one of those mechanisms.
    /// </remarks>
    /// <returns>Returns the result of type <typeparamref name="TResult"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the task doesn't have any available result. This could
    /// happen if the method is called before <see cref="IsCompleted"/> is true, after the task has been disposed of,
    /// if this method has already been called once, or if an exception was thrown during the task's execution.</exception>
    /// <seealso cref="HasResult"/>
    /// <seealso cref="TryGetResult"/>
    public TResult GetResult()
    {
        if (Exceptions.Remove(_id, out var exception))
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        if (!TryGetResult(out var value))
        {
            throw new InvalidOperationException($"Task {_id} doesn't have any available result.");
        }

        return value;
    }

    /// <summary>
    /// Whether there is a result available.
    /// </summary>
    /// <remarks>
    /// This property is true when the <see cref="OVRTask"/>&lt;TResult&gt; is complete (<see cref="IsCompleted"/> is `true`)
    /// and <see cref="GetResult"/> has not already been called.
    ///
    /// Note that <see cref="GetResult"/> is called implicitly when using `await` or <see cref="ContinueWith"/>.
    /// </remarks>
    /// <seealso cref="GetResult"/>
    /// <seealso cref="TryGetResult"/>
    public bool HasResult => Results.ContainsKey(_id);

    /// <summary>
    /// Tries to get the result of the asynchronous operation.
    /// </summary>
    /// <remarks>
    /// This method may safely be called at any time. It tests whether the operation is both complete
    /// (<see cref="IsCompleted"/> is `true`) and the result has not already been retrieved with <see cref="GetResult"/>
    /// (<see cref="HasResult"/> is `true`).
    ///
    /// If the result is available, <paramref name="result"/> is set to the result and this method returns `true`. This
    /// method is equivalent to (though more efficient than) the following:
    /// <code>
    /// <![CDATA[
    /// if (task.HasResult) {
    ///   result = task.GetResult();
    ///   return true;
    /// } else {
    ///   result = default;
    ///   return false;
    /// }
    /// ]]>
    /// </code>
    /// </remarks>
    /// <param name="result">Set to the result of the task, if one is available. Otherwise, it is set to the default
    /// value for <typeparamref name="TResult"/>.</param>
    /// <returns>`true` if this task is complete and a result is available.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the task doesn't have any available result. This could
    /// happen if the method is called before <see cref="IsCompleted"/> is true, after the task has been disposed of
    /// or if this method has already been called once.</exception>
    /// <seealso cref="HasResult"/>
    /// <seealso cref="GetResult"/>
    public bool TryGetResult(out TResult result) => Results.Remove(_id, out result);

    #endregion

    /// \cond
    private class TaskSource : IValueTaskSource<TResult>, OVRObjectPool.IPoolObject
    {
        private ManualResetValueTaskSourceCore<TResult> _manualSource;

        public ValueTask<TResult> Task { get; private set; }

        public TResult GetResult(short token)
        {
            try
            {
                return _manualSource.GetResult(token);
            }
            finally
            {
                OVRObjectPool.Return(this);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _manualSource.GetStatus(token);

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _manualSource.OnCompleted(continuation, state, token, flags);

        void OVRObjectPool.IPoolObject.OnGet()
        {
            _manualSource.Reset();
            Task = new(this, _manualSource.Version);
        }

        void OVRObjectPool.IPoolObject.OnReturn()
        { }

        public void SetResult(TResult result) => _manualSource.SetResult(result);

        public void SetException(Exception exception) => _manualSource.SetException(exception);
    }
    /// \endcond

    /// <summary>
    /// Converts the task to a ValueTask
    /// </summary>
    /// <remarks>
    /// This method converts this <see cref="OVRTask"/>&lt;TResult&gt; to a
    /// [ValueTask](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1?view=net-8.0).
    ///
    /// A `ValueTask` is similar to an `OVRTask`. Key differences:
    /// - A ValueTask does not support <see cref="ContinueWith"/>
    /// - A ValueTask does not support <see cref="WhenAll(System.Collections.Generic.IEnumerable{OVRTask{TResult}})"/>
    ///
    /// The above are only supported on the
    /// [Task](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task-1?view=net-8.0) object.
    ///
    /// Invoking this method also invalidates this <see cref="OVRTask"/>&lt;TResult&gt;. It is invalid to continue using an
    /// <see cref="OVRTask"/>&lt;TResult&gt; after calling <see cref="ToValueTask"/>.
    /// </remarks>
    /// <returns>Returns a new `ValueTask` that completes when the asynchronous operation completes.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the task is not pending (<see cref="IsCompleted"/> is true)
    /// and does not have a result (<see cref="HasResult"/> is false)</exception>
    /// <exception cref="InvalidOperationException">Thrown if the task has already been awaited.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="ContinueWith"/> has already been called.</exception>
    public ValueTask<TResult> ToValueTask()
    {
        var hasResult = Results.TryGetValue(_id, out var result);
        if (!Pending.Contains(_id) && !hasResult)
            throw new InvalidOperationException($"Task {_id} is not a valid task.");

        if (Continuations.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used by an await call.");

        if (ContinueWithInvokers.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used with ContinueWith.");

        using (this)
        {
            if (hasResult)
            {
                Results.Remove(_id);
                return new ValueTask<TResult>(result);
            }

            var source = OVRObjectPool.Get<TaskSource>();
            Sources.Add(_id, source);
            return source.Task;
        }
    }

#if !UNITY_2023_1_OR_NEWER
    /// \cond
    private class Awaitable<T> { }

    private class AwaitableCompletionSource<T>
    {
        public void SetResult(in T result) => throw new NotImplementedException();
        public void Reset() => throw new NotImplementedException();
        public void SetException(Exception exception) => throw new NotImplementedException();
        public Awaitable<T> Awaitable => throw new NotImplementedException();
    }
    /// \endcond
#endif

    private class AwaitableSource : AwaitableCompletionSource<TResult>, OVRObjectPool.IPoolObject
    {
        public void OnGet()
        {
            Reset();
        }

        public void OnReturn()
        { }

        public void SetResultAndReturnToPool(in TResult result)
        {
            try
            {
                SetResult(in result);
            }
            finally
            {
                OVRObjectPool.Return(this);
            }
        }
    }

#if UNITY_2023_1_OR_NEWER
    /// <summary>
    /// Converts the task to an Awaitable.
    /// </summary>
    /// <remarks>
    /// An `Awaitable` is a task-like object developed by Unity and provided in the `UnityEngine` namespace. For more
    /// details, refer to the section on
    /// [Await support](https://docs.unity3d.com/2023.2/Documentation/Manual/AwaitSupport.html) in the Unity manual.
    ///
    /// Awaitables are available starting with Unity 2023.1.
    ///
    /// Invoking this method invalidates this <see cref="OVRTask"/>&lt;TResult&gt;. It is invalid to continue using an
    /// <see cref="OVRTask"/>&lt;TResult&gt; after calling <see cref="ToAwaitable"/>.
    /// </remarks>
    /// <returns>Returns a new Awaitable that completes when the asynchronous operation completes.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the task is not pending (<see cref="IsCompleted"/> is true)
    /// and does not have a result (<see cref="HasResult"/> is false)</exception>
    /// <exception cref="InvalidOperationException">Thrown if the task has already been awaited.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="ContinueWith"/> has already been called.</exception>
    public Awaitable<TResult> ToAwaitable()
    {
        var hasResult = Results.TryGetValue(_id, out var result);
        if (!Pending.Contains(_id) && !hasResult)
            throw new InvalidOperationException($"Task {_id} is not a valid task.");

        if (Continuations.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used by an await call.");

        if (ContinueWithInvokers.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used with ContinueWith.");

        using (this)
        {
            var source = OVRObjectPool.Get<AwaitableSource>();
            if (hasResult)
            {
                source.SetResult(in result);
            }
            else
            {
                AwaitableSources.Add(_id, source);
            }
            return source.Awaitable;
        }
    }
#endif

    #region Awaiter Contract Implementation

    /// <summary>
    /// Definition of an awaiter that satisfies the await contract.
    /// </summary>
    /// <remarks>
    /// This allows an <see cref="OVRTask"/>&lt;TResult&gt; to be awaited using the <c>await</c> keyword.
    /// Typically, you should not use this struct; instead, it is used by the compiler by
    /// automatically calling the <see cref="GetAwaiter"/> method when using the <c>await</c> keyword.
    /// </remarks>
    public readonly struct Awaiter : INotifyCompletion
    {
        /// \cond
        private readonly OVRTask<TResult> _task;

        internal Awaiter(OVRTask<TResult> task)
        {
            _task = task;
        }
        /// \endcond

        /// <summary>
        /// Whether the task has completed
        /// </summary>
        /// <remarks>
        /// When `true` the asynchronous operation associated with the <see cref="OVRTask"/>&lt;TResult&gt; that created
        /// this <see cref="Awaiter"/> (see <see cref="OVRTask{TResult}.GetAwaiter"/>) is complete.
        ///
        /// Typically, you would not call this directly. This is queried by a compiler-generated state machine to
        /// support `async` / `await`.
        /// </remarks>
        public bool IsCompleted => _task.IsCompleted;

        /// <summary>
        /// Provides the Awaiter with a method to call when the task completes.
        /// </summary>
        /// <remarks>
        /// Do not call this directly. It is called by a compiler-generated state machine when using the `await`
        /// keyword.
        /// </remarks>
        /// <param name="continuation">The continuation to invoke when the task is complete.</param>
        void INotifyCompletion.OnCompleted(Action continuation) => _task.WithContinuation(continuation);

        /// <summary>
        /// Gets the result of the asynchronous operation.
        /// </summary>
        /// <remarks>
        /// Typically, you should not call this directly. Use <see cref="OVRTask{TResult}.GetResult()"/> instead.
        /// </remarks>
        /// <returns>The result of the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if there is no result available.</exception>
        public TResult GetResult() => _task.GetResult();
    }

    /// <summary>
    /// Gets an awaiter that satisfies the await contract.
    /// </summary>
    /// <remarks>
    /// This allows an <see cref="OVRTask"/>&lt;TResult&gt; to be awaited using the <c>await</c> keyword.
    /// Typically, you should not call this directly; instead, it is invoked by the compiler, e.g.,
    /// <example>
    /// <code><![CDATA[
    /// // Something that returns an OVRTask<T>
    /// var task = GetResultAsync();
    ///
    /// // compiler uses GetAwaiter here
    /// var result = await task;
    /// ]]></code>
    /// Or, more commonly:
    /// <code><![CDATA[
    /// var result = await GetResultAsync();
    /// ]]></code>
    /// </example>
    /// Requires the main thread to complete the await contract - blocking can result in an infinite loop.
    /// </remarks>
    /// <returns>Returns an Awaiter-like object that satisfies the await pattern.</returns>
    public Awaiter GetAwaiter() => new Awaiter(this);

    private void WithContinuation(Action continuation)
    {
        ValidateDelegateAndThrow(continuation, nameof(continuation));

        Continuations[_id] = continuation;
    }

    #endregion

    #region Delegate Implementation

    /// \cond
    private readonly struct Callback
    {
        private static readonly Dictionary<Guid, Callback> Callbacks = new Dictionary<Guid, Callback>();

        readonly Action<TResult> _delegate;

        static void Invoke(Guid taskId, TResult result)
        {
            if (Callbacks.TryGetValue(taskId, out var callback))
            {
                Callbacks.Remove(taskId);
                callback.Invoke(result);
            }
        }

        static bool Remove(Guid taskId) => Callbacks.Remove(taskId);

        static void Clear() => Callbacks.Clear();

        void Invoke(TResult result) => _delegate(result);

        Callback(Action<TResult> @delegate) => _delegate = @delegate;

        public static readonly ContinueWithInvoker Invoker = Invoke;

        public static readonly ContinueWithRemover Remover = Remove;

        public static readonly Action Clearer = Clear;

        public static void Add(Guid taskId, Action<TResult> @delegate)
        {
            Callbacks.Add(taskId, new Callback(@delegate));
            ContinueWithInvokers.Add(taskId, Invoker);
            ContinueWithRemovers.Add(taskId, Remover);
            ContinueWithClearers.Add(Clearer);
        }
    }

    private readonly struct CallbackWithState<T>
    {
        private static readonly Dictionary<Guid, CallbackWithState<T>> Callbacks =
            new Dictionary<Guid, CallbackWithState<T>>();

        private readonly T _data;

        private readonly Action<TResult, T> _delegate;

        private static void Invoke(Guid taskId, TResult result)
        {
            if (Callbacks.TryGetValue(taskId, out var callback))
            {
                Callbacks.Remove(taskId);
                callback.Invoke(result);
            }
        }

        private CallbackWithState(T data, Action<TResult, T> @delegate)
        {
            _data = data;
            _delegate = @delegate;
        }

        private static readonly ContinueWithInvoker Invoker = Invoke;
        private static readonly ContinueWithRemover Remover = Remove;
        private static readonly Action Clearer = Clear;
        private static void Clear() => Callbacks.Clear();
        private static bool Remove(Guid taskId) => Callbacks.Remove(taskId);
        private void Invoke(TResult result) => _delegate(result, _data);

        public static void Add(Guid taskId, T data, Action<TResult, T> callback)
        {
            Callbacks.Add(taskId, new CallbackWithState<T>(data, callback));
            ContinueWithInvokers.Add(taskId, Invoker);
            ContinueWithRemovers.Add(taskId, Remover);
            ContinueWithClearers.Add(Clearer);
        }
    }
    /// \endcond

    /// <summary>
    /// Registers a delegate to be invoked on completion of the task.
    /// </summary>
    /// <remarks>
    /// The delegate will be invoked with the <typeparamref name="TResult"/> result as parameter.
    ///
    /// Do not use in conjunction with any other methods (`await` or calling <see cref="GetResult"/>).
    ///
    /// Note: If the task throws an exception during execution, there is no way to catch it in when using
    /// <see cref="ContinueWith"/>. Most Meta XR Core SDK calls that return an OVRTask do not throw, but it is possible
    /// to return an <see cref="OVRTask"/>&lt;TResult&gt; from your own async method, which can still throw. For example,
    /// <code><![CDATA[
    /// async OVRTask<OVRAnchor> DoSomethingAsync() {
    ///   var anchor = await OVRAnchor.CreateSpatialAnchorAsync(pose); // doesn't throw
    ///   throw new Exception(); // Cannot be caught if using ContinueWith
    ///   return anchor;
    /// }
    ///
    /// async void MethodA() {
    ///   try {
    ///     var anchor = await DoSomethingAsync();
    ///   } catch (Exception e) {
    ///     // okay; exception caught!
    ///   }
    /// }
    ///
    /// void MethodB() {
    ///   DoSomethingAsync().ContinueWith(anchor => {
    ///     Debug.Log($"Anchor {anchor} created!");
    ///   });
    /// }
    /// ]]></code>
    ///
    /// In the above example, the exception generated by `DoSomethingAsync` is caught in `MethodA`, but there is no
    /// way to catch it in `MethodB` because it uses <see cref="ContinueWith"/> rather than `await`. The exception
    /// is still thrown, however.
    /// </remarks>
    /// <param name="onCompleted">A delegate to be invoked when this task completes. If the task is already complete,
    /// <paramref name="onCompleted"/> is invoked immediately.</param>
    /// <seealso cref="ContinueWith{T}"/>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="onCompleted"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already being awaited.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used in another <see cref="ContinueWith"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used as a `ValueTask` (<see cref="ToValueTask"/>).</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used or as an `Awaitable`.
    /// (<see cref="ToAwaitable"/>, only available in Unity 2023.1+).</exception>
    public void ContinueWith(Action<TResult> onCompleted)
    {
        ValidateDelegateAndThrow(onCompleted, nameof(onCompleted));

        if (IsCompleted)
        {
            onCompleted.Invoke(GetResult());
        }
        else
        {
            Callback.Add(_id, onCompleted);
        }
    }

    /// <summary>
    /// Registers a delegate that will get called on completion of the task.
    /// </summary>
    /// <remarks>
    /// The delegate will be invoked with <paramref name="state"/> and the <typeparamref name="TResult"/> result as
    /// parameters.
    /// Do not use in conjunction with any other methods (`await` or calling <see cref="GetResult"/>).
    ///
    /// Note: If the task throws an exception during execution, there is no way to catch it in when using a callback.
    /// See <see cref="ContinueWith(Action{TResult})"/> for more details.
    /// </remarks>
    /// <param name="onCompleted">A delegate to be invoked when this task completes. If the task is already complete,
    /// <paramref name="onCompleted"/> is invoked immediately.</param>
    /// <param name="state">An object to store and pass to <paramref name="onCompleted"/>.</param>
    /// <seealso cref="ContinueWith(Action{TResult})"/>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="onCompleted"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already being awaited.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used in another <see cref="ContinueWith"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used as a `ValueTask` (<see cref="ToValueTask"/>).</exception>
    /// <exception cref="InvalidOperationException">Thrown if this task is already used or as an `Awaitable`.
    /// (<see cref="ToAwaitable"/>, only available in Unity 2023.1+).</exception>
    public void ContinueWith<T>(Action<TResult, T> onCompleted, T state)
    {
        ValidateDelegateAndThrow(onCompleted, nameof(onCompleted));

        if (IsCompleted)
        {
            onCompleted.Invoke(GetResult(), state);
        }
        else
        {
            CallbackWithState<T>.Add(_id, state, onCompleted);
        }
    }

    private void ValidateDelegateAndThrow(object @delegate, string paramName)
    {
        if (@delegate == null)
            throw new ArgumentNullException(paramName);

        if (Continuations.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used by an await call.");

        if (ContinueWithInvokers.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used with ContinueWith.");

        if (Sources.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used as a ValueTask.");

        if (AwaitableSources.ContainsKey(_id))
            throw new InvalidOperationException($"Task {_id} is already being used as an Awaitable.");
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Disposes of the task.
    /// </summary>
    /// <remarks>
    /// Invalidate this object but does not cancel the task.
    /// In the case where the result will not actually be consumed, it must be called to prevent a memory leak.
    /// You can not call <see cref="GetResult"/> nor use <c>await</c> on a disposed task.
    /// </remarks>
    public void Dispose()
    {
        Results.Remove(_id);
        Continuations.Remove(_id);
        Pending.Remove(_id);

        ContinueWithInvokers.Remove(_id);
        if (ContinueWithRemovers.TryGetValue(_id, out var remover))
        {
            ContinueWithRemovers.Remove(_id);
            remover(_id);
        }

        if (InternalDataRemovers.TryGetValue(_id, out var internalDataRemover))
        {
            InternalDataRemovers.Remove(_id);
            internalDataRemover(_id);
        }

        if (IncrementalResultSubscriberRemovers.TryGetValue(_id, out var subscriberRemover))
        {
            IncrementalResultSubscriberRemovers.Remove(_id);
            subscriberRemover(_id);
        }
    }

    #endregion

    #region IEquatable Implementation
    /// <summary>
    /// Compares for equality with another task.
    /// </summary>
    /// <param name="other">The <see cref="OVRTask"/>&lt;TResult&gt; to compare for equality with this task.</param>
    /// <returns>Returns `true` if the two task objects represent the same task, otherwise `false`.</returns>
    public bool Equals(OVRTask<TResult> other) => _id == other._id;

    /// <summary>
    /// Compares for equality with an `object`.
    /// </summary>
    /// <param name="obj">The `object` to compare for equality with this task.</param>
    /// <returns>Returns `true` if <paramref name="obj"/> is an <see cref="OVRTask"/>&lt;TResult&gt; and represents the same
    /// task as this one, otherwise `false`.</returns>
    public override bool Equals(object obj) => obj is OVRTask<TResult> other && Equals(other);

    /// <summary>
    /// Compares two tasks for equality.
    /// </summary>
    /// <remarks>
    /// This is the same equality test as <see cref="Equals(OVRTask{TResult})"/>.
    /// </remarks>
    /// <param name="lhs">The task to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The task to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator ==(OVRTask<TResult> lhs, OVRTask<TResult> rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two tasks for inequality.
    /// </summary>
    /// <remarks>
    /// This is the logical negation of <see cref="Equals(OVRTask{TResult})"/>.
    /// </remarks>
    /// <param name="lhs">The task to compare with <paramref name="rhs"/>.</param>
    /// <param name="rhs">The task to compare with <paramref name="lhs"/>.</param>
    /// <returns>Returns `true` if <paramref name="lhs"/> is not equal to <paramref name="rhs"/>, otherwise `false`.</returns>
    public static bool operator !=(OVRTask<TResult> lhs, OVRTask<TResult> rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Generates a hash code suitable for use in a `Dictionary` or `HashSet`
    /// </summary>
    /// <returns>Returns a hash code suitable for use in a `Dictionary` or `HashSet`</returns>
    public override int GetHashCode() => _id.GetHashCode();

    /// <summary>
    /// Generates a string representation of this task, based on its internal id.
    /// </summary>
    /// <remarks>
    /// Internally, tasks are identified by a `System.Guid`. This method should only be used for debugging purposes.
    /// The implementation or behavior may change in future versions, so you should not rely on the format of this
    /// string for other uses.
    /// </remarks>
    /// <returns>Returns the stringification of this task's unique identifier (Guid).</returns>
    public override string ToString() => _id.ToString();

    #endregion
}

#region Task builder
/// <summary>
/// The AsyncMethodBuilder for <see cref="OVRTask"/>&lt;TResult&gt;.
/// </summary>
/// <remarks>
/// Do not use this type directly. It is used by the compiler to allow <see cref="OVRTask"/>&lt;TResult&gt; to be used as
/// a task-like object, that is, you can await on it from an awaitable function.
///
/// For more information see [Asynchronous Tasks](https://developer.oculus.com/documentation/unity/unity-async-tasks/).
/// </remarks>
/// <typeparam name="T">The type of the result of an asynchronous operation.</typeparam>
/// <seealso cref="OVRTask"/>
public struct OVRTaskBuilder<T>
{
    /// \cond
    private abstract class PooledStateMachine : IDisposable
    {
        public OVRTask<T>? Task;
        public Action MoveNext;
        public abstract void Dispose();
    }

    private class PooledStateMachine<TStateMachine> : PooledStateMachine, OVRObjectPool.IPoolObject
        where TStateMachine : IAsyncStateMachine
    {
        public TStateMachine StateMachine;

        public static PooledStateMachine<TStateMachine> Get() => OVRObjectPool.Get<PooledStateMachine<TStateMachine>>();

        public override void Dispose() => OVRObjectPool.Return(this);

        public PooledStateMachine() => MoveNext = ExecuteMoveNext;

        private void ExecuteMoveNext() => StateMachine.MoveNext();

        void OVRObjectPool.IPoolObject.OnGet()
        {
            StateMachine = default;
            Task = null;
        }

        void OVRObjectPool.IPoolObject.OnReturn()
        {
            StateMachine = default;
            Task = null;
        }
    }

    private PooledStateMachine _pooledStateMachine;

    private OVRTask<T>? _task;

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public OVRTask<T> Task
    {
        get
        {
            if (_task.HasValue)
            {
                return _task.Value;
            }

            if (_pooledStateMachine != null)
            {
                return (_task = _pooledStateMachine.Task ??= OVRTask.FromGuid<T>(Guid.NewGuid())).Value;
            }

            return (_task = OVRTask.FromGuid<T>(Guid.NewGuid())).Value;
        }
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var pooledStateMachine = GetPooledStateMachine<TStateMachine>();
        ((PooledStateMachine<TStateMachine>)pooledStateMachine).StateMachine = stateMachine;
        awaiter.OnCompleted(pooledStateMachine.MoveNext);
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var pooledStateMachine = GetPooledStateMachine<TStateMachine>();
        ((PooledStateMachine<TStateMachine>)pooledStateMachine).StateMachine = stateMachine;
        awaiter.UnsafeOnCompleted(pooledStateMachine.MoveNext);
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        var pooledStateMachine = GetPooledStateMachine<TStateMachine>();
        ((PooledStateMachine<TStateMachine>)pooledStateMachine).StateMachine = stateMachine;
        stateMachine.MoveNext();
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public static OVRTaskBuilder<T> Create() => default;

    private PooledStateMachine GetPooledStateMachine<TStateMachine>() where TStateMachine : IAsyncStateMachine
    {
        if (_pooledStateMachine == null)
        {
            _pooledStateMachine = PooledStateMachine<TStateMachine>.Get();
            _pooledStateMachine.Task = _task;
        }

        return _pooledStateMachine;
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void SetException(Exception exception)
    {
        Task.SetException(exception);
        _pooledStateMachine?.Dispose();
        _pooledStateMachine = null;
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void SetResult(T result)
    {
        Task.SetResult(result);
        _pooledStateMachine?.Dispose();
        _pooledStateMachine = null;
    }

    /// <summary>This is provided for use by the .NET runtime and should not be used directly.</summary>
    public void SetStateMachine(IAsyncStateMachine stateMachine)
    { }
    /// \endcond
}
#endregion
