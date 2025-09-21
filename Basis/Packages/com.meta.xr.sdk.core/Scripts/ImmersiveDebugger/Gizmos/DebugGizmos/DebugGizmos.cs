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
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.Gizmo
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> responsible for drawing debug gizmos for Immersive Debugger.
    /// It contains methods and configuration options for drawing builtin types within the class.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [ExecuteAlways]
    public class DebugGizmos : MonoBehaviour
    {
        private List<Vector4> _points = new();
        private List<Color> _colors = new();
        private int _index;
        private bool _addedSegmentSinceLastUpdate;

#if UNITY_EDITOR
        private bool _drewGizmos;
        private int _sceneRepaint;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            _root = null;
            _renderSinglePass = true;
            Color = Color.black;
            LineWidth = 0.1f;
        }

        protected static DebugGizmos _root;

        protected static DebugGizmos Root
        {
            get
            {
                if (_root == null)
                {
                    // Use Find instead of FindObjectsByType<> as the extra parameter
                    // is unsupported by later versions of Unity
                    GameObject polylineGizmosGO = GameObject.Find("Polyline Gizmos");
                    if (polylineGizmosGO != null)
                    {
                        DebugGizmos gizmos = polylineGizmosGO.GetComponent<DebugGizmos>();
                        if (gizmos != null)
                        {
                            _root = gizmos;
#if UNITY_EDITOR
                            if (_root.isActiveAndEnabled)
                            {
                                _root.HookUpToEditorEvents();
                            }
#endif
                        }
                    }
                }

                if (_root == null)
                {
                    GameObject go = new GameObject("Polyline Gizmos");
                    _root = go.AddComponent<DebugGizmos>();

                    if (Application.isPlaying)
                    {
                        DontDestroyOnLoad(go);
                    }
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        EditorUtility.SetDirty(_root);
                    }

                    _root.HookUpToEditorEvents();
#endif
                }

                return _root;
            }
        }

        protected virtual void OnEnable()
        {
            if (_root == null)
            {
                return;
            }

#if !UNITY_EDITOR
            if (_root != this)
            {
                Destroy(this);
            }
#else
            if (_root == this)
            {
                if (!Application.isPlaying)
                {
                    HookUpToEditorEvents();
                }
            }
            else
            {
                enabled = false;
                if (Application.isPlaying)
                {
                    Destroy(this);
                }
                else
                {
                    EditorApplication.update += MarkForDestroy;
                }
            }
#endif
        }


#if UNITY_EDITOR
        protected virtual void OnDrawGizmos()
        {
            if (Application.isPlaying)
            {
                return;
            }

            if (_sceneRepaint == 0)
            {
                _drewGizmos = true;
                _sceneRepaint = 2;
                SceneView.RepaintAll();
            }
        }

        private void MarkForDestroy()
        {
            EditorApplication.update -= MarkForDestroy;
            DestroyImmediate(this);
        }

        private void HookUpToEditorEvents()
        {
            if (Application.isPlaying)
            {
                return;
            }
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
            Camera.onPreCull += HandlePreCullRender;
        }

        private void HandlePreCullRender(Camera cam)
        {
            if (_drewGizmos && !_addedSegmentSinceLastUpdate)
            {
                ClearSegments();
            }

            _addedSegmentSinceLastUpdate = false;
            _drewGizmos = false;

            RenderSegments();

            if (_sceneRepaint > 0)
            {
                _sceneRepaint--;
            }
        }
        private void PlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                EditorApplication.playModeStateChanged -= PlayModeStateChanged;
                Camera.onPreCull -= HandlePreCullRender;
            }
        }
#endif

        private PolylineRenderer _polylineRenderer;

        private PolylineRenderer Renderer
        {
            get
            {
                if (_polylineRenderer == null)
                {
                    _polylineRenderer = new PolylineRenderer(null, _renderSinglePass);
                }

                return _polylineRenderer;
            }
        }

        protected virtual void OnDisable()
        {
            if (_polylineRenderer != null)
            {
                _polylineRenderer.Cleanup();
                _polylineRenderer = null;
            }

            if (Application.isPlaying)
            {
                return;
            }
#if UNITY_EDITOR
            if (_root == this)
            {
                EditorApplication.playModeStateChanged -= PlayModeStateChanged;
                Camera.onPreCull -= HandlePreCullRender;
                _root = null;
            }
#endif
        }

        protected void ClearSegments()
        {
            _index = 0;
        }

        protected void RenderSegments()
        {
            Renderer.SetLines(_points, _colors, _index);
            Renderer.RenderLines();
        }

        protected virtual void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            RenderSegments();
            ClearSegments();
        }

        protected void AddSegment(Vector3 p0, Vector3 p1, float width, Color color0, Color color1)
        {
            if (!_addedSegmentSinceLastUpdate)
            {
                ClearSegments();
                _addedSegmentSinceLastUpdate = true;
            }

            while (_index + 2 > _points.Count)
            {
                _points.Add(new Vector4());
                _colors.Add(new Color());
            }

            _points[_index] = new Vector4(p0.x, p0.y, p0.z, width);
            _points[_index + 1] = new Vector4(p1.x, p1.y, p1.z, width);
            _colors[_index] = color0;
            _colors[_index + 1] = color1;

            _index += 2;
        }

        private static bool _renderSinglePass = true;
        /// <summary>
        /// Indicate whether using single pass rendering, can be updated within runtime dynamically.
        /// If using single pass rendering, duplicate buffer data for the <see cref="PolylineRenderer"/>
        /// </summary>
        public static bool RenderSinglePass
        {
            get
            {
                return _renderSinglePass;
            }

            set
            {
                if (_renderSinglePass == value)
                {
                    return;
                }
                _renderSinglePass = value;
                if (Root != null)
                {
                    Destroy(Root);
                }
            }
        }

        /// <summary>
        /// The color (of Unity <see cref="Color"/> type) used to draw the segments, by default to Black.
        /// Can be adjusted dynamically in runtime.
        /// </summary>
        public static Color Color = Color.black;
        /// <summary>
        /// Float indicating the width of the segments that's used for drawing the gizmos, by default to 0.1f.
        /// Can be adjusted dynamically in runtime.
        /// </summary>
        public static float LineWidth = 0.1f;

        internal struct ColorScope : IDisposable
        {
            private readonly Color _savedColor;
            public ColorScope(Color color)
            {
                _savedColor = Color;
                Color = color;
            }

            public void Dispose()
            {
                Color = _savedColor;
            }
        }

        /// <summary>
        /// Draw a point gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="p0">Vector3 indicating the position of the point</param>
        /// <param name="t">Optional <see cref="Transform"/> applied on top of the gizmo</param>
        public static void DrawPoint(Vector3 p0, Transform t = null)
        {
            if (t != null)
            {
                p0 = t.TransformPoint(p0);
            }

            Root.AddSegment(p0, p0, LineWidth, Color, Color);
        }

        /// <summary>
        /// Draw a line gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="p0">Vector3 indicating the starting point of the line</param>
        /// <param name="p1">Vector3 indicating the ending point of the line</param>
        /// <param name="t">Optional <see cref="Transform"/> applied on top of the gizmo</param>
        public static void DrawLine(Vector3 p0, Vector3 p1, Transform t = null)
        {
            if (t != null)
            {
                p0 = t.TransformPoint(p0);
                p1 = t.TransformPoint(p1);
            }

            Root.AddSegment(p0, p1, LineWidth, Color, Color);
        }

        /// <summary>
        /// Draw a wired cube gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="center">Vector3 indicating the center of the cube</param>
        /// <param name="size">Float indicating the size of the cube</param>
        /// <param name="t">Optional <see cref="Transform"/> applied on top of the gizmo</param>
        public static void DrawWireCube(Vector3 center, float size, Transform t = null)
        {
            for (int i = 0; i < CUBE_SEGMENTS.Count; i += 2)
            {
                Vector3 p0 = CUBE_POINTS[CUBE_SEGMENTS[i]] * size + center;
                Vector3 p1 = CUBE_POINTS[CUBE_SEGMENTS[i + 1]] * size + center;
                DrawLine(p0, p1, t);
            }
        }

        private static void DrawAxis(Vector3 position, Quaternion rotation, float size = 0.1f)
        {
            using var colorScope = new ColorScope(Color.black);
            Color = Color.red;
            DrawLine(position, position + rotation * Vector3.right * size);
            Color = Color.green;
            DrawLine(position, position + rotation * Vector3.up * size);
            Color = Color.blue;
            DrawLine(position, position + rotation * Vector3.forward * size);
        }

        /// <summary>
        /// Draw an Axis gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="pose"><see cref="Pose"/> indicating the position/rotation of the Axis</param>
        /// <param name="size">Float indicating the length of each Axis line</param>
        public static void DrawAxis(Pose pose, float size = 0.1f)
        {
            DrawAxis(pose.position, pose.rotation, size);
        }

        /// <summary>
        /// Draw an Axis gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="t"><see cref="Transform"/> indicating the position/rotation of the Axis</param>
        /// <param name="size">Float indicating the length of each Axis line</param>
        public static void DrawAxis(Transform t, float size = 0.1f)
        {
            DrawAxis(new Pose(t.position, t.rotation), size);
        }

        private static void DrawPlane(Vector3 position, Quaternion rotation, float width, float height)
        {
            DrawAxis(position, rotation);
            Matrix4x4 transformMat = Matrix4x4.TRS(position, rotation, Vector3.one);
            for (int i = 0; i < PLANE_SEGMENTS.Count; i += 2)
            {
                var localP0 = new Vector3(PLANE_POINTS[PLANE_SEGMENTS[i]].x * width, PLANE_POINTS[PLANE_SEGMENTS[i]].y * height, 0);
                var localP1 = new Vector3(PLANE_POINTS[PLANE_SEGMENTS[i + 1]].x * width, PLANE_POINTS[PLANE_SEGMENTS[i + 1]].y * height, 0);
                DrawLine(transformMat.MultiplyPoint3x4(localP0), transformMat.MultiplyPoint3x4(localP1));
            }
        }

        /// <summary>
        /// Draw a plane gizmo  using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="pose"><see cref="Pose"/> indicating the origin position and normal of the plane (the center point)</param>
        /// <param name="width">Float indicating the width of the plane</param>
        /// <param name="height">Float indicating the height of the plane</param>
        public static void DrawPlane(Pose pose, float width, float height)
        {
            DrawPlane(pose.position, pose.rotation, width, height);
        }

        private static void DrawBox(Vector3 position, Quaternion rotation, float width, float height, float depth, bool isPivotTopSurface)
        {
            DrawAxis(position, rotation);
            if (isPivotTopSurface)
            {
                // if the pivot is not center but top surface (like sceneVolume)
                var topSurfaceOffset = new Vector3(0, depth / 2f, 0);
                position -= topSurfaceOffset;
            }
            Matrix4x4 transformMat = Matrix4x4.TRS(position, rotation, Vector3.one);
            for (int i = 0; i < CUBE_SEGMENTS.Count; i += 2)
            {
                var localP0 = new Vector3(CUBE_POINTS[CUBE_SEGMENTS[i]].x * width, CUBE_POINTS[CUBE_SEGMENTS[i]].y * height, CUBE_POINTS[CUBE_SEGMENTS[i]].z * depth);
                var localP1 = new Vector3(CUBE_POINTS[CUBE_SEGMENTS[i + 1]].x * width, CUBE_POINTS[CUBE_SEGMENTS[i + 1]].y * height, CUBE_POINTS[CUBE_SEGMENTS[i + 1]].z * depth);
                DrawLine(transformMat.MultiplyPoint3x4(localP0), transformMat.MultiplyPoint3x4(localP1));
            }
        }

        /// <summary>
        /// Draw a box gizmo using <see cref="PolylineRenderer"/> segments
        /// </summary>
        /// <param name="pose"><see cref="Pose"/> indicating the origin position and normal of the box (pivot depending on <see cref="isPivotTopSurface"/>)</param>
        /// <param name="width">Float indicating the width of the box</param>
        /// <param name="height">Float indicating the height of the box</param>
        /// <param name="depth">Float indicating the depth of the box</param>
        /// <param name="isPivotTopSurface">Boolean specifying the pivot's <see cref="Pose"/> is from the middle center of the box or top surface's center,
        /// it's usually used as true for Scene anchors</param>
        public static void DrawBox(Pose pose, float width, float height, float depth, bool isPivotTopSurface = false)
        {
            DrawBox(pose.position, pose.rotation, width, height, depth, isPivotTopSurface);
        }

        private static readonly IReadOnlyList<Vector2> PLANE_POINTS = new List<Vector2>()
        {
            new(-0.5f, -0.5f),
            new(-0.5f, 0.5f),
            new(0.5f, -0.5f),
            new(0.5f, 0.5f),
        };

        private static readonly IReadOnlyList<int> PLANE_SEGMENTS = new List<int>()
        {
            0,
            1,
            1,
            3,
            3,
            2,
            2,
            0
        };

        private static readonly IReadOnlyList<Vector3> CUBE_POINTS = new List<Vector3>()
        {
            new(-0.5f, -0.5f, -0.5f),
            new(0.5f, -0.5f, -0.5f),
            new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f),
            new(0.5f, 0.5f, -0.5f),
            new(0.5f, -0.5f, 0.5f),
            new(-0.5f, 0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f)
        };

        private static readonly IReadOnlyList<int> CUBE_SEGMENTS = new List<int>()
        {
            0,
            1,
            1,
            5,
            3,
            5,
            0,
            3,
            0,
            2,
            1,
            4,
            3,
            6,
            5,
            7,
            2,
            4,
            4,
            7,
            7,
            6,
            6,
            2
        };
    }
}

