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
using System.ComponentModel;
using System.Linq;
using Meta.XR.Util;
using UnityEngine;
using UnityEngine.Assertions;

/// <summary>
/// This class is responsible for managing the mapping between blend shapes of a character's mesh
/// and the set of available <see cref="OVRFaceExpressions.FaceExpression"/>. Use this class to
/// apply the generation of the mapping using <see cref="OVRCustomFaceExtensions.AutoGenerateMapping"/>,
/// so that each blend shape has an associated face expression.
/// For more information, see [Face Tracking for Movement SDK for Unity](https://developer.oculus.com/documentation/unity/move-face-tracking/).
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/move-face-tracking/")]
[Feature(Feature.FaceTracking)]
public static class OVRCustomFaceExtensions
{
    /// <summary>
    /// Find the best matching blend shape for each facial expression based on their names. Use this
    /// function to create an association between a <see cref="SkinnedMeshRenderer"/>'s blend shapes
    /// and <see cref="OVRFaceExpressions.FaceExpression"/> values.
    /// </summary>
    /// <remarks>
    /// This function tokenizes face expression enum strings and blend shape name strings in order to
    /// find an array of <see cref="OVRFaceExpressions.FaceExpression"/> to the blend shapes of a
    /// model. It quantifies the quality of the match by the total number of characters
    /// in the matching tokens. Furthermore, it requires at least a total of more than 2 characters
    /// to match, to avoid matching just single characters. A better technique might be to use
    /// Levenshtein distance to match the tokens to allow some typos while still being allowing
    /// flexibility with respect to the order of tokens.
    /// </remarks>
    /// <param name="skinnedMesh">The mesh to find a mapping for.</param>
    /// <param name="blendShapeNames">Array of blend shape names.</param>
    /// <param name="faceExpressions">Array of <see cref="OVRFaceExpressions.FaceExpression"> id
    /// for mapping to them.</param>
    /// <param name="allowDuplicateMapping">Whether to allow duplicate mapping or not</param>
    /// <returns>Returns an array of <see cref="OVRFaceExpressions.FaceExpression"/> of the same
    /// length as the number of blend shapes on the <paramref name="skinnedMesh"/>, with each
    /// element identifying the closest match found.</returns>
    public static OVRFaceExpressions.FaceExpression[] AutoGenerateMapping(
        Mesh skinnedMesh,
        string[] blendShapeNames,
        OVRFaceExpressions.FaceExpression[] faceExpressions,
        bool allowDuplicateMapping)
    {
        Assert.AreEqual(blendShapeNames.Length, faceExpressions.Length);
        var result = new OVRFaceExpressions.FaceExpression[skinnedMesh.blendShapeCount];
        var expressionTokens = new HashSet<string>[blendShapeNames.Length];
        for (int i = 0; i < blendShapeNames.Length; ++i)
        {
            expressionTokens[i] = TokenizeString(blendShapeNames[i]);
        }

        var usedBlendshapes = new HashSet<OVRFaceExpressions.FaceExpression>();
        for (int i = 0; i < skinnedMesh.blendShapeCount; ++i)
        {
            var blendShapeName = skinnedMesh.GetBlendShapeName(i);
            var bestMatchFound = FindBestMatch(
                expressionTokens,
                blendShapeName,
                faceExpressions,
                OVRFaceExpressions.FaceExpression.Invalid);
            // If not allowing duplicates, make an exception for liptowards.
            if (!allowDuplicateMapping &&
                (usedBlendshapes.Contains(bestMatchFound) &&
                 !IsLipsToward(blendShapeName)))
            {
                result[i] = OVRFaceExpressions.FaceExpression.Invalid;
            }
            else
            {
                result[i] = bestMatchFound;
                usedBlendshapes.Add(bestMatchFound);
            }
        }

        return result;
    }

    private static OVRFaceExpressions.FaceExpression FindBestMatch(HashSet<string>[] tokenizedOptions,
        string searchString, OVRFaceExpressions.FaceExpression[] expressions,
        OVRFaceExpressions.FaceExpression fallback)
    {
        // remove model name prefix if present
        searchString = searchString.Substring(searchString.LastIndexOf('.') + 1);
        HashSet<string> blendShapeTokens = TokenizeString(searchString);

        OVRFaceExpressions.FaceExpression bestMatch = fallback;

        // require more than two characters to match in an expression, to avoid just matching L/ LB/ R/RB
        int bestMatchCount = 2;

        for (int j = 0; j < tokenizedOptions.Length; ++j)
        {
            int thisMatchCount = 0;
            HashSet<string> thisSet = tokenizedOptions[j];
            // Only finds exact matches. The Levenshtein distance would be more "fuzzy" and
            // would allow for handling of common typos and other slight mismatches.
            foreach (string matchingToken in blendShapeTokens.Intersect(thisSet))
            {
                thisMatchCount += matchingToken.Length;
            }

            if (thisMatchCount > bestMatchCount)
            {
                bestMatchCount = thisMatchCount;
                bestMatch = expressions[j];
            }
        }

        return bestMatch;
    }

    private static bool IsLipsToward(string blendshapeName)
    {
        blendshapeName = blendshapeName.Substring(blendshapeName.IndexOf('.') + 1);
        return blendshapeName == "lipsToward_LB" ||
               blendshapeName == "lipsToward_RB" ||
               blendshapeName == "lipsToward_LT" ||
               blendshapeName == "lipsToward_RT";
    }

    internal static HashSet<string> TokenizeString(string s)
    {
        var separators = new char[] { ' ', '_', '-', ',', '.', ';' };
        // add both the camel case and non-camel case split versions since the
        // camel case split doesn't handle all caps
        //(it's fundamentally ambigous without natural language comprehension)
        // duplicates don't matter as we later will hash them and they should match
        var splitTokens = SplitCamelCase(s).Split(separators).Concat(s.Split(separators));

        var hashCodes = new HashSet<string>();
        foreach (string token in splitTokens)
        {
            string lowerCaseToken = token.ToLowerInvariant();
            // give a chance for synonyms to mach with low weight
            if (lowerCaseToken == "left" || lowerCaseToken == "l")
            {
                hashCodes.Add("L");
            }

            if (lowerCaseToken == "right" || lowerCaseToken == "r")
            {
                hashCodes.Add("R");
            }

            hashCodes.Add(lowerCaseToken);
        }

        return hashCodes;
    }

    private static string SplitCamelCase(string input) => System.Text.RegularExpressions.Regex
        .Replace(input, "([A-Z])", " $1", System.Text.RegularExpressions.RegexOptions.Compiled).Trim();


    /// <summary>
    /// The extension method that generates a mapping between the <see cref="OVRCustomFace"/> blend shapes
    /// and the <see cref="OVRFaceExpressions.FaceExpression"/>s. Use this function to
    /// generate the mapping on the <see cref="OVRCustomFace"/> in order for face tracking to function
    /// properly on the character's skinned mesh renderer.
    /// /// </summary>
    /// <param name="customFace">Custom face component.</param>
    public static void AutoMapBlendshapes(this OVRCustomFace customFace)
    {
        var type = customFace.retargetingType;
        var renderer = customFace.GetComponent<SkinnedMeshRenderer>();

        try
        {
            OVRFaceExpressions.FaceExpression[] generatedMapping;
            switch (type)
            {
                case OVRCustomFace.RetargetingType.OculusFace:
                    generatedMapping = OculusFaceAutoGenerateMapping(renderer.sharedMesh,
                        customFace._allowDuplicateMapping);
                    break;
                case OVRCustomFace.RetargetingType.Custom:
                    generatedMapping = CustomAutoGeneratedMapping(customFace,
                        renderer.sharedMesh,
                        customFace._allowDuplicateMapping);
                    break;
                default:
                    throw new InvalidEnumArgumentException($"Invalid {nameof(OVRCustomFace.RetargetingType)}");
            }

            if (generatedMapping != null)
            {
                Assert.AreEqual(generatedMapping.Length, renderer.sharedMesh.blendShapeCount);
                if (customFace._mappings == null || customFace._mappings.Length != renderer.sharedMesh.blendShapeCount)
                {
                    customFace._mappings =
                        new OVRFaceExpressions.FaceExpression[renderer.sharedMesh.blendShapeCount];
                }

                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; ++i)
                {
                    customFace._mappings[i] = generatedMapping[i];
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Auto Map Face Error: {e.Message}");
        }
    }

    /// <summary>
    /// The extension method that clears the blend shape mappings on
    /// a <see cref="OVRCustomFace"/> instance. Use this in case the mappings on the
    /// instance are not correct. This could happen if the mapping algorithm did not
    /// produce a satisfactory result when being driven by face tracking's
    /// <see cref="OVRFaceExpressions.FaceExpression"/> values.
    /// </summary>
    /// <param name="customFace">custom face component</param>
    public static void ClearBlendshapes(this OVRCustomFace customFace)
    {
        var renderer = customFace.GetComponent<SkinnedMeshRenderer>();
        for (int i = 0; i < renderer.sharedMesh.blendShapeCount; ++i)
        {
            customFace._mappings[i] = OVRFaceExpressions.FaceExpression.Invalid;
        }
    }

    internal static OVRFaceExpressions.FaceExpression[] OculusFaceAutoGenerateMapping(Mesh sharedMesh,
        bool allowDuplicateMapping)
    {
        string[] oculusBlendShapeNames = Enum.GetNames(typeof(OVRFaceExpressions.FaceExpression));
        OVRFaceExpressions.FaceExpression[] oculusFaceExpressions =
            (OVRFaceExpressions.FaceExpression[])Enum.GetValues(typeof(OVRFaceExpressions.FaceExpression));
        return AutoGenerateMapping(sharedMesh,
            oculusBlendShapeNames, oculusFaceExpressions, allowDuplicateMapping);
    }

    internal static OVRFaceExpressions.FaceExpression[] CustomAutoGeneratedMapping(OVRCustomFace customFace,
        Mesh sharedMesh,
        bool allowDuplicateMapping)
    {
        string[] customBlendShapeNames;
        OVRFaceExpressions.FaceExpression[] customFaceExpressions;
        (customBlendShapeNames, customFaceExpressions) = customFace.GetCustomBlendShapeNameAndExpressionPairs();
        return AutoGenerateMapping(sharedMesh,
            customBlendShapeNames, customFaceExpressions, allowDuplicateMapping);
    }

}
