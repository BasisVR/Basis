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

/// <summary>
/// Descriptive labels of the <see cref="OVRAnchor"/>, as a list of enum values.
/// </summary>
/// <remarks>
/// This component can be accessed from an <see cref="OVRAnchor"/> that supports it by calling
/// <see cref="OVRAnchor.GetComponent{T}"/> from the anchor.
///
/// The semantic labels comonent is part of the Meta Quest Scene Model. Read more at
/// [Scene Overview](https://developer.oculus.com/documentation/unity/unity-scene-overview/).
/// </remarks>
/// <seealso cref="Labels"/>
public readonly partial struct OVRSemanticLabels : IOVRAnchorComponent<OVRSemanticLabels>, IEquatable<OVRSemanticLabels>
{
    private static char[] _semanticLabelsBuffer;

    /// <summary>
    /// An enum that contains all possible classification values.
    /// </summary>
    public enum Classification
    {
        Floor = 0,
        Ceiling = 1,
        WallFace = 2,
        Table = 3,
        Couch = 4,
        DoorFrame = 5,
        WindowFrame = 6,
        Other = 7,
        Storage = 8,
        Bed = 9,
        Screen = 10,
        Lamp = 11,
        Plant = 12,
        WallArt = 13,
        SceneMesh = 14,
        InvisibleWallFace = 15,
    }

    internal const string DeprecationMessage = "String-based labels are deprecated (v65). Please use the equivalent enum-based methods.";

    /// <summary>
    /// \deprecated Semantic Labels. Please use <see cref="GetClassifications"/> instead.
    /// </summary>
    /// <returns>
    /// <para>Returns comma-separated values in one <see cref="string"/></para>
    /// </returns>
    /// <exception cref="Exception">If it fails to get the semantic labels</exception>
    [Obsolete(DeprecationMessage)]
    public string Labels
    {
        get
        {
            if (!OVRPlugin.GetSpaceSemanticLabels(Handle, out var labels))
            {
                throw new Exception("Could not Get Semantic Labels");
            }

            return OVRSemanticClassification.ValidateAndUpgradeLabels(labels);
        }
    }

    /// <summary>
    /// Get the Semantic Labels. Non-allocating.
    /// </summary>
    public void GetClassifications(ICollection<Classification> classifications)
    {
        if (!OVRPlugin.GetSpaceSemanticLabelsNonAlloc(Handle, ref _semanticLabelsBuffer, out int length))
            throw new Exception("Could not Get Semantic Labels");

        classifications.Clear();
        FromApiString(new ReadOnlySpan<char>(_semanticLabelsBuffer, 0, length), classifications);

        // upgrade any labels
        //  - desk -> table is no longer performed
        //  - if invisible_wall_face, must contain wall_face
        var hasInvisibleWallFace = classifications.Contains(Classification.InvisibleWallFace);
        var hasWallFace = classifications.Contains(Classification.WallFace);
        if (hasInvisibleWallFace && !hasWallFace)
            classifications.Add(Classification.WallFace);
    }

    // Convert a single label into a Classification.
    // NOTE: unsupported labels are always OTHER (including deprecated DESK).
    internal static Classification FromApiLabel(ReadOnlySpan<char> singleLabel)
    {
        if (singleLabel.SequenceEqual("FLOOR")) return Classification.Floor;
        if (singleLabel.SequenceEqual("CEILING")) return Classification.Ceiling;
        if (singleLabel.SequenceEqual("WALL_FACE")) return Classification.WallFace;
        if (singleLabel.SequenceEqual("COUCH")) return Classification.Couch;
        if (singleLabel.SequenceEqual("DOOR_FRAME")) return Classification.DoorFrame;
        if (singleLabel.SequenceEqual("WINDOW_FRAME")) return Classification.WindowFrame;
        if (singleLabel.SequenceEqual("OTHER")) return Classification.Other;
        if (singleLabel.SequenceEqual("STORAGE")) return Classification.Storage;
        if (singleLabel.SequenceEqual("BED")) return Classification.Bed;
        if (singleLabel.SequenceEqual("SCREEN")) return Classification.Screen;
        if (singleLabel.SequenceEqual("LAMP")) return Classification.Lamp;
        if (singleLabel.SequenceEqual("PLANT")) return Classification.Plant;
        if (singleLabel.SequenceEqual("TABLE")) return Classification.Table;
        if (singleLabel.SequenceEqual("WALL_ART")) return Classification.WallArt;
        if (singleLabel.SequenceEqual("INVISIBLE_WALL_FACE")) return Classification.InvisibleWallFace;
        if (singleLabel.SequenceEqual("GLOBAL_MESH")) return Classification.SceneMesh;

        Debug.LogWarning($"Unknown classification: {singleLabel.ToString()}");
        return Classification.Other;
    }

    // Converts a comma separated list of labels into a list of Classifications.
    internal static void FromApiString(ReadOnlySpan<char> apiLabels, ICollection<Classification> classifications)
    {
        // we avoid string.Split(',') because of allocations
        // and because the input is restricted by OpenXR
        var from = 0;
        int to;
        while ((to = IndexOf(apiLabels, ',', from)) != -1)
        {
            AddLabel(apiLabels.Slice(from, to - from), classifications);
            from = to + 1;
        }
        if (from < apiLabels.Length)
            AddLabel(apiLabels.Slice(from), classifications);

        void AddLabel(ReadOnlySpan<char> label, ICollection<Classification> labels)
        {
            // skip any labels we no longer support
#pragma warning disable CS0618 // Type or member is obsolete
            if (!label.SequenceEqual(OVRSceneManager.Classification.Desk))
                labels.Add(FromApiLabel(label));
#pragma warning restore CS0618 // Type or member is obsolete
        }

        int IndexOf(ReadOnlySpan<char> s, char c, int start)
        {
            for (var i = start; i < s.Length; i++)
                if (s[i] == c)
                    return i;
            return -1;
        }
    }

    // Convert a single classification into an API label.
    internal static string ToApiLabel(Classification classification) =>
        classification switch
        {
#pragma warning disable CS0618, CS0612 // Type or member is obsolete
            Classification.Floor => OVRSceneManager.Classification.Floor,
            Classification.Ceiling => OVRSceneManager.Classification.Ceiling,
            Classification.WallFace => OVRSceneManager.Classification.WallFace,
            Classification.Couch => OVRSceneManager.Classification.Couch,
            Classification.DoorFrame => OVRSceneManager.Classification.DoorFrame,
            Classification.WindowFrame => OVRSceneManager.Classification.WindowFrame,
            Classification.Other => OVRSceneManager.Classification.Other,
            Classification.Storage => OVRSceneManager.Classification.Storage,
            Classification.Bed => OVRSceneManager.Classification.Bed,
            Classification.Screen => OVRSceneManager.Classification.Screen,
            Classification.Lamp => OVRSceneManager.Classification.Lamp,
            Classification.Plant => OVRSceneManager.Classification.Plant,
            Classification.Table => OVRSceneManager.Classification.Table,
            Classification.WallArt => OVRSceneManager.Classification.WallArt,
            Classification.InvisibleWallFace => OVRSceneManager.Classification.InvisibleWallFace,
            Classification.SceneMesh => OVRSceneManager.Classification.GlobalMesh,
            _ => OVRSceneManager.Classification.Other,
#pragma warning restore CS0618, CS0612 // Type or member is obsolete
        };

    // Convert a list of classifications into a comma-separated string. Null returns the empty string.
    internal static string ToApiString(IReadOnlyList<Classification> classifications)
    {
        if (classifications == null) return string.Empty;

        using (new OVRObjectPool.ListScope<string>(out var labels))
        {
            foreach (var classification in classifications)
            {
                labels.Add(ToApiLabel(classification));
            }

            return string.Join(',', labels);
        }
    }
}
