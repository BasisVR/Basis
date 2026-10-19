using System;
using UnityEngine;
using UnityEditor.SceneTemplate;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using UnityEditor.Rendering.Utilities;

namespace UnityEditor.Rendering.Universal
{
    class URPBasicScenePipeline : ISceneTemplatePipeline
    {
        void ISceneTemplatePipeline.AfterTemplateInstantiation(SceneTemplateAsset sceneTemplateAsset, Scene scene, bool isAdditive, string sceneName)
        {
            //To avoid problematic behavior and warnings in the future, let's remove all missing scripts monobehaviors. 
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            foreach (var go in scene.GetRootGameObjects())
            {
                foreach (var meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (meshFilter.sharedMesh != null)
                    {
                        var mesh = meshFilter.sharedMesh;
                        meshFilter.sharedMesh = null;
                        meshFilter.sharedMesh = mesh;
                    }
                }
            }
        }

        void ISceneTemplatePipeline.BeforeTemplateInstantiation(SceneTemplateAsset sceneTemplateAsset, bool isAdditive, string sceneName)
        {

        }

        bool ISceneTemplatePipeline.IsValidTemplateForInstantiation(SceneTemplateAsset sceneTemplateAsset)
        {
            return true;
        }
    }
}
