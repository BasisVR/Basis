/*
Copyright (c) 2025 Stress Level Zero
Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:
The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using UnityEngine;
using UnityEditor;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;

using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.XR.OpenXR.NativeTypes;

#if UNITY_EDITOR
using UnityEditor.XR.OpenXR.Features;
using UnityEditor.XR.OpenXR;
#endif

namespace UnityEngine.XR.OpenXR.Features
{
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Fix No Visible Area Mesh",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.WSA, BuildTargetGroup.Android },
        Company = "Stress Level Zero",
        Desc = "Fix no visible area mesh",
        OpenxrExtensionStrings = "XR_KHR_visibility_mask",
        Version = "0.0.1",
        FeatureId = featureId)]
#endif
    public class FixNoVisibleAreaMesh : OpenXRFeature
    {
        public const string featureId = "com.stresslevelzero.fixnovisiblemesh";

        bool gotSession = false;
        static ulong xrSession;
        bool gotInstance = false;
        static ulong xrInstance;
        static ulong refSpace;


        [StructLayout(LayoutKind.Sequential)]
        internal struct XrVisibilityMaskKHR
        {
            public uint type; // must be 1000031000
            public IntPtr next;
            public uint vertexCapacityInput;
            public uint vertexCountOutput;
            public IntPtr vertices;
            public uint indexCapacityInput;
            public uint indexCountOutput;
            public IntPtr indices;

            public XrVisibilityMaskKHR(bool dummy)
            {
                type = 1000031000;
                next = IntPtr.Zero;
                vertexCapacityInput = 0;
                vertexCountOutput = 0;
                vertices = IntPtr.Zero;
                indexCapacityInput = 0;
                indexCountOutput = 0;
                indices = IntPtr.Zero;
            }
        }

        internal struct XrView
        {
            public uint type;
            public IntPtr next;
            public XrPosef pose;
            public XrFovf fov;
            public XrView(bool dummy)
            {
                type = 7; //XR_TYPE_VIEW 
                next = IntPtr.Zero;
                pose = new XrPosef();
                fov = new XrFovf();
            }
        }

        internal struct XrViewLocateInfo
        {
            public uint type;
            public IntPtr next;
            public uint viewConfigurationType;
            public Int64 displayTime;
            public ulong space;

            public XrViewLocateInfo(bool dummy)
            {
                type = 6; // XR_TYPE_VIEW_LOCATE_INFO
                next = IntPtr.Zero;
                viewConfigurationType = (uint)XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                displayTime = 0;
                space = 0;
            }
        }

        internal struct XrReferenceSpaceCreateInfo
        {
            public uint type;
            public IntPtr next;
            public XrReferenceSpaceType referenceSpaceType;
            public XrPosef poseInReferenceSpace;

            public XrReferenceSpaceCreateInfo(bool dummy)
            {
                type = 37u; // XR_TYPE_REFERENCE_SPACE_CREATE_INFO 
                next = IntPtr.Zero;
                referenceSpaceType = XrReferenceSpaceType.View;
                poseInReferenceSpace =  new XrPosef(Vector3.zero, Quaternion.identity);
            }
        }

        internal struct XrViewState
        {
            public uint type;
            public IntPtr next;
            public XrViewStateFlags viewStateFlags;

            public XrViewState(bool dummy)
            {
                type = 11;//XR_TYPE_VIEW_STATE
                next = IntPtr.Zero;
                viewStateFlags = XrViewStateFlags.None;
            }
        }

        public enum XrViewConfigurationType : uint
        {
            XR_VIEW_CONFIGURATION_TYPE_PRIMARY_MONO = 1,
            XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO = 2,
            // Provided by XR_VARJO_quad_views
            XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO = 1000037000,
            // Provided by XR_MSFT_first_person_observer
            XR_VIEW_CONFIGURATION_TYPE_SECONDARY_MONO_FIRST_PERSON_OBSERVER_MSFT = 1000054000,
            XR_VIEW_CONFIGURATION_TYPE_MAX_ENUM = 0x7FFFFFFF
        }

        public enum XrVisibilityMaskTypeKHR : int
        {
            XR_VISIBILITY_MASK_TYPE_HIDDEN_TRIANGLE_MESH_KHR = 1,
            XR_VISIBILITY_MASK_TYPE_VISIBLE_TRIANGLE_MESH_KHR = 2,
            XR_VISIBILITY_MASK_TYPE_LINE_LOOP_KHR = 3,
            XR_VISIBILITY_MASK_TYPE_MAX_ENUM_KHR = 0x7FFFFFFF
        }

        internal delegate int Type_xrGetInstProcAddr(ulong instance, string name, out IntPtr function);
        static Type_xrGetInstProcAddr d_xrGetInstProcAddr;
        static Type_xrGetInstProcAddr d_OriginalGetInstanceProcAddr;

        internal delegate int Type_xrGetVisibilityMaskKHR(ulong session, UInt32 viewType, UInt32 viewIndex, UInt32 maskType, ref XrVisibilityMaskKHR visMaskPtr);
        static Type_xrGetVisibilityMaskKHR d_OriginalXrGetVisibilityMaskKHR;

        internal delegate int Type_xrLocateViews(ulong session, ref XrViewLocateInfo viewLocateInfo, ref XrViewState viewState, UInt32 viewCapacityInput, ref UInt32 viewCountOutput, ref XrView views);
        static Type_xrLocateViews d_xrLocateViews;

        internal delegate int Type_xrCreateReferenceSpace(ulong session, ref XrReferenceSpaceCreateInfo createInfo, ref ulong space);
        static Type_xrCreateReferenceSpace d_xrCreateReferenceSpace;

        static List<XrFovf> viewsArray;

        override protected IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            Setup();
            d_OriginalGetInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<Type_xrGetInstProcAddr>(func);
            return Marshal.GetFunctionPointerForDelegate((Type_xrGetInstProcAddr)HookXrGetInstProcAddr);
        }

        void Setup()
        {
            d_xrGetInstProcAddr = null;
            d_OriginalXrGetVisibilityMaskKHR = null;
            s_MissingVisMesh = false;
        }

        protected override bool OnInstanceCreate(ulong inputXrInstance)
        {
            Debug.Log("OpenXR Fix Visibility Mesh Feature: OnInstanceCreate");

            gotInstance = true;
            xrInstance = inputXrInstance;
            
            GetFunctionDelegates();
            return base.OnInstanceCreate(xrInstance);
        }



        private void GetFunctionDelegates()
        {
            d_xrGetInstProcAddr = (Type_xrGetInstProcAddr)Marshal.GetDelegateForFunctionPointer(OpenXRFeature.xrGetInstanceProcAddr, typeof(Type_xrGetInstProcAddr));

            IntPtr ptr_xrCreateReferenceSpace = IntPtr.Zero;
            int xrResult = d_xrGetInstProcAddr(xrInstance, "xrCreateReferenceSpace", out ptr_xrCreateReferenceSpace);
            if (xrResult != 0 || ptr_xrCreateReferenceSpace == IntPtr.Zero)
            {
                Debug.LogError("OpenXR Fix Visibility Mesh Feature: Failed to get xrCreateReferenceSpace function pointer. Error code: " + xrResult);
            }
            else
            {
                d_xrCreateReferenceSpace = Marshal.GetDelegateForFunctionPointer<Type_xrCreateReferenceSpace>(ptr_xrCreateReferenceSpace);
            }
            IntPtr ptr_xrLocateViews = IntPtr.Zero;
            xrResult = d_xrGetInstProcAddr(xrInstance, "xrLocateViews", out ptr_xrLocateViews);
            if (xrResult != 0 || ptr_xrLocateViews == IntPtr.Zero)
            {
                Debug.LogError("OpenXR Fix Visibility Mesh Feature: Failed to get xrLocateViews function pointer. Error code: " + xrResult);
            }
            else
            {
                d_xrLocateViews = Marshal.GetDelegateForFunctionPointer<Type_xrLocateViews>(ptr_xrLocateViews);
            }
        }
        protected override void OnSessionCreate(ulong inputXrSession)
        {
            xrSession = inputXrSession;
            CreateReferenceSpace();
        }
        private void CreateReferenceSpace()
        {
            XrReferenceSpaceCreateInfo spaceInfo = new XrReferenceSpaceCreateInfo(false);
            d_xrCreateReferenceSpace.Invoke(xrSession, ref spaceInfo, ref refSpace);
        }

        private static void GetFov()
        {
            XrViewLocateInfo viewLocateInfo = new XrViewLocateInfo(false);
            viewLocateInfo.space = refSpace;
            viewLocateInfo.viewConfigurationType = (uint) XrViewConfigurationType.XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
            viewLocateInfo.displayTime = 0x1;
            XrViewState viewState = new XrViewState(false);
            viewState.viewStateFlags = XrViewStateFlags.OrientationTracked | XrViewStateFlags.OrientationValid;
            Span<uint> viewCountOutput = new uint[1] { 0XFFFFFFFFu };
            Span<XrView> views0 = new XrView[2];
            int result = d_xrLocateViews.Invoke(xrSession, ref viewLocateInfo, ref viewState, 0, ref viewCountOutput[0], ref views0[0]);
            if (result != 0)
            {
                Debug.LogError($"xrLocateViews failed with error {(XrResult)result}");
            }

            uint viewCount = viewCountOutput[0];
            if (viewCount == 0 || viewCount == 0XFFFFFFFFu)
            {
                Debug.LogError($"Failed to get view count! view count is {viewCountOutput[0]}");
                return;
            }
            else
            {
                Debug.Log($"view count is {viewCountOutput[0]}");
            }
            Span<XrView> views = new XrView[viewCount];
            for (int i = 0; i < viewCount; i++)
            {
                views[i] = new XrView(false);
            }
            result = d_xrLocateViews.Invoke(xrSession, ref viewLocateInfo, ref viewState, viewCount, ref viewCountOutput[0], ref views[0]);
            if (result != 0)
            {
                Debug.LogError($"xrLocateViews failed with error {(XrResult)result}");
            }

            if (viewsArray == null) viewsArray = new List<XrFovf>((int)viewCount);
            viewsArray.Clear();
            for (int i = 0; i < viewCount; i++)
            {
                viewsArray.Add(views[i].fov);
            }
        }

        static int HookXrGetInstProcAddr(ulong instance, string name, out IntPtr function)
        {
            if (name == "xrGetVisibilityMaskKHR")
            {
                IntPtr originalVisMaskPtr = IntPtr.Zero;
                d_OriginalGetInstanceProcAddr?.Invoke(instance, "xrGetVisibilityMaskKHR", out originalVisMaskPtr);
                d_OriginalXrGetVisibilityMaskKHR = Marshal.GetDelegateForFunctionPointer<Type_xrGetVisibilityMaskKHR>(originalVisMaskPtr);

                function = Marshal.GetFunctionPointerForDelegate((Type_xrGetVisibilityMaskKHR)OverrideXrGetVisibilityMaskKHR);
                return 0;
            }
            else
            {
                return d_OriginalGetInstanceProcAddr.Invoke(instance, name, out function);
            }
        }


        static bool s_MissingVisMesh = false;
        const uint s_vertexCapacityInput = 4;
        const uint s_indexCapacityInput = 6;


        // 0 --- 2
        // |   / |
        // |  /  | 
        // | /   |
        // 1 --- 3
        static int[] idxBuffer = new int[6]
        {
            0, 1, 2,
            2, 1, 3
        };

        static float[] vtxBuffer = new float[8]
        {
            -1.0f,  1.0f,
            -1.0f, -1.0f,
             1.0f,  1.0f,
             1.0f, -1.0f
        };

        static int OverrideXrGetVisibilityMaskKHR(ulong session, uint viewType, UInt32 viewIndex, uint maskType, ref XrVisibilityMaskKHR visMaskPtr)
        {
            if (maskType != (uint)XrVisibilityMaskTypeKHR.XR_VISIBILITY_MASK_TYPE_VISIBLE_TRIANGLE_MESH_KHR)
            {
                if (d_OriginalXrGetVisibilityMaskKHR != null)
                {
                    return d_OriginalXrGetVisibilityMaskKHR.Invoke(session, viewType, viewIndex, maskType, ref visMaskPtr);
                }
                else
                {
                    Debug.LogError("Simulate No Vis Mesh: original XrGetVisibilityMaskKHR delegate was not assigned before the hooked version was called!");
                    return 1;
                }
            }

            if (visMaskPtr.indexCapacityInput == 0 || visMaskPtr.vertexCapacityInput == 0)
            {
                int result = d_OriginalXrGetVisibilityMaskKHR.Invoke(session, viewType, viewIndex, maskType, ref visMaskPtr);

                if (visMaskPtr.indexCountOutput == 0 || visMaskPtr.vertexCountOutput == 0)
                {
                    visMaskPtr.indexCountOutput = s_indexCapacityInput;
                    visMaskPtr.vertexCountOutput = s_vertexCapacityInput;
                    s_MissingVisMesh = true;
                }
                else
                {
                    s_MissingVisMesh = false;
                }
                return result;
            }

            if (s_MissingVisMesh && visMaskPtr.indexCapacityInput == s_indexCapacityInput && visMaskPtr.vertexCapacityInput == s_vertexCapacityInput)
            {
                float[] vtxBufferScaled = new float[8];
                vtxBuffer.CopyTo(vtxBufferScaled, 0);

                GetFov();

                if (viewsArray != null)
                {
                    for (int vIdx = 0; vIdx < 4; vIdx++)
                    {
                        int xIdx = 2 * vIdx;
                        int yIdx = 2 * vIdx + 1;
                        float angleX = vtxBufferScaled[xIdx] < 0 ? viewsArray[(int)viewIndex].AngleLeft : viewsArray[(int)viewIndex].AngleRight;
                        
                        vtxBufferScaled[xIdx] *= Mathf.Abs(Mathf.Tan(angleX));

                        float angleY = vtxBufferScaled[yIdx] < 0 ? viewsArray[(int)viewIndex].AngleDown : viewsArray[(int)viewIndex].AngleUp;
                        vtxBufferScaled[yIdx] *= Mathf.Abs(Mathf.Tan(angleY));
                        //Debug.Log($"Vtx {vIdx}: AngleX: {angleX}, AngleY: {angleY}, ({vtxBufferScaled[xIdx]}, {vtxBufferScaled[yIdx]}");
                    }
                }
                else
                {
                    Debug.LogError("viewsArray has not been populated!");
                }
                Marshal.Copy(idxBuffer, 0, visMaskPtr.indices, idxBuffer.Length);
                Marshal.Copy(vtxBufferScaled, 0, visMaskPtr.vertices, vtxBuffer.Length);
                return 0;
            }
            else
            {
                return d_OriginalXrGetVisibilityMaskKHR.Invoke(session, viewType, viewIndex, maskType, ref visMaskPtr);
            }

            return 0;
        }
    }
}