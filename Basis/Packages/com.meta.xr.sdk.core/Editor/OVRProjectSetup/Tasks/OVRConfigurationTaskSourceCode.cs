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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

internal class OVRConfigurationTaskSourceCode
{
    private static Func<Object, int, bool> OpenAssetDelegate = AssetDatabase.OpenAsset;

    public static void Mock()
    {
        OpenAssetDelegate = null;
    }

    public static void Unmock()
    {
        OpenAssetDelegate = AssetDatabase.OpenAsset;
    }

    private static IEnumerable<MethodInfo> _expectedMethods;

    private OVRConfigurationTask _task;
    private readonly StackTrace _stackTrace;
    private Object _object;
    private bool _processed;

    public bool Valid
    {
        get
        {
            if (!_processed)
            {
                ProcessStackTrace();
            }

            return _object != null;
        }
    }

    public int Line { get; private set; }
    public string FilePath { get; private set; }

    public OVRConfigurationTaskSourceCode(OVRConfigurationTask task)
    {
        _task = task;
        _stackTrace = new StackTrace(true);
    }

    public void ProcessStackTrace()
    {
        if (FindPathAndLine(out var path, out var line))
        {

            path = path.Replace("\\", "/");
            path = Path.GetFileNameWithoutExtension(path);
            var assets = AssetDatabase.FindAssets(path);
            foreach (var asset in assets)
            {
                FilePath = AssetDatabase.GUIDToAssetPath(asset);
                Line = line;
                _object = AssetDatabase.LoadAssetAtPath(FilePath, typeof(Object));
                if (_object != null)
                {
                    break;
                }
            }
        }

        _processed = true;
    }

    public bool Open()
    {
        if (Valid)
        {
            OpenAssetDelegate?.Invoke(_object, Line);
        }

        OVRTelemetry.Start(OVRProjectSetupTelemetryEvent.EventTypes.GoToSource)
            .AddAnnotation(OVRProjectSetupTelemetryEvent.AnnotationTypes.Uid, _task.Uid.ToString())
            .AddAnnotation(OVRProjectSetupTelemetryEvent.AnnotationTypes.BuildTargetGroup,
                BuildTargetGroup.Unknown.ToString())
            .AddAnnotation(OVRProjectSetupTelemetryEvent.AnnotationTypes.Value, Valid ? "true" : "false")
            .Send();

        return Valid;
    }

    private StackFrame FindStackFrame()
    {
        // Find method before the ovrconfigurationTask constructor
        for (int i = 2; i < 6; i++)
        {
            StackFrame frame = _stackTrace.GetFrame(i);
            var type = frame.GetMethod().ReflectedType;
            if (type != typeof(OVRConfigurationTask) && type != typeof(OVRProjectSetup))
            {
                return frame;
            }

        }
        return null;
    }

    private bool FindPathAndLine(out string path, out int line)
    {
        var frame = FindStackFrame();
        if (frame == null)
        {
            path = null;
            line = -1;
            return false;
        }

        path = frame.GetFileName();
        line = frame.GetFileLineNumber();
        return true;
    }
}
