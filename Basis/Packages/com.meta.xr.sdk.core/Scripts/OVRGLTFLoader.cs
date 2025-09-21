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

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using UnityEngine;
using OVRSimpleJSON;
using System.Threading.Tasks;

public enum OVRChunkType
{
    JSON = 0x4E4F534A,
    BIN = 0x004E4942,
}

public enum OVRTextureFormat
{
    NONE,
    KTX2,
    PNG,
    JPEG,
}

/// <summary>
/// This enum represents a simplified representation on how Texture Filter quality is implemented in Unity.
/// The values set in this enum are NOT random and are directly used by ApplyTextureQuality() and DetectTextureQuality()
/// to get/set the correspondent setup in Unity.
/// </summary>
public enum OVRTextureQualityFiltering
{
    None = -1,
    Bilinear = 0,
    Trilinear = 1,
    Aniso2x = 2,
    Aniso4x = 3,
    Aniso8x = 4,
    Aniso16x = 5,
}

/// <summary>
/// Struct that contains mesh data loaded from a GLTF (GL Transmission Format file). This includes the mesh object, material of the mesh, mesh attributes, and the morph targets of the mesh.
/// </summary>
public struct OVRMeshData
{
    public Mesh mesh;
    public Material material;
    public OVRMeshAttributes baseAttributes;
    public OVRMeshAttributes[] morphTargets;
}

/// <summary>
/// Struct that contains material data from a GLTF (GL Transmission Format file) required for creating a Unity material. This includes the shader, texture data, and base color.
/// </summary>
public struct OVRMaterialData
{
    public Shader shader;
    public int textureId;
    public OVRTextureData texture;
    public Color baseColorFactor;
}

/// <summary>
/// Struct that contains the overall GLTF (GL Transmission Format) scene structure. This includes the root of the scene as a game object as well as all the animation and morph target data for the scene.
/// </summary>
public struct OVRGLTFScene
{
    public GameObject root;
    public Dictionary<OVRGLTFInputNode, OVRGLTFAnimatinonNode> animationNodes;
    public Dictionary<int, OVRGLTFAnimatinonNode[]> animationNodeLookup;
    public List<OVRGLTFAnimationNodeMorphTargetHandler> morphTargetHandlers;
}

/// <summary>
/// Struct that contains data on a texture loaded from a GLTF (GL Transmission Format file). This includes the raw data of the texture as well as metadata like width, height, format, and uri.
/// </summary>
public struct OVRTextureData
{
    public byte[] data;
    public int width;
    public int height;
    public OVRTextureFormat format;
    public TextureFormat transcodedFormat;
    public string uri;
}

/// <summary>
/// Struct that contains the mesh attribute data for each mesh loaded from a GLTF (GL Transmission Format file). This includes the verts, normals, tangets, UVs, colors, and bone weights.
/// </summary>
public struct OVRMeshAttributes
{
    public Vector3[] vertices;
    public Vector3[] normals;
    public Vector4[] tangents;
    public Vector2[] texcoords;
    public Color[] colors;
    public BoneWeight[] boneWeights;
}

/// <summary>
/// This is a lightweight [GLTF model](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html) loader that is guaranteed to work with models loaded from the Meta Quest runtime
/// using OVRPlugin.LoadRenderModel <see cref="OVRPlugin.LoadRenderModel"/>. The loader only accepts GLTF files/data in the [GLB format](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#glb-file-format-specification).
/// We do not recommended using this class as a general purpose GLTF loader.
/// </summary>
public class OVRGLTFLoader
{
    private const float LoadingMaxTimePerFrame = 1.0f / 70f;

    private readonly Func<Stream> m_deferredStream;
    private JSONNode m_jsonData;
    private Stream m_glbStream;

    private GameObject[] m_Nodes;

    private Dictionary<OVRGLTFInputNode, OVRGLTFAnimatinonNode> m_InputAnimationNodes;

    // <animationIndex, OVRGLTFAnimatinonNode[]>
    private Dictionary<int, OVRGLTFAnimatinonNode[]> m_AnimationLookup;

    // <nodeIndex, OVRGLTFAnimatinonNodeMorphTargetHandler>
    private Dictionary<int, OVRGLTFAnimationNodeMorphTargetHandler> m_morphTargetHandlers;

    private Shader m_Shader = Shader.Find("Legacy Shaders/Diffuse");
    private Shader m_AlphaBlendShader = Shader.Find("Unlit/Transparent");
    private OVRTextureQualityFiltering m_TextureQuality = OVRTextureQualityFiltering.Bilinear; // = Unity default
    private float m_TextureMipmapBias = 0.0f; // = shader default

    public OVRGLTFScene scene;

    public static readonly Vector3 GLTFToUnitySpace = new Vector3(-1, 1, 1);
    public static readonly Vector3 GLTFToUnityTangent = new Vector4(-1, 1, 1, -1);
    public static readonly Vector4 GLTFToUnitySpace_Rotation = new Vector4(1, -1, -1, 1);

    private static Dictionary<string, OVRGLTFInputNode> InputNodeNameMap = new Dictionary<string, OVRGLTFInputNode>
    {
        { "button_a", OVRGLTFInputNode.Button_A_X },
        { "button_x", OVRGLTFInputNode.Button_A_X },
        { "button_b", OVRGLTFInputNode.Button_B_Y },
        { "button_y", OVRGLTFInputNode.Button_B_Y },
        { "button_oculus", OVRGLTFInputNode.Button_Oculus_Menu },
        { "trigger_front", OVRGLTFInputNode.Trigger_Front },
        { "trigger_grip", OVRGLTFInputNode.Trigger_Grip },
        { "thumbstick", OVRGLTFInputNode.ThumbStick },
    };

    public Func<string, Material, Texture2D> textureUriHandler;
    private Dictionary<int, Texture2D> m_textures;
    private Dictionary<int, Material> m_materials;
    private float m_processingNodesStart;
    private OVRGLTFAccessor _dataAccessor;

    /// <summary>
    /// Creates a new GLTF loader using a file path.
    /// </summary>
    /// <param name="fileName">Path to GLB file</param>
    public OVRGLTFLoader(string fileName)
    {
        m_glbStream = File.Open(fileName, FileMode.Open);
    }

    /// <summary>
    /// Creates a new GLTF loader using a binary data array.
    /// </summary>
    /// <param name="data">GLB byte data</param>
    public OVRGLTFLoader(byte[] data)
    {
        m_glbStream = new MemoryStream(data, 0, data.Length, false, true);
    }

    /// <summary>
    /// Creates a new GLTF loader with a deferred data stream. This should be used with <see cref="OVRGLTFLoader.LoadGLBCoroutine"/> to allow for async loading of the GLTF model. This can help prevent stalling on complex GLTF models.
    /// </summary>
    /// <param name="deferredStream">GLB data as a Stream</param>
    public OVRGLTFLoader(Func<Stream> deferredStream)
    {
        m_deferredStream = deferredStream;
    }

    /// <summary>
    /// Starts the loading process for the full GLTF file into Unity.
    /// </summary>
    /// <param name="supportAnimation">Load GLTF animations.</param>
    /// <param name="loadMips">Load GLTF textures with mip maps.</param>
    /// <returns>Full GLTF scene</returns>
    public OVRGLTFScene LoadGLB(bool supportAnimation, bool loadMips = true)
    {
        var loadGltfCoroutine = LoadGLBCoroutine(supportAnimation, loadMips);
        while (loadGltfCoroutine.MoveNext())
        {
            // process the coroutine synchronously
        }
        return scene;
    }

    /// <summary>
    /// Starts the loading process for the full GLTF file into Unity as a coroutine. This can help prevent stalling as loading will be spread out over multiple frames.
    /// </summary>
    /// <param name="supportAnimation">Load GLTF animations</param>
    /// <param name="loadMips">Load GLTF textures with mip maps.</param>
    /// <returns>IEnumerator that can be yielded on.</returns>
    public IEnumerator LoadGLBCoroutine(bool supportAnimation, bool loadMips = true)
    {
        scene = new OVRGLTFScene();
        m_InputAnimationNodes = new Dictionary<OVRGLTFInputNode, OVRGLTFAnimatinonNode>();
        m_AnimationLookup = new Dictionary<int, OVRGLTFAnimatinonNode[]>();
        m_morphTargetHandlers = new Dictionary<int, OVRGLTFAnimationNodeMorphTargetHandler>();
        m_textures = new Dictionary<int, Texture2D>();
        m_materials = new Dictionary<int, Material>();

        // If running in the unity editor avoid a background task
        if (Application.isBatchMode)
        {
            Debug.Log("Batch Mode Single Threaded Loading");
            m_jsonData = InitializeGLBLoad();
        }
        else
        {
            var task = Task.Run<JSONNode>(() => InitializeGLBLoad());
            yield return new WaitUntil(() => task.IsCompleted);
            m_jsonData = task.Result;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
            }
        }
        if (m_jsonData == null || !OVRGLTFAccessor.TryCreate(m_jsonData["accessors"], m_jsonData["bufferViews"], m_jsonData["buffers"],
                m_glbStream, out _dataAccessor))
        {
            m_glbStream?.Close();
            yield break;
        }

        var loadGltf = LoadGLTF(supportAnimation, loadMips);
        // Run coroutine withut initial frame skip
        while (loadGltf.MoveNext())
        {
            yield return loadGltf.Current;
        }

        m_glbStream.Close();

        if (!m_Nodes.Any())
        {
            yield break;
        }

        // Rotate to match unity coordinates
        scene.root.transform.Rotate(Vector3.up, 180.0f);
        scene.root.SetActive(true);
        scene.animationNodes = m_InputAnimationNodes;
        scene.animationNodeLookup = m_AnimationLookup;
        scene.morphTargetHandlers = m_morphTargetHandlers.Values.ToList();
    }

    private JSONNode InitializeGLBLoad()
    {
        if (m_deferredStream != null)
        {
            m_glbStream = m_deferredStream.Invoke();
        }
        if (ValidateGLB(m_glbStream))
        {
            byte[] jsonChunkData = ReadChunk(m_glbStream, OVRChunkType.JSON);
            if (jsonChunkData != null)
            {
                string json = System.Text.Encoding.ASCII.GetString(jsonChunkData);
                return JSON.Parse(json);
            }
        }
        return null;
    }

    /// <summary>
    /// Sets the shader that should be used by the GLTF loader on materials.
    /// </summary>
    /// <param name="shader">Shader to be used.</param>
    public void SetModelShader(Shader shader)
    {
        m_Shader = shader;
    }

    /// <summary>
    /// Sets the alpha blending shader that should be used by the GLTF loader on materials.
    /// </summary>
    /// <param name="shader">Shader to be used.</param>
    public void SetModelAlphaBlendShader(Shader shader)
    {
        m_AlphaBlendShader = shader;
    }

    /// <summary>
    /// Sets the texture quality for loading all textures in the GLTF model. The default is Bilinear.
    /// Once loaded, textures will be read-only on GPU memory.
    /// </summary>
    /// <param name="loadedTexturesQuality">The quality setting.</param>
    public void SetTextureQualityFiltering(OVRTextureQualityFiltering loadedTexturesQuality)
    {
        m_TextureQuality = loadedTexturesQuality;
    }

    /// <summary>
    /// Sets the MipMap bias value for loading all textures in the GLTF model. The default is 0.
    /// Only supported when MipMaps are loaded and the provided shader has a property named "_MainTexMMBias"
    /// </summary>
    /// <param name="loadedTexturesMipmapBiasing">The value for bias. Value is clamped between [-1,1]</param>
    public void SetMipMapBias(float loadedTexturesMipmapBiasing)
    {
        m_TextureMipmapBias = Mathf.Clamp(loadedTexturesMipmapBiasing, -1.0f, 1.0f);
    }

    /// <summary>
    /// Decodes the Texture Quality setting from the input Texture2D properties' values.
    /// </summary>
    /// <param name="srcTexture">The input Texture2D</param>
    /// <returns>The enum TextureQualityFiltering representing the quality.</returns>
    public static OVRTextureQualityFiltering DetectTextureQuality(in Texture2D srcTexture)
    {
        OVRTextureQualityFiltering quality = OVRTextureQualityFiltering.None;
        switch (srcTexture.filterMode)
        {
            case FilterMode.Point:
                quality = OVRTextureQualityFiltering.None;
                break;
            case FilterMode.Bilinear:
                goto default;
            case FilterMode.Trilinear:
                if (srcTexture.anisoLevel <= 1)
                    quality = OVRTextureQualityFiltering.Trilinear;
                // In theory, aniso supports values between 2-16x, but in reality GPUs and gfx APIs implement
                // powers of 2 (values in between have no change)
                else if (srcTexture.anisoLevel < 4)
                    quality = OVRTextureQualityFiltering.Aniso2x;
                else if (srcTexture.anisoLevel < 8)
                    quality = OVRTextureQualityFiltering.Aniso4x;
                else if (srcTexture.anisoLevel < 16)
                    quality = OVRTextureQualityFiltering.Aniso8x;
                else
                    quality = OVRTextureQualityFiltering.Aniso16x;
                break;
            default:
                quality = OVRTextureQualityFiltering.Bilinear;
                break;
        }

        return quality;
    }

    /// <summary>
    /// Applies the input Texture Quality setting into the ref Texture2D provided as input. Texture2D must not be readonly.
    /// </summary>
    /// <param name="qualityLevel">The quality level to apply</param>
    /// <param name="destTexture">The destination Texture2D to apply quality setting to</param>
    public static void ApplyTextureQuality(OVRTextureQualityFiltering qualityLevel, ref Texture2D destTexture)
    {
        if (destTexture == null)
            return;

        switch (qualityLevel)
        {
            case OVRTextureQualityFiltering.None:
                destTexture.filterMode = FilterMode.Point;
                destTexture.anisoLevel = 0;
                break;
            case OVRTextureQualityFiltering.Bilinear:
                destTexture.filterMode = FilterMode.Bilinear;
                destTexture.anisoLevel = 0;
                break;
            case OVRTextureQualityFiltering.Trilinear:
                destTexture.filterMode = FilterMode.Trilinear;
                destTexture.anisoLevel = 0;
                break;
            default: // for higher values
                destTexture.filterMode = FilterMode.Trilinear;
                // In theory, aniso supports values between 2-16x, but in reality GPUs and gfx APIs implement
                // powers of 2 (values in between have no change)
                // given the enum value, this gives aniso x2 x4 x8 x16
                destTexture.anisoLevel = Mathf.FloorToInt(Mathf.Pow(2.0f, (int)qualityLevel - 1));
                break;
        }
    }

    /// <summary>
    /// Checks the GLB data stream to check if it is a valid GLTF file.
    /// </summary>
    /// <param name="glbStream">Data stream of the GLB file.</param>
    /// <returns>If the stream is a valid GLTF file.</returns>
    static public bool ValidateGLB(Stream glbStream)
    {
        if (glbStream == null)
        {
            return false;
        }
        // Read the magic entry and ensure value matches the glTF value
        int uint32Size = sizeof(uint);
        byte[] buffer = new byte[uint32Size];
        glbStream.Read(buffer, 0, uint32Size);
        uint magic = BitConverter.ToUInt32(buffer, 0);

        if (magic != 0x46546C67)
        {
            Debug.LogError("Data stream was not a valid glTF format");
            return false;
        }

        // Read glTF version
        glbStream.Read(buffer, 0, uint32Size);
        uint version = BitConverter.ToUInt32(buffer, 0);

        if (version != 2)
        {
            Debug.LogError("Only glTF 2.0 is supported");
            return false;
        }

        // Read glTF file size
        glbStream.Read(buffer, 0, uint32Size);
        uint length = BitConverter.ToUInt32(buffer, 0);
        if (length != glbStream.Length)
        {
            Debug.LogError("glTF header length does not match file length");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a chunk of binary data from the stream that either represents the JSON or BIN part of the GLTF file.
    /// </summary>
    /// <param name="glbStream">The binary stream of the GLTF file.</param>
    /// <param name="type">Type indicating which part of the GLTF file should be read.</param>
    /// <returns></returns>
    public static byte[] ReadChunk(Stream glbStream, OVRChunkType type)
    {
        uint chunkLength;
        if (ValidateChunk(glbStream, type, out chunkLength))
        {
            byte[] chunkBuffer = new byte[chunkLength];
            glbStream.Read(chunkBuffer, 0, (int)chunkLength);
            return chunkBuffer;
        }

        return null;
    }

    private static bool ValidateChunk(Stream glbStream, OVRChunkType type, out uint chunkLength)
    {
        int uint32Size = sizeof(uint);
        byte[] buffer = new byte[uint32Size];
        glbStream.Read(buffer, 0, uint32Size);
        chunkLength = BitConverter.ToUInt32(buffer, 0);

        glbStream.Read(buffer, 0, uint32Size);
        uint chunkType = BitConverter.ToUInt32(buffer, 0);

        if (chunkType != (uint)type)
        {
            Debug.LogError("Read chunk does not match type.");
            return false;
        }

        return true;
    }

    private IEnumerator LoadGLTF(bool supportAnimation, bool loadMips)
    {
        if (m_jsonData == null)
        {
            Debug.LogError("m_jsonData was null");
            yield break;
        }

        var scenes = m_jsonData["scenes"];
        if (scenes.Count == 0)
        {
            Debug.LogError("No valid scenes in this glTF.");
            yield break;
        }

        // Create GameObjects for each node in the model so that they can be referenced during processing
        scene.root = new GameObject("GLB Scene Root");
        var sceneRootTransform = scene.root.transform;
        scene.root.SetActive(false);

        var nodes = m_jsonData["nodes"].AsArray;
        m_Nodes = new GameObject[nodes.Count];
        sceneRootTransform.hierarchyCapacity = nodes.Count;
        var i = 0;
        foreach (var node in nodes.Values)
        {
            var go = new GameObject();
            go.transform.SetParent(sceneRootTransform, false);
            m_Nodes[i++] = go;
        }

        // Limit loading to just the first scene in the glTF
        var mainScene = scenes[0];
        var rootNodes = mainScene["nodes"].AsArray;
        m_processingNodesStart = Time.realtimeSinceStartup;
        // Load all nodes (some models like e.g. laptops use multiple nodes)
        foreach (JSONNode rootNode in rootNodes)
        {
            int rootNodeId = rootNode.AsInt;
            var processNode = ProcessNode(nodes, rootNodeId, loadMips, sceneRootTransform);
            // Run coroutine without initial frame skip
            while (processNode.MoveNext())
            {
                yield return processNode.Current;
            }
        }

        if (supportAnimation)
        {
            var processAnimations = ProcessAnimations();
            // Run coroutine without initial frame skip
            while (processAnimations.MoveNext())
            {
                yield return processAnimations.Current;
            }
        }
    }

    private IEnumerator ProcessNode(JSONArray nodes, int nodeId, bool loadMips, Transform parent)
    {
        bool hasSkipped = false;
        if (Time.realtimeSinceStartup - m_processingNodesStart > LoadingMaxTimePerFrame)
        {
            m_processingNodesStart = Time.realtimeSinceStartup;
            hasSkipped = true;
            yield return null;
        }

        JSONNode node = nodes[nodeId];

        var nodeGameObject = m_Nodes[nodeId];
        var nodeTransform = nodeGameObject.transform;
        var nodeName = node["name"].Value;
        nodeTransform.name = nodeName;
        nodeTransform.SetParent(parent, false);

        // Process the child nodes first
        var childNodes = node["children"].AsArray;
        if (childNodes.Count > 0)
        {
            foreach (var child in childNodes.Values)
            {
                var childId = child.AsInt;
                var processNode = ProcessNode(nodes, childId, loadMips, nodeTransform);
                // Run coroutine without initial frame skip
                while (processNode.MoveNext())
                {
                    yield return processNode.Current;
                }
            }
        }

        if (nodeName.StartsWith("batteryIndicator"))
        {
            nodeGameObject.SetActive(false);
            yield break;
        }

        if (node["mesh"] != null)
        {
            var meshId = node["mesh"].AsInt;
            OVRMeshData meshData = ProcessMesh(m_jsonData["meshes"][meshId], loadMips);

            if (node["skin"] != null)
            {
                var renderer = nodeGameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = meshData.mesh;
                renderer.sharedMaterial = meshData.material;

                var skinId = node["skin"].AsInt;
                ProcessSkin(m_jsonData["skins"][skinId], renderer);
            }
            else
            {
                var filter = nodeGameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = meshData.mesh;
                var renderer = nodeGameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = meshData.material;
            }

            if (meshData.morphTargets != null)
            {
                m_morphTargetHandlers[nodeId] = new OVRGLTFAnimationNodeMorphTargetHandler(meshData);
            }
        }

        var translation = node["translation"].AsArray;
        var rotation = node["rotation"].AsArray;
        var scale = node["scale"].AsArray;

        if (translation.Count > 0 || rotation.Count > 0)
        {
            var pos = Vector3.zero;
            var rot = Quaternion.identity;
            if (translation.Count > 0)
            {
                pos = new Vector3(
                    translation[0] * GLTFToUnitySpace.x,
                    translation[1] * GLTFToUnitySpace.y,
                    translation[2] * GLTFToUnitySpace.z);
            }
            if (rotation.Count > 0)
            {
                rot = new Quaternion(
                    rotation[0] * GLTFToUnitySpace.x * -1.0f,
                    rotation[1] * GLTFToUnitySpace.y * -1.0f,
                    rotation[2] * GLTFToUnitySpace.z * -1.0f,
                    rotation[3]
                );
            }
            nodeTransform.SetPositionAndRotation(pos, rot);
        }

        if (scale.Count > 0)
        {
            nodeTransform.localScale = new Vector3(scale[0], scale[1], scale[2]);
            // disable any zero-scale gameobjects to reduce drawcalls
            nodeTransform.gameObject.SetActive(nodeTransform.gameObject.transform.localScale != Vector3.zero);
        }

        var delta = Time.realtimeSinceStartup - m_processingNodesStart;
        if (!hasSkipped && Time.realtimeSinceStartup - m_processingNodesStart > LoadingMaxTimePerFrame)
        {
            m_processingNodesStart = Time.realtimeSinceStartup;
            yield return null;
        }
    }

    private OVRMeshData ProcessMesh(JSONNode meshNode, bool loadMips)
    {
        OVRMeshData meshData = new OVRMeshData();

        int totalVertexCount = 0;
        var primitives = meshNode["primitives"];
        int[] primitiveVertexCounts = new int[primitives.Count];
        for (int i = 0; i < primitives.Count; i++)
        {
            var jsonPrimitive = primitives[i];
            var jsonAttrbite = jsonPrimitive["attributes"]["POSITION"];
            var jsonAccessor = m_jsonData["accessors"][jsonAttrbite.AsInt];

            primitiveVertexCounts[i] = jsonAccessor["count"];
            totalVertexCount += primitiveVertexCounts[i];
        }

        int[][] indicies = new int[primitives.Count][];

        // Begin async processing of material and its texture

        var jsonMaterial = primitives[0]["material"];
        if (jsonMaterial != null)
        {
            var matData = ProcessMaterial(jsonMaterial.AsInt);
            matData.texture = ProcessTexture(matData.textureId);
            TranscodeTexture(ref matData.texture);

            // reuse materials whenever possible
            int matId = jsonMaterial.AsInt;
            if (m_materials.TryGetValue(matId, out var cachedMat))
            {
                meshData.material = cachedMat;
            }
            else
            {
                Material mat = CreateUnityMaterial(matData, loadMips);
                m_materials.Add(matId, mat);
                meshData.material = mat;
            }
        }

        OVRMeshAttributes attributes = new OVRMeshAttributes();
        OVRMeshAttributes[] morphTargetAttributes = null;

        int vertexOffset = 0;
        for (int i = 0; i < primitives.Count; i++)
        {
            var jsonPrimitive = primitives[i];

            int indicesAccessorId = jsonPrimitive["indices"].AsInt;

            _dataAccessor.Seek(indicesAccessorId);

            indicies[i] = _dataAccessor.ReadInt();
            FlipTriangleIndices(ref indicies[i]);

            attributes = ReadMeshAttributes(jsonPrimitive["attributes"], totalVertexCount, vertexOffset);

            // morph targets
            var jsonAttribute = jsonPrimitive["targets"];
            if (jsonAttribute != null)
            {
                morphTargetAttributes = new OVRMeshAttributes[jsonAttribute.Count];
                for (var ii = 0; ii < jsonAttribute.Count; ii++)
                {
                    morphTargetAttributes[ii] = ReadMeshAttributes(jsonAttribute[ii], totalVertexCount, vertexOffset);
                }
            }

            vertexOffset += primitiveVertexCounts[i];
        }

        Mesh mesh = new Mesh();
        mesh.vertices = attributes.vertices;
        mesh.normals = attributes.normals;
        mesh.tangents = attributes.tangents;
        mesh.colors = attributes.colors;
        mesh.uv = attributes.texcoords;
        mesh.boneWeights = attributes.boneWeights;
        mesh.subMeshCount = primitives.Count;

        int baseVertex = 0;
        for (int i = 0; i < primitives.Count; i++)
        {
            mesh.SetIndices(indicies[i], MeshTopology.Triangles, i, false, baseVertex);
            baseVertex += primitiveVertexCounts[i];
        }

        mesh.RecalculateBounds();
        meshData.mesh = mesh;

        meshData.morphTargets = morphTargetAttributes;
        if (morphTargetAttributes != null)
        {
            meshData.baseAttributes = attributes;
        }

        return meshData;
    }

    private static void FlipTriangleIndices(ref int[] indices)
    {
        for (var i = 0; i < indices.Length; i += 3)
        {
            (indices[i], indices[i + 2]) = (indices[i + 2], indices[i]);
        }
    }

    private OVRMeshAttributes ReadMeshAttributes(JSONNode jsonAttributes, int totalVertexCount, int vertexOffset)
    {
        OVRMeshAttributes results = new OVRMeshAttributes();
        var jsonAttribute = jsonAttributes["POSITION"];
        if (jsonAttribute != null)
        {
            _dataAccessor.Seek(jsonAttribute.AsInt);
            results.vertices = _dataAccessor.ReadVector3(GLTFToUnitySpace);
        }

        jsonAttribute = jsonAttributes["NORMAL"];
        if (jsonAttribute != null)
        {
            _dataAccessor.Seek(jsonAttribute.AsInt);
            results.normals = _dataAccessor.ReadVector3(GLTFToUnitySpace);
        }

        jsonAttribute = jsonAttributes["TANGENT"];
        if (jsonAttribute != null)
        {
            _dataAccessor.Seek(jsonAttribute.AsInt);
            results.tangents = _dataAccessor.ReadVector4(GLTFToUnityTangent);
        }

        jsonAttribute = jsonAttributes["TEXCOORD_0"];
        if (jsonAttribute != null)
        {
            _dataAccessor.Seek(jsonAttribute.AsInt);
            results.texcoords = _dataAccessor.ReadVector2();
        }

        jsonAttribute = jsonAttributes["COLOR_0"];
        if (jsonAttribute != null)
        {
            _dataAccessor.Seek(jsonAttribute.AsInt);
            results.colors = _dataAccessor.ReadColor();
        }

        jsonAttribute = jsonAttributes["WEIGHTS_0"];
        if (jsonAttribute != null)
        {
            results.boneWeights = new BoneWeight[totalVertexCount];
            _dataAccessor.Seek(jsonAttribute.AsInt);
            _dataAccessor.ReadWeights(ref results.boneWeights);

            var jointAttribute = jsonAttributes["JOINTS_0"];
            _dataAccessor.Seek(jointAttribute.AsInt);
            _dataAccessor.ReadJoints(ref results.boneWeights);
        }

        return results;
    }

    private void ProcessSkin(JSONNode skinNode, SkinnedMeshRenderer renderer)
    {
        Matrix4x4[] inverseBindMatrices = null;
        if (skinNode["inverseBindMatrices"] != null)
        {
            var inverseBindMatricesId = skinNode["inverseBindMatrices"].AsInt;
            _dataAccessor.Seek(inverseBindMatricesId);
            inverseBindMatrices = _dataAccessor.ReadMatrix4x4(GLTFToUnitySpace);
        }

        if (skinNode["skeleton"] != null)
        {
            var skeletonRootId = skinNode["skeleton"].AsInt;
            renderer.rootBone = m_Nodes[skeletonRootId].transform;
        }

        Transform[] bones = null;
        if (skinNode["joints"] != null)
        {
            var joints = skinNode["joints"].AsArray;

            bones = new Transform[joints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                bones[i] = m_Nodes[joints[i]].transform;
            }
        }

        renderer.sharedMesh.bindposes = inverseBindMatrices;
        renderer.bones = bones;
    }

    private OVRMaterialData ProcessMaterial(int matId)
    {
        OVRMaterialData matData = new OVRMaterialData();

        var jsonMaterial = m_jsonData["materials"][matId];

        var jsonAlphaMode = jsonMaterial["alphaMode"];
        bool alphaBlendMode = jsonAlphaMode != null && jsonAlphaMode.Value == "BLEND";

        var jsonPbrDetails = jsonMaterial["pbrMetallicRoughness"];

        matData.baseColorFactor = Color.white; // GLTF Default
        var jsonBaseColorFactor = jsonPbrDetails["baseColorFactor"];
        if (jsonBaseColorFactor != null)
        {
            matData.baseColorFactor = new Color(jsonBaseColorFactor[0].AsFloat, jsonBaseColorFactor[1].AsFloat,
                jsonBaseColorFactor[2].AsFloat, jsonBaseColorFactor[3].AsFloat);
        }

        var jsonBaseColor = jsonPbrDetails["baseColorTexture"];
        if (jsonBaseColor != null)
        {
            int textureId = jsonBaseColor["index"].AsInt;
            matData.textureId = textureId;
        }
        else
        {
            var jsonTextrure = jsonMaterial["emissiveTexture"];
            if (jsonTextrure != null)
            {
                int textureId = jsonTextrure["index"].AsInt;
                matData.textureId = textureId;
            }
        }

        matData.shader = alphaBlendMode ? m_AlphaBlendShader : m_Shader;
        return matData;
    }

    private OVRTextureData ProcessTexture(int textureId)
    {
        var jsonTexture = m_jsonData["textures"][textureId];

        int imageSource = -1;
        var jsonExtensions = jsonTexture["extensions"];
        if (jsonExtensions != null)
        {
            var baisuExtension = jsonExtensions["KHR_texture_basisu"];
            if (baisuExtension != null)
            {
                imageSource = baisuExtension["source"].AsInt;
            }
        }
        else
        {
            imageSource = jsonTexture["source"].AsInt;
        }

        var jsonSource = m_jsonData["images"][imageSource];

        OVRTextureData textureData = new OVRTextureData();

        var jsonSourceUri = jsonSource["uri"].Value;
        if (!String.IsNullOrEmpty(jsonSourceUri))
        {
            textureData.uri = jsonSourceUri;
            return textureData;
        }

        // skip "sampler". not supported

        var bufferViewId = jsonSource["bufferView"].AsInt;
        switch (jsonSource["mimeType"].Value)
        {
            case "image/ktx2":
                textureData.data = _dataAccessor.ReadBuffer(bufferViewId);
                textureData.format = OVRTextureFormat.KTX2;
                break;
            case "image/png":
                textureData.data = _dataAccessor.ReadBuffer(bufferViewId);
                textureData.format = OVRTextureFormat.PNG;
                break;
            default:
                Debug.LogWarning($"Unsupported image mimeType '{jsonSource["mimeType"].Value}'");
                break;
        }
        return textureData;
    }

    private void TranscodeTexture(ref OVRTextureData textureData)
    {
        if (!String.IsNullOrEmpty(textureData.uri))
        {
            return;
        }
        if (textureData.format == OVRTextureFormat.KTX2)
        {
            OVRKtxTexture.Load(textureData.data, ref textureData);
        }
        else if (textureData.format == OVRTextureFormat.PNG)
        {
            // fall back to unity Texture2D.LoadImage, which will override dimensions & format.
        }
        else
        {
            Debug.LogWarning("Only KTX2 textures can be trascoded.");
        }
    }

    private Material CreateUnityMaterial(OVRMaterialData matData, bool loadMips)
    {
        Material mat = new Material(matData.shader);

        mat.color = matData.baseColorFactor;

        if (loadMips && mat.HasProperty("_MainTexMMBias"))
            mat.SetFloat("_MainTexMMBias", m_TextureMipmapBias);

        Texture2D texture = null;
        bool configureCreatedTexture = false;
        if (m_textures.TryGetValue(matData.textureId, out texture))
        {
            mat.mainTexture = texture;
            return mat;
        }

        if (matData.texture.format == OVRTextureFormat.KTX2)
        {
            texture = new Texture2D(matData.texture.width, matData.texture.height, matData.texture.transcodedFormat,
                loadMips);
            texture.LoadRawTextureData(matData.texture.data);
            configureCreatedTexture = true;
        }
        else if (matData.texture.format == OVRTextureFormat.PNG)
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, loadMips);
            texture.LoadImage(matData.texture.data);
            configureCreatedTexture = true;
        }
        else if (!String.IsNullOrEmpty(matData.texture.uri))
        {
            texture = textureUriHandler?.Invoke(matData.texture.uri, mat);
        }

        if (!texture) return mat;
        if (configureCreatedTexture)
        {
            ApplyTextureQuality(m_TextureQuality, ref texture);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        }
        m_textures[matData.textureId] = texture;
        mat.mainTexture = texture;
        return mat;
    }

    private OVRGLTFInputNode GetInputNodeType(string name)
    {
        foreach (var item in InputNodeNameMap)
        {
            if (name.Contains(item.Key))
            {
                return item.Value;
            }
        }

        return OVRGLTFInputNode.None;
    }

    private IEnumerator ProcessAnimations()
    {
        var animations = m_jsonData["animations"];
        var animationIndex = 0;
        var processingStart = Time.realtimeSinceStartup;
        foreach (JSONNode animation in animations.AsArray)
        {
            //We don't need animation name at this moment
            //string name = animation["name"].ToString();
            var animationNodeLookup = new Dictionary<int, OVRGLTFAnimatinonNode>();
            var channels = animation["channels"].AsArray;
            foreach (JSONNode channel in channels)
            {
                int nodeId = channel["target"]["node"].AsInt;

                OVRGLTFInputNode inputNodeType = GetInputNodeType(m_Nodes[nodeId].name);
                if (!animationNodeLookup.TryGetValue(nodeId, out var animationNode))
                {
                    m_morphTargetHandlers.TryGetValue(nodeId, out var morphTargetHandler);
                    animationNode = animationNodeLookup[nodeId] = new OVRGLTFAnimatinonNode(
                        inputNodeType, m_Nodes[nodeId],
                        morphTargetHandler);
                }

                if (inputNodeType != OVRGLTFInputNode.None)
                {
                    if (!m_InputAnimationNodes.ContainsKey(inputNodeType))
                    {
                        m_InputAnimationNodes[inputNodeType] = animationNode;
                    }
                }

                animationNode.AddChannel(channel, animation["samplers"], _dataAccessor);
            }
            m_AnimationLookup[animationIndex] = animationNodeLookup.Values.ToArray();
            animationIndex++;
            if (Time.realtimeSinceStartup - processingStart > LoadingMaxTimePerFrame)
            {
                processingStart = Time.realtimeSinceStartup;
                yield return null;
            }
        }
    }
}
