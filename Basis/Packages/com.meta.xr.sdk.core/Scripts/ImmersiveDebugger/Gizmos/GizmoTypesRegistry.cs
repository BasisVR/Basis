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
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Gizmo
{
    internal struct GizmoTypeInfo
    {
        public readonly Action<object> RenderDelegate;
        public GizmoTypeInfo(Action<object> renderDelegate)
        {
            RenderDelegate = renderDelegate;
        }
    }

    internal static class GizmoTypesRegistry
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            GizmoTypeInfos?.Clear();
        }

        private static readonly Dictionary<(DebugGizmoType, Type), GizmoTypeInfo> GizmoTypeInfos = new();

        public static void RegisterGizmoType(DebugGizmoType gizmoType, Type dataSourceType, Action<object> renderDelegate)
        {
            GizmoTypeInfos.Add((gizmoType, dataSourceType), new GizmoTypeInfo(renderDelegate));
        }

        public static void InitGizmos()
        {
            RegisterGizmoType(DebugGizmoType.Axis, typeof(Pose), dataSource =>
            {
                if (dataSource is Pose pose)
                {
                    DebugGizmos.DrawAxis(pose);
                }
            });
            RegisterGizmoType(DebugGizmoType.Axis, typeof(Transform), dataSource =>
            {
                if (dataSource is Transform transform)
                {
                    DebugGizmos.DrawAxis(transform);
                }
            });
            RegisterGizmoType(DebugGizmoType.Point, typeof(Vector3), dataSource =>
            {
                if (dataSource is Vector3 position)
                {
                    DebugGizmos.DrawPoint(position);
                }
            });
            RegisterGizmoType(DebugGizmoType.Point, typeof(Transform), dataSource =>
            {
                if (dataSource is Transform transform)
                {
                    DebugGizmos.DrawPoint(transform.position);
                }
            });
            RegisterGizmoType(DebugGizmoType.Line, typeof(Tuple<Vector3, Vector3>), dataSource =>
            {
                if (dataSource is Tuple<Vector3, Vector3> lineStartEndPair)
                {
                    DebugGizmos.DrawLine(lineStartEndPair.Item1, lineStartEndPair.Item2);
                }
            });
            RegisterGizmoType(DebugGizmoType.Lines, typeof(Vector3[]), dataSource =>
            {
                if (dataSource is Vector3[] lines)
                {
                    for (int i = 1; i < lines.Length; i++)
                    {
                        DebugGizmos.DrawLine(lines[i - 1], lines[i]);
                    }
                }
            });
            RegisterGizmoType(DebugGizmoType.Plane, typeof(Tuple<Pose, float, float>), dataSource =>
            {
                if (dataSource is Tuple<Pose, float, float> planeData)
                {
                    DebugGizmos.DrawPlane(planeData.Item1, planeData.Item2, planeData.Item3);
                }
            });
            RegisterGizmoType(DebugGizmoType.Cube, typeof(Tuple<Vector3, float>), dataSource =>
            {
                if (dataSource is Tuple<Vector3, float> cubeData)
                {
                    DebugGizmos.DrawWireCube(cubeData.Item1, cubeData.Item2);
                }
            });
            RegisterGizmoType(DebugGizmoType.TopCenterBox, typeof(Tuple<Pose, float, float, float>), dataSource =>
            {
                if (dataSource is Tuple<Pose, float, float, float> boxData)
                {
                    DebugGizmos.DrawBox(boxData.Item1, boxData.Item2, boxData.Item3, boxData.Item4, true);
                }
            });
            RegisterGizmoType(DebugGizmoType.Box, typeof(Tuple<Pose, float, float, float>), dataSource =>
            {
                if (dataSource is Tuple<Pose, float, float, float> boxData)
                {
                    DebugGizmos.DrawBox(boxData.Item1, boxData.Item2, boxData.Item3, boxData.Item4, false);
                }
            });
        }

        public static bool IsValidDataTypeForGizmoType(Type type, DebugGizmoType gizmoType)
        {
            if (GizmoTypeInfos.TryGetValue((gizmoType, type), out _))
            {
                return true;
            }
            Debug.LogWarning($"{gizmoType} not found in GizmoTypeInfos, please registerGizmoType.");
            return false;
        }

        public static void RenderGizmo(DebugGizmoType type, object dataSource)
        {
            if (dataSource == null)
            {
                return;
            }

            if (GizmoTypeInfos.TryGetValue((type, dataSource.GetType()), out var typeInfo))
            {
                typeInfo.RenderDelegate(dataSource);
            }
        }

    }
}

