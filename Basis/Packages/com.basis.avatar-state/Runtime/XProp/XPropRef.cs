using System;
using System.Collections.Generic;
using System.Text;

namespace Jinxxy.Item.XProp
{
    public enum PathType
    {
        ContextRelative,   // ./...
        ScopeRelative,     // #/...
        Absolute           // /...
    }

    /// <summary>
    /// Represents a parsed XProp reference according to v0.1.0 specification
    /// </summary>
    public readonly struct XPropRef : IEquatable<XPropRef>
    {
        public readonly PathType PathType;
        public readonly string[] Hierarchy;
        public readonly string Facet;
        public readonly string[] Qualifiers;
        public readonly string TypeHint;
        public readonly string PropertyPath;

        public XPropRef(
            PathType pathType,
            string[] hierarchy,
            string facet,
            string[] qualifier,
            string typeHint,
            string propertyPath)
        {
            PathType = pathType;
            Hierarchy = hierarchy ?? Array.Empty<string>();
            Facet = facet ?? throw new ArgumentNullException(nameof(facet));
            Qualifiers = qualifier;
            TypeHint = typeHint;
            PropertyPath = propertyPath ?? throw new ArgumentNullException(nameof(propertyPath));
        }

        /// <summary>
        /// Constructor overload with optional typeHint (defaults to null)
        /// </summary>
        public XPropRef(
            PathType pathType,
            string[] hierarchy,
            string facet,
            string[] qualifier,
            string propertyPath)
            : this(pathType, hierarchy, facet, qualifier, null, propertyPath)
        {
        }

        /// <summary>
        /// Reconstructs the XProp reference string (normalized form)
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();

            // Path prefix and hierarchy
            switch (PathType)
            {
                case PathType.ContextRelative:
                    sb.Append("./");
                    if (Hierarchy.Length > 0)
                    {
                        sb.Append(EncodeHierarchy());
                    }
                    break;
                case PathType.ScopeRelative:
                    sb.Append("#/");
                    if (Hierarchy.Length > 0)
                    {
                        sb.Append(EncodeHierarchy());
                    }
                    break;
                case PathType.Absolute:
                    sb.Append("/");
                    sb.Append(EncodeHierarchy());
                    break;
            }

            // Facet delimiter and facet
            sb.Append("::");
            sb.Append(Facet);

            // Qualifier
            if (Qualifiers != null && Qualifiers.Length > 0)
            {
                sb.Append('(');
                for (int i = 0; i < Qualifiers.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(EncodeQualifierParam(Qualifiers[i]));
                }
                sb.Append(')');
            }

            // Type hint
            if (!string.IsNullOrEmpty(TypeHint))
            {
                sb.Append('<');
                sb.Append(TypeHint);
                sb.Append('>');
            }

            // Property
            sb.Append(':');
            sb.Append(PropertyPath);

            return sb.ToString();
        }

        private string EncodeHierarchy()
        {
            var parts = new List<string>();
            foreach (var seg in Hierarchy)
            {
                if (IsUnquotedSegment(seg))
                {
                    parts.Add(seg);
                }
                else
                {
                    // Need to quote - apply escapes
                    var escaped = seg
                        .Replace("~", "~0")
                        .Replace("]", "~1")
                        .Replace("[", "~2");
                    parts.Add($"[{escaped}]");
                }
            }
            return string.Join("/", parts);
        }

        private static bool IsUnquotedSegment(string seg)
        {
            if (string.IsNullOrEmpty(seg)) return false;
            foreach (char c in seg)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }
            return true;
        }

        private static string EncodeQualifierParam(string param)
        {
            // Escape delimiters
            var result = new StringBuilder();
            foreach (char c in param)
            {
                switch (c)
                {
                    case ',':
                        result.Append("~3");
                        break;
                    case '(':
                        result.Append("~4");
                        break;
                    case ')':
                        result.Append("~5");
                        break;
                    default:
                        result.Append(c);
                        break;
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// Gets the first qualifier parameter, or null if no qualifiers
        /// </summary>
        public readonly string PrimaryQualifier => Qualifiers != null && Qualifiers.Length > 0 ? Qualifiers[0] : null;

        /// <summary>
        /// Tries to parse the primary qualifier as an integer (e.g., material slot index)
        /// </summary>
        public readonly bool TryGetIntQualifier(out int value)
        {
            value = 0;
            string primary = PrimaryQualifier;
            if (primary == null) return false;
            return int.TryParse(primary, out value);
        }

        /// <summary>
        /// Creates an XPropRef from a path string and components
        /// </summary>
        /// <param name="pathType">The type of path (scope-relative, context-relative, etc.)</param>
        /// <param name="path">The hierarchy path as a string (e.g., "Armature/Hips/Spine"). Empty for self-reference.</param>
        /// <param name="facet">The facet name (e.g., "mesh", "material")</param>
        /// <param name="qualifiers">Optional qualifier parameters</param>
        /// <param name="typeHint">Optional type hint</param>
        /// <param name="propertyPath">The property path (defaults to empty string)</param>
        /// <returns>A new XPropRef instance</returns>
        public static XPropRef FromPath(
            PathType pathType,
            string path,
            string facet,
            string[] qualifiers = null,
            string typeHint = null,
            string propertyPath = "")
        {
            if (string.IsNullOrEmpty(facet))
            {
                throw new ArgumentException("Facet cannot be null or empty.", nameof(facet));
            }

            // Parse path into hierarchy segments
            string[] hierarchy;
            if (string.IsNullOrEmpty(path))
            {
                hierarchy = Array.Empty<string>();
            }
            else
            {
                hierarchy = path.Split('/');
            }

            return new XPropRef(
                pathType,
                hierarchy,
                facet,
                qualifiers,
                typeHint,
                propertyPath ?? string.Empty
            );
        }

        /// <summary>
        /// Creates an XPropRef from hierarchy segments and components
        /// </summary>
        public static XPropRef Create(
            PathType pathType,
            string[] hierarchy,
            string facet,
            string[] qualifiers = null,
            string typeHint = null,
            string propertyPath = "")
        {
            if (string.IsNullOrEmpty(facet))
            {
                throw new ArgumentException("Facet cannot be null or empty.", nameof(facet));
            }

            return new XPropRef(
                pathType,
                hierarchy,
                facet,
                qualifiers,
                typeHint,
                propertyPath ?? string.Empty
            );
        }

        public static XPropRef Parse(string xpropString)
        {
            return XPropParser.Parse(xpropString);
        }

        /// <summary>
        /// Checks equality of two XPropRef instances, comparing array elements
        /// </summary>
        public bool Equals(XPropRef other)
        {
            return PathType == other.PathType &&
                   ArrayEquals(Hierarchy, other.Hierarchy) &&
                   Facet == other.Facet &&
                   ArrayEquals(Qualifiers, other.Qualifiers) &&
                   TypeHint == other.TypeHint &&
                   PropertyPath == other.PropertyPath;
        }

        public override bool Equals(object obj)
        {
            return obj is XPropRef other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public static bool operator ==(XPropRef left, XPropRef right) => left.Equals(right);
        public static bool operator !=(XPropRef left, XPropRef right) => !left.Equals(right);

        private static bool ArrayEquals(string[] a, string[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
