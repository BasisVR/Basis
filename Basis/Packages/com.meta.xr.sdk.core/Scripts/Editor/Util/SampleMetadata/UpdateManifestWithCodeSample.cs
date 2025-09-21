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
using System.Text;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

namespace Meta.XR.Samples.Editor
{
    public class UpdateManifestWithCodeSample : IPostGenerateGradleAndroidProject
    {
        const string NAMESPACE_ATTRIBUTE = "xmlns:android";
        const string ELEMENT_NAME = "meta-data";
        const string NODE_NAME_PREFIX = "com.meta.usedsamples";
        const string NODE_PATH = "manifest/application";

        /// <summary>
        /// The callback that receives the path to the gradle project.
        /// </summary>
        /// <param name="path"></param>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var listOfSamples = FindAllMetaCodeSamplesAttributes();

            // Get the AndroidManifest.xml files and append the code sample.
            var manifestFiles = Directory.GetFiles(Path.Combine(path, ".."), "AndroidManifest.xml", SearchOption.AllDirectories);
            foreach (var manifestFile in manifestFiles)
            {
                UpdateManifest(manifestFile, listOfSamples);
            }
        }

        public int callbackOrder { get; }

        /// <summary>
        /// Create the full node name using the node prefix and sample name
        /// </summary>
        /// <param name="sampleName"></param>
        /// <returns>the created node name</returns>
        private static string CreateNodeName(string sampleName)
        {
            return $"{NODE_NAME_PREFIX}.{sampleName}";
        }

        /// <summary>
        /// Update the AndroidManifest with the list of samples and the classes found in the project
        /// </summary>
        /// <param name="manifestPath">The path to the Android manifest to update</param>
        /// <param name="allSamples">Map of samples name and the classes related to them</param>
        private static void UpdateManifest(string manifestPath, Dictionary<string, HashSet<string>> allSamples)
        {
            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var manifestElement = (XmlElement)doc.SelectSingleNode("/manifest");
            if (manifestElement == null)
            {
                Debug.LogError($"Could not find manifest tag in android manifest.");
                return;
            }

            if ((XmlElement)doc.SelectSingleNode(NODE_PATH) == null)
            {
                Debug.LogError($"Could not find {NODE_PATH} tag in android manifest.");
                return;
            }

            var androidNamespaceUri = manifestElement.GetAttribute(NAMESPACE_ATTRIBUTE);
            var allExistingNodes = doc.SelectNodes(NODE_PATH + "/" + ELEMENT_NAME);
            foreach (var sampleInfo in allSamples)
            {
                var nodeName = CreateNodeName(sampleInfo.Key);
                XmlElement nodeElement = null;
                if (allExistingNodes != null)
                {
                    foreach (XmlElement e in allExistingNodes)
                    {
                        if (nodeName == null || nodeName == e.GetAttribute("name", androidNamespaceUri))
                        {
                            nodeElement = e;
                            break;
                        }
                    }
                }

                if (nodeElement == null)
                {
                    var parent = doc.SelectSingleNode(NODE_PATH);
                    nodeElement = doc.CreateElement(ELEMENT_NAME);
                    nodeElement.SetAttribute("name", androidNamespaceUri, nodeName);
                    parent?.AppendChild(nodeElement);
                }


                var classes = new StringBuilder();
                classes.AppendJoin("|", sampleInfo.Value);
                nodeElement.SetAttribute("value", androidNamespaceUri, classes.ToString());
            }

            var settings = new XmlWriterSettings
            {
                NewLineChars = "\n",
                Indent = true,
            };
            using var writer = XmlWriter.Create(manifestPath, settings);
            doc.Save(writer);
        }

        /// <summary>
        /// Find all CodeSampleAttributes and return a list of sample name.
        /// </summary>
        /// <returns>List of sample name</returns>
        private static Dictionary<string, HashSet<string>> FindAllMetaCodeSamplesAttributes()
        {
            var codeSamples = new Dictionary<string, HashSet<string>>();
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    var codeSampleAttributes = type.GetCustomAttributes(typeof(MetaCodeSampleAttribute), false);
                    if (codeSampleAttributes.Length > 0)
                    {
                        foreach (MetaCodeSampleAttribute attr in codeSampleAttributes)
                        {
                            if (!codeSamples.ContainsKey(attr.SampleName))
                            {
                                codeSamples.Add(attr.SampleName, new HashSet<string>());
                            }
                            codeSamples[attr.SampleName].Add(type.Name);
                        }
                    }
                }
            }

            return codeSamples;
        }

    }
}
