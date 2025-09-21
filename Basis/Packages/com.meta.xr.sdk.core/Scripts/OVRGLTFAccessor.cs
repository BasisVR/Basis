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

using System.Collections.Generic;
using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using OVRSimpleJSON;

public enum OVRGLTFType
{
    NONE,
    SCALAR,
    VEC2,
    VEC3,
    VEC4,
    MAT4,
}

public enum OVRGLTFComponentType
{
    NONE = 0,
    BYTE = 5120,
    UNSIGNED_BYTE = 5121,
    SHORT = 5122,
    UNSIGNED_SHORT = 5123,
    UNSIGNED_INT = 5125,
    FLOAT = 5126,
}

/// <summary>
/// Helper class used by OVRGLTFLoader <see cref="OVRGLTFLoader"> to load GLTF (GL Transmission Format) data buffers using appropriate accessors and translates them into corresponding Unity data types when applicable.
/// This class follows the GLTF 2.0 specification for loading data. For more information, refer to the [Binary Data Storage](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#binary-data-storage) section on the GLTF 2.0 specification site.
/// </summary>
/// <remarks>
/// This class is intended to be used in conjunction with the <see cref="OVRGLTFLoader">. It is not recommended to use these functions outside of the context of GLTF loading.
/// </remarks>

public class OVRGLTFAccessor : IDisposable
{
    private struct GLTFAccessor
    {
        public OVRGLTFType Type;

        public OVRGLTFComponentType ComponentType;
        public int ComponentTypeStride;
        public int BufferViewIndex;
        public int ByteOffset;
        public int Count;
        public JSONNode Min;
        public JSONNode Max;
    }

    private struct GLTFBufferView
    {
        public int BufferIndex;
        public int ByteOffset;
        public int ByteLength;
        public int ByteStride;
    }

    private struct GLTFBuffer
    {
        public int ByteLength;
    }

    private readonly List<GLTFAccessor> _accessors = new List<GLTFAccessor>();
    private readonly List<GLTFBufferView> _bufferViews = new List<GLTFBufferView>();
    private readonly List<GLTFBuffer> _buffers = new List<GLTFBuffer>();
    private readonly Stream _binaryChunk;
    private readonly int _binaryChunkLength;
    private readonly int _binaryChunkStart;
    private readonly BinaryReader _reader;

    private GLTFAccessor _activeGltfAccessor;
    private GLTFBufferView _activeBufferView;
    private GLTFBuffer _activeBuffer;
    private int _activeBufferOffset;
    private bool _requireStrideSeek;

    /// <summary>
    /// Tries to create a OVRGLTFAccessor object by checking if the binary stream data provided is a valid GLTF binary chunk.
    /// </summary>
    /// <param name="accessorsRoot">JSON node containing the GLTF accessors.</param>
    /// <param name="bufferViewsRoot">JSON node containing the GLTF buffer views.</param>
    /// <param name="buffersRoot">JSON node containing the GLTF buffers.</param>
    /// <param name="binaryChunk">Binary stream of the entire GLTF file.</param>
    /// <param name="dataAccessor">A valid OVRGLTFAccessor that can be used to access data in the GLTF binary stream.</param>
    /// <returns>If the OVRGLTFAccessor was created successfully.</returns>
    public static bool TryCreate(JSONNode accessorsRoot, JSONNode bufferViewsRoot, JSONNode buffersRoot, Stream binaryChunk, out OVRGLTFAccessor dataAccessor)
    {
        var reader = new BinaryReader(binaryChunk, Encoding.UTF8, true);
        var chunkLength = reader.ReadUInt32();
        var chunkType = reader.ReadUInt32();
        if (chunkType != (uint)OVRChunkType.BIN)
        {
            Debug.LogError("Read chunk does not match type.");
            dataAccessor = null;
            return false;
        }
        dataAccessor = new OVRGLTFAccessor(accessorsRoot, bufferViewsRoot, buffersRoot, reader, (int)binaryChunk.Position, (int)chunkLength);
        return true;
    }

    private OVRGLTFAccessor(JSONNode accessorsRoot, JSONNode bufferViewsRoot, JSONNode buffersRoot, BinaryReader binaryChunkReader, int binaryChinkStart, int binaryChunkLength)
    {
        _reader = binaryChunkReader;
        _binaryChunk = binaryChunkReader.BaseStream;
        _binaryChunkLength = binaryChunkLength;
        _binaryChunkStart = binaryChinkStart;
        foreach (var accessorNode in accessorsRoot.Children)
        {
            var accessor = new GLTFAccessor();
            foreach (var attribute in accessorNode)
            {
                switch (attribute.Key)
                {
                    case "bufferView":
                        accessor.BufferViewIndex = attribute.Value.AsInt;
                        break;
                    case "byteOffset":
                        accessor.ByteOffset = attribute.Value.AsInt;
                        break;
                    case "componentType":
                        accessor.ComponentType = (OVRGLTFComponentType)attribute.Value.AsInt;
                        accessor.ComponentTypeStride = GetStrideForType(accessor.ComponentType);
                        break;
                    case "count":
                        accessor.Count = attribute.Value.AsInt;
                        break;
                    case "type":
                        accessor.Type = ToOVRType(attribute.Value.Value);
                        break;
                    case "max":
                        accessor.Max = attribute.Value;
                        break;
                    case "min":
                        accessor.Min = attribute.Value;
                        break;
                    case "sparse":
                        Debug.LogWarning("Sparse accessors unsupported");
                        break;
                }
            }
            _accessors.Add(accessor);
        }

        foreach (var bufferViewNode in bufferViewsRoot.Children)
        {
            var bufferView = new GLTFBufferView();
            foreach (var attribute in bufferViewNode)
            {
                switch (attribute.Key)
                {
                    case "bufferIndex":
                        bufferView.BufferIndex = attribute.Value.AsInt;
                        break;
                    case "byteOffset":
                        bufferView.ByteOffset = attribute.Value.AsInt;
                        break;
                    case "byteLength":
                        bufferView.ByteLength = attribute.Value.AsInt;
                        break;
                    case "byteStride":
                        bufferView.ByteStride = attribute.Value.AsInt;
                        break;
                }
            }
            _bufferViews.Add(bufferView);
        }

        foreach (var bufferNode in buffersRoot.Children)
        {
            var buffer = new GLTFBuffer();
            foreach (var attribute in bufferNode)
            {
                switch (attribute.Key)
                {
                    case "byteLength":
                        buffer.ByteLength = attribute.Value.AsInt;
                        break;
                }
            }
            _buffers.Add(buffer);
        }
    }

    private static OVRGLTFType ToOVRType(string type)
    {
        switch (type)
        {
            case "SCALAR":
                return OVRGLTFType.SCALAR;
            case "VEC2":
                return OVRGLTFType.VEC2;
            case "VEC3":
                return OVRGLTFType.VEC3;
            case "VEC4":
                return OVRGLTFType.VEC4;
            case "MAT4":
                return OVRGLTFType.MAT4;
            default:
                Debug.LogError("Unsupported accessor type.");
                return OVRGLTFType.NONE;
        }
    }

    /// <summary>
    /// Indexes into the GLTF accessors JSONNode provided to the <see cref="TryCreate"/> method and moves the binary data stream position based on the accessor's data.
    /// </summary>
    /// <param name="accessorIndex">The index to a accessor in the accessor array.</param>
    /// <param name="onlyBufferView">If the position should be set to just the start of the buffer view.</param>
    public void Seek(int accessorIndex, bool onlyBufferView = false)
    {
        if (accessorIndex >= _accessors.Count)
        {
            return;
        }
        _activeGltfAccessor = _accessors[accessorIndex];
        _activeBufferView = _bufferViews[_activeGltfAccessor.BufferViewIndex];
        _activeBuffer = _buffers[_activeBufferView.BufferIndex];

        _requireStrideSeek = _activeBufferView.ByteStride != 0 && _activeBufferView.ByteStride != _activeGltfAccessor.ComponentTypeStride;

        if (_binaryChunkLength != _activeBuffer.ByteLength)
        {
            Debug.LogError("Chunk length is not equal to buffer length.");
            return;
        }

        _activeBufferOffset = _binaryChunkStart + _activeBufferView.ByteOffset;
        if (!onlyBufferView)
        {
            _activeBufferOffset += _activeGltfAccessor.ByteOffset;
        }
        _binaryChunk.Seek(_activeBufferOffset, SeekOrigin.Begin);
    }

    private void SeekStride(int strideIndex)
    {
        if (!_requireStrideSeek || strideIndex == 0)
        {
            return;
        }
        if (strideIndex >= _activeGltfAccessor.Count)
        {
            Debug.LogError("Invalid seek index for data");
            return;
        }
        var stride = _activeBufferView.ByteStride;
        _binaryChunk.Seek(_activeBufferOffset + (stride * strideIndex), SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads data as floats from the binary stream, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <returns>An array of floats.</returns>
    public float[] ReadFloat()
    {
        var res = new float[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (_activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
        }
        else
#endif
        {
            for (var i = 0; i < res.Length; i++)
            {
                res[i] = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
            }
        }
        return res;
    }

    /// <summary>
    /// Read data as integers from the binary stream, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <returns>An array of integers.</returns>
    public int[] ReadInt()
    {
        var res = new int[_activeGltfAccessor.Count];
        for (var i = 0; i < res.Length; i++)
        {
            res[i] = ReadAsInt(_reader, _activeGltfAccessor.ComponentType);
        }
        return res;
    }

    /// <summary>
    /// Read data as 2D vectors from the binary stream, using the current accessor set by <see cref="Seek"/>
    /// </summary>
    /// <returns>An array of Vector2s</returns>
    public Vector2[] ReadVector2()
    {
        var res = new Vector2[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
        }
        else
#endif
        {

            for (var i = 0; i < res.Length; i++)
            {
                SeekStride(i);
                res[i].x = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].y = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
            }
        }
        return res;
    }

    /// <summary>
    /// Read data from the binary stream as 3D vectors, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="conversionScale">Coversion scale that is applied to each Vector3.</param>
    /// <returns>An array of Vector3s</returns>
    public Vector3[] ReadVector3(Vector3 conversionScale)
    {
        var res = new Vector3[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
            for (var i = 0; i < res.Length; i++)
            {
                res[i].Scale(conversionScale);
            }
        }
        else
#endif
        {
            for (var i = 0; i < res.Length; i++)
            {
                SeekStride(i);
                res[i].x = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].y = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].z = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].Scale(conversionScale);
            }
        }
        return res;
    }

    /// <summary>
    /// Read data from the binary stream as 4D vectors, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="conversionScale">Coversion scale that is applied to each Vector4.</param>
    /// <returns>An array of Vector4s</returns>
    public Vector4[] ReadVector4(Vector4 conversionScale)
    {
        var res = new Vector4[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
            for (var i = 0; i < res.Length; i++)
            {
                res[i].Scale(conversionScale);
            }
        }
        else
#endif
        {
            for (var i = 0; i < res.Length; i++)
            {
                SeekStride(i);
                res[i].x = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].y = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].z = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].w = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                res[i].Scale(conversionScale);
            }
        }

        return res;
    }

    private static int ReadAsInt(BinaryReader reader, OVRGLTFComponentType type)
    {
        switch (type)
        {
            case OVRGLTFComponentType.NONE:
                return 0;
            case OVRGLTFComponentType.BYTE:
                return reader.ReadSByte();
            case OVRGLTFComponentType.UNSIGNED_BYTE:
                return reader.ReadByte();
            case OVRGLTFComponentType.SHORT:
                return reader.ReadInt16();
            case OVRGLTFComponentType.UNSIGNED_SHORT:
                return reader.ReadUInt16();
            case OVRGLTFComponentType.UNSIGNED_INT:
                return (int)reader.ReadUInt32();
            case OVRGLTFComponentType.FLOAT:
                return (int)reader.ReadSingle();
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
    private static float ReadAsFloat(BinaryReader reader, OVRGLTFComponentType type)
    {
        switch (type)
        {
            case OVRGLTFComponentType.NONE:
                return 0;
            case OVRGLTFComponentType.BYTE:
                return reader.ReadSByte();
            case OVRGLTFComponentType.UNSIGNED_BYTE:
                return reader.ReadByte();
            case OVRGLTFComponentType.SHORT:
                return reader.ReadInt16();
            case OVRGLTFComponentType.UNSIGNED_SHORT:
                return reader.ReadUInt16();
            case OVRGLTFComponentType.UNSIGNED_INT:
                return reader.ReadUInt32();
            case OVRGLTFComponentType.FLOAT:
                return reader.ReadSingle();
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    /// <summary>
    /// Read data from the binary stream as colors, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <returns>An array of Colors.</returns>
    public Color[] ReadColor()
    {
        if (_activeGltfAccessor.Type != OVRGLTFType.VEC4 && _activeGltfAccessor.Type != OVRGLTFType.VEC3)
        {
            Debug.LogError("Tried to read non-color type as a color array." + _activeGltfAccessor.Type);
            return Array.Empty<Color>();
        }
        Color[] colors = new Color[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT && _activeGltfAccessor.Type == OVRGLTFType.VEC4)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(colors.AsSpan()));
        }
        else
#endif
        {
            for (var i = 0; i < colors.Length; i++)
            {
                SeekStride(i);
                if (_activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
                {
                    colors[i].r = _reader.ReadSingle();
                    colors[i].g = _reader.ReadSingle();
                    colors[i].b = _reader.ReadSingle();
                    colors[i].a = (_activeGltfAccessor.Type == OVRGLTFType.VEC4) ? _reader.ReadSingle() : 1.0f;
                }
                else
                {
                    float maxValue = GetMaxValueForType(_activeGltfAccessor.ComponentType);
                    colors[i].r = ReadAsInt(_reader, _activeGltfAccessor.ComponentType) / maxValue;
                    colors[i].g = ReadAsInt(_reader, _activeGltfAccessor.ComponentType) / maxValue;
                    colors[i].b = ReadAsInt(_reader, _activeGltfAccessor.ComponentType) / maxValue;
                    colors[i].a = (_activeGltfAccessor.Type == OVRGLTFType.VEC4)
                        ? ReadAsInt(_reader, _activeGltfAccessor.ComponentType) / maxValue
                        : 1.0f;
                }
            }
        }

        return colors;
    }

    /// <summary>
    /// Read data from the binary stream as bone weights, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="resultsBoneWeights">Output array of BoneWeights.</param>
    public void ReadWeights(ref BoneWeight[] resultsBoneWeights)
    {
        if (_activeGltfAccessor.Type != OVRGLTFType.VEC4)
        {
            Debug.LogError("Tried to read bone weights data as a non-vec4 array.");
            return;
        }
        resultsBoneWeights ??= new BoneWeight[_activeGltfAccessor.Count];

        for (int i = 0; i < resultsBoneWeights.Length; i++)
        {
            SeekStride(i);
            resultsBoneWeights[i].weight0 = _reader.ReadSingle();
            resultsBoneWeights[i].weight1 = _reader.ReadSingle();
            resultsBoneWeights[i].weight2 = _reader.ReadSingle();
            resultsBoneWeights[i].weight3 = _reader.ReadSingle();

            float weightSum = resultsBoneWeights[i].weight0 + resultsBoneWeights[i].weight1 + resultsBoneWeights[i].weight2 + resultsBoneWeights[i].weight3;
            if (!Mathf.Approximately(weightSum, 0))
            {
                resultsBoneWeights[i].weight0 /= weightSum;
                resultsBoneWeights[i].weight1 /= weightSum;
                resultsBoneWeights[i].weight2 /= weightSum;
                resultsBoneWeights[i].weight3 /= weightSum;
            }
        }
    }

    /// <summary>
    /// Read data from the binary stream as bone indices, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="resultsBoneWeights">Output array of BoneWeights.</param>
    public void ReadJoints(ref BoneWeight[] resultsBoneWeights)
    {
        if (_activeGltfAccessor.Type != OVRGLTFType.VEC4)
        {
            Debug.LogError("Tried to read bone weights data as a non-vec4 array.");
            return;
        }
        resultsBoneWeights ??= new BoneWeight[_activeGltfAccessor.Count];
        for (int i = 0; i < resultsBoneWeights.Length; i++)
        {
            SeekStride(i);
            resultsBoneWeights[i].boneIndex0 = ReadAsInt(_reader, _activeGltfAccessor.ComponentType);
            resultsBoneWeights[i].boneIndex1 = ReadAsInt(_reader, _activeGltfAccessor.ComponentType);
            resultsBoneWeights[i].boneIndex2 = ReadAsInt(_reader, _activeGltfAccessor.ComponentType);
            resultsBoneWeights[i].boneIndex3 = ReadAsInt(_reader, _activeGltfAccessor.ComponentType);
        }
    }

    /// <summary>
    /// Read data from the binary stream as Quaternions, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="gltfToUnitySpaceRotation">The conversion value to apply to translate from GLTF space to Unity space.</param>
    /// <returns>An array of Quaternions.</returns>
    public Quaternion[] ReadQuaterion(Vector4 gltfToUnitySpaceRotation)
    {
        if (_activeGltfAccessor.Type != OVRGLTFType.VEC4)
        {
            Debug.LogError("Tried to read bone weights data as a non-vec4 array.");
            return Array.Empty<Quaternion>();
        }

        var res = new Quaternion[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
            for (var i = 0; i < res.Length; i++)
            {
                res[i].x *= gltfToUnitySpaceRotation.x;
                res[i].y *= gltfToUnitySpaceRotation.y;
                res[i].z *= gltfToUnitySpaceRotation.z;
                res[i].w *= gltfToUnitySpaceRotation.w;
            }
        }
        else
#endif
        {
            for (var i = 0; i < res.Length; i++)
            {
                SeekStride(i);
                res[i].x = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType) * gltfToUnitySpaceRotation.x;
                res[i].y = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType) * gltfToUnitySpaceRotation.y;
                res[i].z = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType) * gltfToUnitySpaceRotation.z;
                res[i].w = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType) * gltfToUnitySpaceRotation.w;
            }
        }
        return res;
    }

    /// <summary>
    /// Read data from the binary stream as a Matrix 4x4, using the current accessor set by <see cref="Seek"/>.
    /// </summary>
    /// <param name="conversionScale">Conversion scale to be applied to each 4x4 matrix.</param>
    /// <returns>An array of Matrix4x4.</returns>
    public Matrix4x4[] ReadMatrix4x4(Vector3 conversionScale)
    {
        if (_activeGltfAccessor.Type != OVRGLTFType.MAT4)
        {
            Debug.LogError("Tried to read non-vec3 data as a vec3 array.");
            return Array.Empty<Matrix4x4>();
        }

        Matrix4x4 scale = Matrix4x4.Scale(conversionScale);
        var res = new Matrix4x4[_activeGltfAccessor.Count];
#if UNITY_2021_3_OR_NEWER
        if (!_requireStrideSeek && _activeGltfAccessor.ComponentType == OVRGLTFComponentType.FLOAT)
        {
            _binaryChunk.Read(MemoryMarshal.AsBytes(res.AsSpan()));
            for (var i = 0; i < _activeGltfAccessor.Count; i++)
            {
                res[i] = scale * res[i] * scale;
            }
        }
        else
#endif
        {
            for (var i = 0; i < _activeGltfAccessor.Count; i++)
            {
                SeekStride(i);
                for (var m = 0; m < 16; m++)
                {
                    res[i][m] = ReadAsFloat(_reader, _activeGltfAccessor.ComponentType);
                }
                res[i] = scale * res[i] * scale;
            }
        }
        return res;
    }

    private int GetStrideForType(OVRGLTFComponentType type)
    {
        switch (type)
        {
            case OVRGLTFComponentType.BYTE:
                return sizeof(sbyte);
            case OVRGLTFComponentType.UNSIGNED_BYTE:
                return sizeof(byte);
            case OVRGLTFComponentType.SHORT:
                return sizeof(short);
            case OVRGLTFComponentType.UNSIGNED_SHORT:
                return sizeof(ushort);
            case OVRGLTFComponentType.UNSIGNED_INT:
                return sizeof(uint);
            case OVRGLTFComponentType.FLOAT:
                return sizeof(float);
            default:
                Debug.LogWarning("GetStrideForType called with unsupported component type " + type);
                return 0;
        }
    }

    private float GetMaxValueForType(OVRGLTFComponentType type)
    {
        switch (type)
        {
            case OVRGLTFComponentType.BYTE:
                return sbyte.MaxValue;
            case OVRGLTFComponentType.UNSIGNED_BYTE:
                return byte.MaxValue;
            case OVRGLTFComponentType.SHORT:
                return short.MaxValue;
            case OVRGLTFComponentType.UNSIGNED_SHORT:
                return ushort.MaxValue;
            case OVRGLTFComponentType.UNSIGNED_INT:
                return uint.MaxValue;
            case OVRGLTFComponentType.FLOAT:
                return float.MaxValue;
            default:
                Debug.LogWarning("GetMaxValueForType called with unsupported component type " + type);
                return 1;
        }
    }

    /// <summary>
    /// Read the binary stream using a specific buffer view.
    /// </summary>
    /// <param name="bufferViewIndex">Index of a buffer view to use out of all buffer views.</param>
    /// <returns>An array of byte data the buffer view points to.</returns>
    public byte[] ReadBuffer(int bufferViewIndex)
    {
        _activeBufferView = _bufferViews[bufferViewIndex];
        _activeBuffer = _buffers[_activeBufferView.BufferIndex];
        _binaryChunk.Seek(_binaryChunkStart, SeekOrigin.Begin);
        _binaryChunk.Seek(_activeBufferView.ByteOffset, SeekOrigin.Current);
        return _reader.ReadBytes(_activeBufferView.ByteLength);
    }

    /// <summary>
    /// Disposes the BinaryReader if being used for accessing the Stream.
    /// </summary>
    public void Dispose()
    {
        _reader.Dispose();
    }

    /// <summary>
    /// Gets the total number of GLTF accessors loaded from the GLTF file.
    /// </summary>
    /// <returns>Total number of GLTF accessors.</returns>
    public int GetDataCount()
    {
        return _activeGltfAccessor.Count;
    }
}
