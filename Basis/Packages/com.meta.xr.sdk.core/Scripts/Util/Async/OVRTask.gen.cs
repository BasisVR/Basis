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

static partial class OVRTask
{
    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2) = await OVRTask.Combine(task1, task2);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2)> WhenAll<T1, T2>(OVRTask<T1> task1, OVRTask<T2> task2)
        => MultiTaskData<T1, T2>.Get(task1, task2);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3) = await OVRTask.Combine(task1, task2, task3);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3)> WhenAll<T1, T2, T3>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3)
        => MultiTaskData<T1, T2, T3>.Get(task1, task2, task3);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3, T4>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3, result4) = await OVRTask.Combine(task1, task2, task3, task4);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <param name="task4">The forth task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3, T4)> WhenAll<T1, T2, T3, T4>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4)
        => MultiTaskData<T1, T2, T3, T4>.Get(task1, task2, task3, task4);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3, T4, T5>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3, result4, result5) = await OVRTask.Combine(task1, task2, task3, task4, task5);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <param name="task4">The forth task to combine</param>
    /// <param name="task5">The fifth task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3, T4, T5)> WhenAll<T1, T2, T3, T4, T5>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5)
        => MultiTaskData<T1, T2, T3, T4, T5>.Get(task1, task2, task3, task4, task5);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3, T4, T5, T6>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3, result4, result5, result6) = await OVRTask.Combine(task1, task2, task3, task4, task5, task6);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <param name="task4">The forth task to combine</param>
    /// <param name="task5">The fifth task to combine</param>
    /// <param name="task6">The sixth task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3, T4, T5, T6)> WhenAll<T1, T2, T3, T4, T5, T6>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6)
        => MultiTaskData<T1, T2, T3, T4, T5, T6>.Get(task1, task2, task3, task4, task5, task6);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3, T4, T5, T6, T7>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3, result4, result5, result6, result7) = await OVRTask.Combine(task1, task2, task3, task4, task5, task6, task7);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <param name="task4">The forth task to combine</param>
    /// <param name="task5">The fifth task to combine</param>
    /// <param name="task6">The sixth task to combine</param>
    /// <param name="task7">The seventh task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3, T4, T5, T6, T7)> WhenAll<T1, T2, T3, T4, T5, T6, T7>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6, OVRTask<T7> task7)
        => MultiTaskData<T1, T2, T3, T4, T5, T6, T7>.Get(task1, task2, task3, task4, task5, task6, task7);

    /// <summary>
    /// Creates a new task that will complete when all of the given tasks have completed.
    /// </summary>
    /// <remarks>
    /// This method can be used to combine multiple tasks into a single task that depends on all of them. When the returned task
    /// completes, the results of all combined tasks can be accessed through the result of the returned task. The type of the result
    /// is a tuple containing the result types of each input task, that is <code><![CDATA[ValueTuple<T1, T2, T3, T4, T5, T6, T7, T8>]]></code>
    /// <example>
    /// For example,
    /// <code><![CDATA[
    /// var (result1, result2, result3, result4, result5, result6, result7, result8) = await OVRTask.Combine(task1, task2, task3, task4, task5, task6, task7, task8);
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="task1">The first task to combine</param>
    /// <param name="task2">The second task to combine</param>
    /// <param name="task3">The third task to combine</param>
    /// <param name="task4">The forth task to combine</param>
    /// <param name="task5">The fifth task to combine</param>
    /// <param name="task6">The sixth task to combine</param>
    /// <param name="task7">The seventh task to combine</param>
    /// <param name="task8">The eighth task to combine</param>
    /// <returns>Returns a task that represents the completion of all input tasks.</returns>
    public static OVRTask<(T1, T2, T3, T4, T5, T6, T7, T8)> WhenAll<T1, T2, T3, T4, T5, T6, T7, T8>(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6, OVRTask<T7> task7, OVRTask<T8> task8)
        => MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>.Get(task1, task2, task3, task4, task5, task6, task7, task8);


    class MultiTaskData<T1, T2> : MultiTaskData<(T1, T2)>
    {
        public static OVRTask<(T1, T2)> Get(OVRTask<T1> task1, OVRTask<T2> task2)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3> : MultiTaskData<(T1, T2, T3)>
    {
        public static OVRTask<(T1, T2, T3)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3, T4> : MultiTaskData<(T1, T2, T3, T4)>
    {
        public static OVRTask<(T1, T2, T3, T4)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3, T4>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            data.AddTask(task4._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            task4.ContinueWith(_onResult4, (task4._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3, T4>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3, T4>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3, T4>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T4, (Guid, MultiTaskData<T1, T2, T3, T4>)> _onResult4 = (result, data) =>
        {
            data.Item2.Result.Item4 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3, T4, T5> : MultiTaskData<(T1, T2, T3, T4, T5)>
    {
        public static OVRTask<(T1, T2, T3, T4, T5)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3, T4, T5>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            data.AddTask(task4._id);
            data.AddTask(task5._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            task4.ContinueWith(_onResult4, (task4._id, data));
            task5.ContinueWith(_onResult5, (task5._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3, T4, T5>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3, T4, T5>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3, T4, T5>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T4, (Guid, MultiTaskData<T1, T2, T3, T4, T5>)> _onResult4 = (result, data) =>
        {
            data.Item2.Result.Item4 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T5, (Guid, MultiTaskData<T1, T2, T3, T4, T5>)> _onResult5 = (result, data) =>
        {
            data.Item2.Result.Item5 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3, T4, T5, T6> : MultiTaskData<(T1, T2, T3, T4, T5, T6)>
    {
        public static OVRTask<(T1, T2, T3, T4, T5, T6)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3, T4, T5, T6>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            data.AddTask(task4._id);
            data.AddTask(task5._id);
            data.AddTask(task6._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            task4.ContinueWith(_onResult4, (task4._id, data));
            task5.ContinueWith(_onResult5, (task5._id, data));
            task6.ContinueWith(_onResult6, (task6._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T4, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult4 = (result, data) =>
        {
            data.Item2.Result.Item4 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T5, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult5 = (result, data) =>
        {
            data.Item2.Result.Item5 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T6, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6>)> _onResult6 = (result, data) =>
        {
            data.Item2.Result.Item6 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3, T4, T5, T6, T7> : MultiTaskData<(T1, T2, T3, T4, T5, T6, T7)>
    {
        public static OVRTask<(T1, T2, T3, T4, T5, T6, T7)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6, OVRTask<T7> task7)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3, T4, T5, T6, T7>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            data.AddTask(task4._id);
            data.AddTask(task5._id);
            data.AddTask(task6._id);
            data.AddTask(task7._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            task4.ContinueWith(_onResult4, (task4._id, data));
            task5.ContinueWith(_onResult5, (task5._id, data));
            task6.ContinueWith(_onResult6, (task6._id, data));
            task7.ContinueWith(_onResult7, (task7._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T4, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult4 = (result, data) =>
        {
            data.Item2.Result.Item4 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T5, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult5 = (result, data) =>
        {
            data.Item2.Result.Item5 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T6, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult6 = (result, data) =>
        {
            data.Item2.Result.Item6 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T7, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7>)> _onResult7 = (result, data) =>
        {
            data.Item2.Result.Item7 = result;
            data.Item2.OnResult(data.Item1);
        };
    }

    class MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8> : MultiTaskData<(T1, T2, T3, T4, T5, T6, T7, T8)>
    {
        public static OVRTask<(T1, T2, T3, T4, T5, T6, T7, T8)> Get(OVRTask<T1> task1, OVRTask<T2> task2, OVRTask<T3> task3, OVRTask<T4> task4, OVRTask<T5> task5, OVRTask<T6> task6, OVRTask<T7> task7, OVRTask<T8> task8)
        {
            var data = OVRObjectPool.Get<MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>>();
            data.AddTask(task1._id);
            data.AddTask(task2._id);
            data.AddTask(task3._id);
            data.AddTask(task4._id);
            data.AddTask(task5._id);
            data.AddTask(task6._id);
            data.AddTask(task7._id);
            data.AddTask(task8._id);
            task1.ContinueWith(_onResult1, (task1._id, data));
            task2.ContinueWith(_onResult2, (task2._id, data));
            task3.ContinueWith(_onResult3, (task3._id, data));
            task4.ContinueWith(_onResult4, (task4._id, data));
            task5.ContinueWith(_onResult5, (task5._id, data));
            task6.ContinueWith(_onResult6, (task6._id, data));
            task7.ContinueWith(_onResult7, (task7._id, data));
            task8.ContinueWith(_onResult8, (task8._id, data));
            return data.CombinedTask;
        }

        static Action<T1, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult1 = (result, data) =>
        {
            data.Item2.Result.Item1 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T2, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult2 = (result, data) =>
        {
            data.Item2.Result.Item2 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T3, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult3 = (result, data) =>
        {
            data.Item2.Result.Item3 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T4, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult4 = (result, data) =>
        {
            data.Item2.Result.Item4 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T5, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult5 = (result, data) =>
        {
            data.Item2.Result.Item5 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T6, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult6 = (result, data) =>
        {
            data.Item2.Result.Item6 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T7, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult7 = (result, data) =>
        {
            data.Item2.Result.Item7 = result;
            data.Item2.OnResult(data.Item1);
        };

        static Action<T8, (Guid, MultiTaskData<T1, T2, T3, T4, T5, T6, T7, T8>)> _onResult8 = (result, data) =>
        {
            data.Item2.Result.Item8 = result;
            data.Item2.OnResult(data.Item1);
        };
    }
}
