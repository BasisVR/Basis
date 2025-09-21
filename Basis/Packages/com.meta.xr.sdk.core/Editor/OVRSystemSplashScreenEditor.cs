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
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Oculus.VR.Editor
{
    public class OVRSystemSplashScreenEditor
    {
        private const string DefaultFileName = "OculusSystemSplashScreen";

        /// <summary>
        /// Utility method that tries to make a blit copy of the input texture, and outputs its
        /// content in the suitable file encoding & format to the default path.
        ///
        /// If it fails (e.g. No GPU), it will return the original input texture instead.
        /// </summary>
        /// <param name="texture">Input texture to make a copy of</param>
        /// <returns>The copied texture, or the input texture if the operation fails</returns>
        public static Texture2D ProcessTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return null;
            }

            string splashScreenAssetPath = AssetDatabase.GetAssetPath(texture);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                // If no Gfx device, directly copy the image file and hope for the best
                // (e.g. automated builds in batch mode)
                Debug.LogWarning(
                    "Generate system splash screen: No graphics device found. Falling back to the original file. " +
                    "Please ensure the source image has the correct PNG encoding.");
                if (Path.GetExtension(splashScreenAssetPath).ToLower() != ".png")
                {
                    Debug.LogError(
                        "Invalid file format of System Splash Screen. It has to be a PNG file to be used by the Quest OS. The asset path: " +
                        splashScreenAssetPath);
                    return null;
                }
                return texture;
            }

            // If Gfx device found, blit to an intermediate texture to rectify image formats
            var rt = new RenderTexture(texture.width, texture.height, 0);
            Graphics.Blit(texture, rt);
            var tempTexture = new Texture2D(
                rt.width,
                rt.height,
                TextureFormat.RGBA32,
                mipChain: false,
                linear: false);
            tempTexture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);

            string saveTextureFilePath = PromptSaveTextureFilePath();
            if (string.IsNullOrWhiteSpace(saveTextureFilePath))
            {
                // User reject - bail
                return null;
            }

            try
            {
                File.WriteAllBytes(saveTextureFilePath, tempTexture.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogError("Unable to generate splash screen texture: " + e.Message);
                return null;
            }
            Debug.LogFormat("Generated system splash screen asset at {0}", saveTextureFilePath);

            string saveTextureRelativePath = Path.Join("Assets",
                Path.GetRelativePath(Application.dataPath, saveTextureFilePath));
            AssetDatabase.ImportAsset(saveTextureRelativePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(saveTextureRelativePath);

            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(saveTextureRelativePath);
        }

        private static string PromptSaveTextureFilePath()
        {
            return EditorUtility.DisplayDialog("System Splash Screen Texture",
                "A compatible texture copy has been generated. Save and apply it now?",
                "Save and use copy",
                "Use original")
                ? EditorUtility.SaveFilePanel("Save generated system splash screen texture...",
                    "Assets",
                    DefaultFileName,
                    "png")
                : null;
        }
    }
}
