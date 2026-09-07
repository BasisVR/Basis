using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Basis
{
    /// <summary>One captured still: where its file lives, the pose it was taken from, and the intrinsics it was taken with.</summary>
    public readonly struct BasisPhotogrammetryFrame
    {
        /// <summary>Capture order. Frames are written to the manifest sorted by this, not by encode-completion order.</summary>
        public readonly int Index;

        /// <summary>Relative to the manifest file, e.g. <c>images/00001.png</c>.</summary>
        public readonly string RelativeFilePath;

        /// <summary>Camera-to-world, already in the nerfstudio/instant-ngp convention — see <see cref="BasisPhotogrammetryPose.BuildNerfTransformMatrix"/>.</summary>
        public readonly Matrix4x4 TransformMatrix;

        public readonly float FocalLengthX;
        public readonly float FocalLengthY;
        public readonly float PrincipalPointX;
        public readonly float PrincipalPointY;
        public readonly int Width;
        public readonly int Height;

        public BasisPhotogrammetryFrame(int index, string relativeFilePath, Matrix4x4 transformMatrix,
            float focalLengthX, float focalLengthY, float principalPointX, float principalPointY,
            int width, int height)
        {
            Index = index;
            RelativeFilePath = relativeFilePath;
            TransformMatrix = transformMatrix;
            FocalLengthX = focalLengthX;
            FocalLengthY = focalLengthY;
            PrincipalPointX = principalPointX;
            PrincipalPointY = principalPointY;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Hand-written nerfstudio/instant-ngp <c>transforms.json</c> — the same "no JSON library"
    /// idiom as <c>BasisHandHeldCameraPhotoMetadata.BuildBasisJson</c>, the only other hand-rolled
    /// JSON in this package. The shape is one fixed record repeated per frame, so a generic
    /// serializer would only add a dependency this package does not otherwise have.
    ///
    /// <para>Top-level intrinsics mirror the first frame's, and every frame also carries its own —
    /// nerfstudio reads per-frame overrides, so a FOV change mid-session (the operator drags the
    /// zoom slider mid-walk) still produces a correct file instead of a silently wrong one.</para>
    /// </summary>
    public static class BasisPhotogrammetryManifest
    {
        public static string BuildJson(IReadOnlyList<BasisPhotogrammetryFrame> frames)
        {
            List<BasisPhotogrammetryFrame> ordered = new List<BasisPhotogrammetryFrame>(frames);
            ordered.Sort((a, b) => a.Index.CompareTo(b.Index));

            StringBuilder json = new StringBuilder(1024 + ordered.Count * 512);
            json.Append("{\n");

            if (ordered.Count > 0)
            {
                BasisPhotogrammetryFrame first = ordered[0];
                json.Append("  \"camera_model\": \"OPENCV\",\n");
                AppendFloatField(json, "fl_x", first.FocalLengthX, "  ");
                AppendFloatField(json, "fl_y", first.FocalLengthY, "  ");
                AppendFloatField(json, "cx", first.PrincipalPointX, "  ");
                AppendFloatField(json, "cy", first.PrincipalPointY, "  ");
                AppendIntField(json, "w", first.Width, "  ");
                AppendIntField(json, "h", first.Height, "  ");
                json.Append("  \"k1\": 0.0,\n  \"k2\": 0.0,\n  \"p1\": 0.0,\n  \"p2\": 0.0,\n");
            }

            json.Append("  \"frames\": [\n");
            for (int index = 0; index < ordered.Count; index++)
            {
                AppendFrame(json, ordered[index], isLast: index == ordered.Count - 1);
            }
            json.Append("  ]\n}\n");
            return json.ToString();
        }

        private static void AppendFrame(StringBuilder json, BasisPhotogrammetryFrame frame, bool isLast)
        {
            const string indent = "      ";
            json.Append("    {\n");
            json.Append(indent).Append("\"file_path\": \"").Append(EscapeJsonString(frame.RelativeFilePath)).Append("\",\n");
            AppendFloatField(json, "fl_x", frame.FocalLengthX, indent);
            AppendFloatField(json, "fl_y", frame.FocalLengthY, indent);
            AppendFloatField(json, "cx", frame.PrincipalPointX, indent);
            AppendFloatField(json, "cy", frame.PrincipalPointY, indent);
            AppendIntField(json, "w", frame.Width, indent);
            AppendIntField(json, "h", frame.Height, indent);

            json.Append(indent).Append("\"transform_matrix\": [\n");
            for (int row = 0; row < 4; row++)
            {
                Vector4 values = frame.TransformMatrix.GetRow(row);
                json.Append(indent).Append("  [")
                    .Append(FormatFloat(values.x)).Append(", ")
                    .Append(FormatFloat(values.y)).Append(", ")
                    .Append(FormatFloat(values.z)).Append(", ")
                    .Append(FormatFloat(values.w)).Append(row == 3 ? "]\n" : "],\n");
            }
            json.Append(indent).Append("]\n");

            json.Append(isLast ? "    }\n" : "    },\n");
        }

        private static void AppendFloatField(StringBuilder json, string key, float value, string indent) =>
            json.Append(indent).Append('"').Append(key).Append("\": ").Append(FormatFloat(value)).Append(",\n");

        private static void AppendIntField(StringBuilder json, string key, int value, string indent) =>
            json.Append(indent).Append('"').Append(key).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        /// <summary>Round-trippable and locale-proof — JSON requires a '.' decimal separator regardless of the machine's culture.</summary>
        private static string FormatFloat(float value) => value.ToString("G9", CultureInfo.InvariantCulture);

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            StringBuilder escaped = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    default: escaped.Append(character); break;
                }
            }
            return escaped.ToString();
        }
    }
}
