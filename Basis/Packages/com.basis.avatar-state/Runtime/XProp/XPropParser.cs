using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Jinxxy.Item.XProp
{
    public enum XPropErrorCode
    {
        None,

        // Parsing errors
        ParseError,              // General syntax error
        LimitExceeded,           // Exceeded length/depth/count limits

        // Validation errors (used during facet validation)
        FacetUnknown,            // Facet not in allowed list
        QualifierInvalid,        // Qualifier failed validation

        // Runtime errors (for use by implementations, not parser)
        NodeNotFound,            // Referenced node doesn't exist
        PropertyNotFound,        // Referenced property doesn't exist
        TypeMismatch,            // Value type doesn't match expected type
    }

    public class XPropException : Exception
    {
        public XPropErrorCode ErrorCode { get; }

        public XPropException(XPropErrorCode code, string message)
            : base(message)
        {
            ErrorCode = code;
        }
    }

    /// <summary>
    /// Delegate for validating facets during parsing
    /// </summary>
    /// <param name="facet">The facet name to validate</param>
    /// <param name="qualifier">The qualifier parameters (if any)</param>
    /// <param name="typeHint">The type hint (if any)</param>
    /// <returns>True if the facet is valid, false otherwise</returns>
    public delegate bool FacetValidator(string facet, string[] qualifier, string typeHint, string propertyPath);

    /// <summary>
    /// Options for facet validation
    /// </summary>
    public class FacetValidationOptions
    {
        /// <summary>
        /// Per-facet validators. Key is facet name, value is validator function.
        /// </summary>
        public Dictionary<string, FacetValidator> FacetValidators = new();

        /// <summary>
        /// If true, unknown facets will be allowed even if AllowedFacets is set
        /// </summary>
        public bool AllowUnknownFacets = true;

        /// <summary>
        /// Default validation options - no restrictions, syntax-only validation
        /// </summary>
        public static FacetValidationOptions Default = new();

        private static readonly FacetValidator _alwaysAllow = static (_, _, _, _) => true;
        /// <summary>
        /// Adds a facet with its validator
        /// </summary>
        public void AddFacet(string facet, FacetValidator validator = null)
        {
            if (string.IsNullOrEmpty(facet))
                throw new ArgumentException("Facet cannot be null or empty.", nameof(facet));

            // Add validator if provided
            FacetValidators ??= new Dictionary<string, FacetValidator>();
            FacetValidators[facet] = validator ?? _alwaysAllow;

        }

        /// <summary>
        /// Adds multiple facets without validators
        /// </summary>
        public void AddFacets(params (string, FacetValidator)[] facets)
        {
            foreach (var (facet, validator) in facets)
                AddFacet(facet, validator);
        }

        public void RemoveFacet(string facet)
        {
            FacetValidators?.Remove(facet);
        }
    }

    /// <summary>
    /// Parser for XProp v0.1.0 specification
    /// </summary>
    public static class XPropParser
    {
        private static readonly Regex Tokenizer = new(@"^(?:(?<ctx_rel>\.\/(?<ctx_path>(?:[^:\[\]]+|\[[^\]]*\])*))|(?<scope_rel>#\/(?<scope_path>(?:[^:\[\]]+|\[[^\]]*\])*))|(?<abs>\/(?<abs_path>(?:[^:\[\]]+|\[[^\]]*\])+)))::(?<facet>[a-z0-9_]+)(?:\((?<qualifier>[^)]*)\))?(?:<(?<type>[A-Za-z][A-Za-z0-9_]*)>)?:(?<property>.+)$",
            RegexOptions.Compiled);

        // Limits from spec
        public const int MAX_LENGTH = 1024;
        public const int MAX_PATH_SEGMENTS = 32;
        public const int MAX_SEGMENT_LENGTH = 128;
        public const int MAX_PROPERTY_DEPTH = 16;
        public const int MAX_ARRAY_INDEX = 65535;
        public const int MAX_QUALIFIER_PARAMS = 8;
        public const int MAX_QUALIFIER_PARAM_LENGTH = 64;

        // Character class checks
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLowerAlphaNumUnderscore(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAlphaNumUnderscore(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAlpha(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAlphaOrUnderscore(char c) => IsAlpha(c) || c == '_';
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPropertyChar(char c) => IsAlphaNumUnderscore(c) || c == '-';
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsWhitespace(string s)
        {
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                    return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MatchesAll(string s, Func<char, bool> predicate)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (!predicate(c)) return false;
            return true;
        }

        // Escape lookup tables: index = char - '0', value = decoded char (or 0 if invalid)
        private static readonly char[] PathEscapes = { '~', ']', '[' };           // ~0 ~1 ~2
        private static readonly char[] QualifierEscapes = { ',', '(', ')' };      // ~3 ~4 ~5

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDecodeEscape(char code, char[] table, int offset, out char decoded)
        {
            int idx = code - '0' - offset;
            if (idx >= 0 && idx < table.Length)
            {
                decoded = table[idx];
                return true;
            }
            decoded = default;
            return false;
        }

        /// <summary>
        /// Parse an XProp reference string
        /// </summary>
        /// <param name="enableDetailedErrors">When true, falls back to manual slicing to produce more specific parse errors</param>
        public static XPropRef Parse(string input, bool enableDetailedErrors = true)
        {
            if (string.IsNullOrEmpty(input))
                throw new XPropException(XPropErrorCode.ParseError, "Input cannot be null or empty");

            if (input.Length > MAX_LENGTH)
                throw new XPropException(XPropErrorCode.LimitExceeded,
                    $"Reference exceeds maximum length of {MAX_LENGTH}");

            // Quick prefix and delimiter checks to keep error messages consistent
            PathType pathType;
            int prefixLength;
            if (input.StartsWith("./"))
            {
                pathType = PathType.ContextRelative;
                prefixLength = 2;
            }
            else if (input.StartsWith("#/"))
            {
                pathType = PathType.ScopeRelative;
                prefixLength = 2;
            }
            else if (input.StartsWith("/"))
            {
                pathType = PathType.Absolute;
                prefixLength = 1;
            }
            else
            {
                throw new XPropException(XPropErrorCode.ParseError,
                    "Invalid path prefix: must start with ./, #/, or /");
            }

            int facetDelimiter = FindFacetDelimiter(input, prefixLength);
            if (facetDelimiter == -1)
                throw new XPropException(XPropErrorCode.ParseError, "Missing :: delimiter");

            int propertyDelimiter = FindPropertyDelimiter(input, facetDelimiter + 2);
            if (propertyDelimiter == -1)
                throw new XPropException(XPropErrorCode.ParseError, "Missing : delimiter");

            string pathStr;
            string facetBlock;
            string propertyPath;

            // Regex-based tokenization with optional fallback to manual slicing to preserve detailed errors
            var match = Tokenizer.Match(input);
            if (match.Success)
            {
                pathStr = pathType switch
                {
                    PathType.ContextRelative => match.Groups["ctx_path"].Value,
                    PathType.ScopeRelative => match.Groups["scope_path"].Value,
                    _ => match.Groups["abs_path"].Value
                };

                facetBlock = match.Groups["facet"].Value;
                if (match.Groups["qualifier"].Success)
                    facetBlock += "(" + match.Groups["qualifier"].Value + ")";
                if (match.Groups["type"].Success)
                    facetBlock += "<" + match.Groups["type"].Value + ">";

                propertyPath = match.Groups["property"].Value;
            }
            else
            {
                if (!enableDetailedErrors)
                    throw new XPropException(XPropErrorCode.ParseError, "Invalid XProp format");
                // Fallback: slice using known delimiters to allow Parse* helpers to emit specific errors
                pathStr = input.Substring(prefixLength, facetDelimiter - prefixLength);
                facetBlock = input.Substring(facetDelimiter + 2, propertyDelimiter - facetDelimiter - 2);
                propertyPath = input.Substring(propertyDelimiter + 1);
            }

            string[] hierarchy;
            if (string.IsNullOrEmpty(pathStr))
            {
                if (pathType == PathType.Absolute)
                {
                    throw new XPropException(XPropErrorCode.ParseError,
                        "Absolute path requires at least one segment");
                }
                hierarchy = Array.Empty<string>();
            }
            else
            {
                hierarchy = ParseHierarchy(pathStr);
            }

            if (hierarchy.Length > MAX_PATH_SEGMENTS)
                throw new XPropException(XPropErrorCode.LimitExceeded,
                    $"Path exceeds maximum depth of {MAX_PATH_SEGMENTS}");

            ParseFacetPart(facetBlock, out string facet, out string[] qualifier, out string typeHint);

            if (string.IsNullOrEmpty(propertyPath))
                throw new XPropException(XPropErrorCode.ParseError, "Empty property path");

            ValidatePropertyPath(propertyPath);

            return new XPropRef(pathType, hierarchy, facet, qualifier, typeHint, propertyPath);
        }

        /// <summary>
        /// Tries to parse an XProp reference, returning false on error
        /// </summary>
        public static bool TryParse(string input, out XPropRef result, out string errorMessage)
        {
            try
            {
                result = Parse(input);
                errorMessage = null;
                return true;
            }
            catch (XPropException ex)
            {
                result = default;
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Check if a string is a valid XProp reference
        /// </summary>
        public static bool IsValid(string input)
        {
            return TryParse(input, out _, out _);
        }

        /// <summary>
        /// Parse an XProp reference with facet validation
        /// </summary>
        /// <param name="input">The XProp reference string to parse</param>
        /// <param name="validationOptions">Options for validating the facet</param>
        /// <returns>Parsed XPropRef if valid</returns>
        /// <exception cref="XPropException">Thrown if parsing fails or facet validation fails</exception>
        public static XPropRef ParseWithValidation(string input, FacetValidationOptions validationOptions)
        {
            if (validationOptions == null)
                throw new ArgumentNullException(nameof(validationOptions));

            // Parse normally first
            var result = Parse(input);

            // Validate the facet
            ValidateFacet(result.Facet, result.Qualifiers, result.TypeHint, result.PropertyPath, validationOptions);

            return result;
        }

        /// <summary>
        /// Tries to parse an XProp reference with facet validation
        /// </summary>
        public static bool TryParseWithValidation(string input, FacetValidationOptions validationOptions,
            out XPropRef result, out string errorMessage)
        {
            try
            {
                result = ParseWithValidation(input, validationOptions);
                errorMessage = null;
                return true;
            }
            catch (XPropException ex)
            {
                result = default;
                errorMessage = ex.Message;
                return false;
            }
            catch (ArgumentNullException ex)
            {
                result = default;
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Parse an XProp reference with a simple set of allowed facets
        /// </summary>
        /// <param name="input">The XProp reference string to parse</param>
        /// <param name="allowedFacets">Array of allowed facet names</param>
        /// <returns>Parsed XPropRef if valid</returns>
        public static XPropRef ParseWithAllowedFacets(string input, params string[] allowedFacets)
        {
            if (allowedFacets == null || allowedFacets.Length == 0)
                throw new ArgumentException("Must provide at least one allowed facet", nameof(allowedFacets));

            var options = new FacetValidationOptions
            {
                AllowUnknownFacets = false
            };

            foreach (var facet in allowedFacets)
                options.AddFacet(facet, null);

            return ParseWithValidation(input, options);
        }

        /// <summary>
        /// Parse an XProp reference with per-facet validators
        /// </summary>
        /// <param name="input">The XProp reference string to parse</param>
        /// <param name="facetValidators">Dictionary mapping facet names to their validators</param>
        /// <returns>Parsed XPropRef if valid</returns>
        public static XPropRef ParseWithValidators(string input, Dictionary<string, FacetValidator> facetValidators)
        {
            if (facetValidators == null)
                throw new ArgumentNullException(nameof(facetValidators));

            var options = new FacetValidationOptions
            {
                FacetValidators = facetValidators,
                AllowUnknownFacets = false
            };

            return ParseWithValidation(input, options);
        }

        /// <summary>
        /// Validates a facet against the provided validation options
        /// </summary>
        /// <param name="facet">The facet name</param>
        /// <param name="qualifier">The qualifier parameters</param>
        /// <param name="typeHint">The type hint</param>
        /// <param name="propertyPath">The property path</param>
        /// <param name="options">Validation options</param>
        /// <exception cref="XPropException">Thrown if validation fails</exception>
        public static void ValidateFacet(string facet, string[] qualifier, string typeHint, string propertyPath, FacetValidationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            // Check if facet has a registered validator
            FacetValidator validator = null;
            bool hasValidator = options.FacetValidators != null && options.FacetValidators.TryGetValue(facet, out validator);

            // If unknown facets are not allowed and this facet has no validator, reject it
            if (!hasValidator && !options.AllowUnknownFacets)
            {
                string allowedList = options.FacetValidators != null
                    ? string.Join(", ", options.FacetValidators.Keys)
                    : "(none)";
                throw new XPropException(XPropErrorCode.FacetUnknown,
                    $"Unknown facet '{facet}'. Allowed facets: {allowedList}");
            }

            // Run validator if registered
            if (hasValidator && validator != null)
            {
                if (!validator(facet, qualifier, typeHint, propertyPath))
                {
                    throw new XPropException(XPropErrorCode.QualifierInvalid,
                        $"Facet validation failed for '{facet}' with qualifier [{string.Join(", ", qualifier ?? Array.Empty<string>())}]");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindFacetDelimiter(string s, int start)
        {
            int i = start;
            while (i < s.Length)
            {
                if (s[i] == '[')
                {
                    // Skip to closing bracket, handling escapes
                    i++;
                    while (i < s.Length && s[i] != ']')
                    {
                        if (s[i] == '~')
                        {
                            if (i + 1 >= s.Length || !IsDigit(s[i + 1]))
                                throw new XPropException(XPropErrorCode.ParseError, "Incomplete escape sequence");
                            i += 2; // Skip escape sequence
                        }
                        else
                            i++;
                    }
                    if (i >= s.Length)
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Unclosed bracket in path segment");
                    i++; // Skip the ]
                }
                else if (i + 1 < s.Length && s[i] == ':' && s[i + 1] == ':')
                {
                    return i;
                }
                else
                {
                    i++;
                }
            }
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindPropertyDelimiter(string s, int start)
        {
            int i = start;
            while (i < s.Length)
            {
                if (s[i] == '(')
                {
                    // Skip to closing paren
                    int depth = 1;
                    i++;
                    while (i < s.Length && depth > 0)
                    {
                        if (s[i] == '(')
                            depth++;
                        else if (s[i] == ')')
                            depth--;
                        i++;
                    }
                }
                else if (s[i] == '<')
                {
                    // Skip to closing angle bracket
                    i++;
                    while (i < s.Length && s[i] != '>')
                        i++;
                    if (i < s.Length)
                        i++; // Skip the >
                }
                else if (s[i] == ':')
                {
                    return i;
                }
                else
                {
                    i++;
                }
            }
            return -1;
        }

        private static string[] ParseHierarchy(string pathStr)
        {
            var segments = new List<string>();
            int i = 0;

            while (i < pathStr.Length)
            {
                string segment;

                if (pathStr[i] == '[')
                {
                    // Quoted segment
                    i++;
                    var segChars = new StringBuilder();
                    while (i < pathStr.Length && pathStr[i] != ']')
                    {
                        if (pathStr[i] == '~')
                        {
                            if (i + 1 >= pathStr.Length)
                                throw new XPropException(XPropErrorCode.ParseError, "Incomplete escape sequence");

                            if (!TryDecodeEscape(pathStr[i + 1], PathEscapes, 0, out char decoded))
                                throw new XPropException(XPropErrorCode.ParseError, $"Unknown escape sequence in path: ~{pathStr[i + 1]}");

                            segChars.Append(decoded);
                            i += 2;
                        }
                        else
                        {
                            segChars.Append(pathStr[i++]);
                        }
                    }

                    if (i >= pathStr.Length)
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Unclosed bracket in path segment");

                    segment = segChars.ToString();
                    i++; // Skip ]
                }
                else
                {
                    // Unquoted segment
                    int j = i;
                    while (j < pathStr.Length && pathStr[j] != '/' && pathStr[j] != '[')
                        j++;
                    segment = pathStr.Substring(i, j - i);
                    i = j;

                    if (string.IsNullOrEmpty(segment))
                        throw new XPropException(XPropErrorCode.ParseError, "Empty path segment");

                    // Check for parent traversal before character validation
                    if (segment == "..")
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Parent traversal (..) is not permitted");

                    // Validate unquoted segment characters
                    if (!MatchesAll(segment, IsAlphaNumUnderscore))
                        throw new XPropException(XPropErrorCode.ParseError,
                            $"Invalid characters in unquoted segment: {segment}");
                }

                if (segment.Length > MAX_SEGMENT_LENGTH)
                    throw new XPropException(XPropErrorCode.LimitExceeded,
                        $"Segment exceeds maximum length of {MAX_SEGMENT_LENGTH}");

                segments.Add(segment);

                // Skip separator
                if (i < pathStr.Length)
                {
                    if (pathStr[i] == '/')
                    {
                        i++;
                        if (i >= pathStr.Length)
                            throw new XPropException(XPropErrorCode.ParseError,
                                "Trailing slash in path");
                    }
                }
            }

            return segments.ToArray();
        }

        private static void ParseFacetPart(string facetStr, out string facet, out string[] qualifier, out string typeHint)
        {
            typeHint = null;
            qualifier = null;

            // Extract type hint <Type> from end
            if (TryExtractDelimited(ref facetStr, '<', '>', out string typeContent))
            {
                if (string.IsNullOrEmpty(typeContent))
                    throw new XPropException(XPropErrorCode.ParseError, "Empty type hint");
                if (!IsAlpha(typeContent[0]) || !MatchesAll(typeContent, IsAlphaNumUnderscore))
                    throw new XPropException(XPropErrorCode.ParseError, $"Invalid type name '{typeContent}': must start with letter");
                typeHint = typeContent;
            }

            // Extract qualifier (params)
            if (TryExtractDelimited(ref facetStr, '(', ')', out string qualContent))
            {
                qualifier = string.IsNullOrEmpty(qualContent) ? Array.Empty<string>() : ParseAndValidateQualifier(qualContent);
            }

            // Validate facet name
            if (string.IsNullOrEmpty(facetStr))
                throw new XPropException(XPropErrorCode.ParseError, "Empty facet name");
            if (!MatchesAll(facetStr, IsLowerAlphaNumUnderscore))
                throw new XPropException(XPropErrorCode.ParseError, $"Invalid facet name '{facetStr}': must be lowercase ASCII letters, digits, or underscores");

            facet = facetStr;
        }

        private static bool TryExtractDelimited(ref string str, char open, char close, out string content)
        {
            int closeIdx = str.IndexOf(close);
            if (closeIdx == -1) { content = null; return false; }

            int openIdx = str.LastIndexOf(open, closeIdx);
            if (openIdx == -1)
                throw new XPropException(XPropErrorCode.ParseError, $"Malformed: found '{close}' without '{open}'");

            content = str.Substring(openIdx + 1, closeIdx - openIdx - 1);
            str = str[..openIdx];
            return true;
        }

        private static string[] ParseAndValidateQualifier(string content)
        {
            var rawParams = ParseQualifierParams(content);

            if (rawParams.Length > MAX_QUALIFIER_PARAMS)
                throw new XPropException(XPropErrorCode.LimitExceeded, $"Qualifier exceeds maximum of {MAX_QUALIFIER_PARAMS} params");

            // Trim and validate each parameter
            var result = new string[rawParams.Length];
            for (int i = 0; i < rawParams.Length; i++)
            {
                var raw = rawParams[i];

                // Check for excessive leading whitespace (more than one space)
                if (raw.Length > 0 && char.IsWhiteSpace(raw[0]))
                {
                    // Count leading whitespace
                    int wsCount = 0;
                    for (int j = 0; j < raw.Length && char.IsWhiteSpace(raw[j]); j++)
                        wsCount++;

                    if (wsCount > 1)
                        throw new XPropException(XPropErrorCode.ParseError, "Whitespace not permitted in qualifier parameters");
                }

                // Check for excessive trailing whitespace (more than one space)
                if (raw.Length > 0 && char.IsWhiteSpace(raw[raw.Length - 1]))
                {
                    // Count trailing whitespace
                    int wsCount = 0;
                    for (int j = raw.Length - 1; j >= 0 && char.IsWhiteSpace(raw[j]); j--)
                        wsCount++;

                    if (wsCount > 1)
                        throw new XPropException(XPropErrorCode.ParseError, "Whitespace not permitted in qualifier parameters");
                }

                var trimmed = raw.Trim();

                if (trimmed.Length > MAX_QUALIFIER_PARAM_LENGTH)
                    throw new XPropException(XPropErrorCode.LimitExceeded, $"Qualifier param exceeds maximum length of {MAX_QUALIFIER_PARAM_LENGTH}");

                if (ContainsWhitespace(trimmed))
                    throw new XPropException(XPropErrorCode.ParseError, "Whitespace not permitted in qualifier parameters");

                result[i] = trimmed;
            }

            return result;
        }

        private static string[] ParseQualifierParams(string content)
        {
            var result = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '~' && i + 1 < content.Length)
                {
                    if (TryDecodeEscape(content[i + 1], QualifierEscapes, 3, out char decoded))
                    {
                        current.Append(decoded);
                        i++; // Skip next char (escape code)
                    }
                    else
                    {
                        // Lenient: treat unknown as literal
                        current.Append(c);
                    }
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());
            return result.ToArray();
        }

        private static void ValidatePropertyPath(string propPath)
        {
            // Split into segments by dot, but handle array indices
            var segments = new List<string>();
            var currentSegment = new StringBuilder();
            int i = 0;
            int depth = 0;

            while (i < propPath.Length)
            {
                if (propPath[i] == '.')
                {
                    if (currentSegment.Length > 0)
                    {
                        string seg = currentSegment.ToString();
                        ValidatePropertySegment(seg);
                        segments.Add(seg);
                        depth++;
                        currentSegment.Clear();
                    }
                    else
                    {
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Empty property segment (consecutive dots)");
                    }
                    i++;
                }
                else if (propPath[i] == '[')
                {
                    // Array index - must follow a segment name
                    if (currentSegment.Length == 0)
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Array index without preceding property name");

                    // Find closing bracket
                    int j = i + 1;
                    while (j < propPath.Length && propPath[j] != ']')
                        j++;

                    if (j >= propPath.Length)
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Unclosed array index bracket");

                    string indexStr = propPath.Substring(i + 1, j - i - 1);
                    ValidateArrayIndex(indexStr);

                    // Check for multidimensional access
                    if (j + 1 < propPath.Length && propPath[j + 1] == '[')
                        throw new XPropException(XPropErrorCode.ParseError,
                            "Multidimensional array access is not supported");

                    currentSegment.Append('[');
                    currentSegment.Append(indexStr);
                    currentSegment.Append(']');
                    i = j + 1;
                }
                else
                {
                    currentSegment.Append(propPath[i]);
                    i++;
                }
            }

            // Handle final segment
            if (currentSegment.Length > 0)
            {
                string seg = currentSegment.ToString();
                ValidatePropertySegment(seg);
                segments.Add(seg);
                depth++;
            }
            else
            {
                throw new XPropException(XPropErrorCode.ParseError, "Property path ends with dot");
            }

            if (depth == 0)
                throw new XPropException(XPropErrorCode.ParseError,
                    "Property path must contain at least one segment");

            if (depth > MAX_PROPERTY_DEPTH)
                throw new XPropException(XPropErrorCode.LimitExceeded,
                    $"Property path exceeds maximum depth of {MAX_PROPERTY_DEPTH}");
        }

        private static void ValidatePropertySegment(string seg)
        {
            // Remove array index if present
            string namePart = seg;
            if (seg.Contains("["))
            {
                namePart = seg.Substring(0, seg.IndexOf('['));
            }

            if (string.IsNullOrEmpty(namePart))
                throw new XPropException(XPropErrorCode.ParseError, "Empty property segment name");

            // v0.8.0: Property names must start with letter or underscore
            if (!IsAlphaOrUnderscore(namePart[0]))
                throw new XPropException(XPropErrorCode.ParseError,
                    $"Invalid property segment name '{namePart}': must start with letter or underscore");

            // v0.8.0: Property names can contain letters, digits, underscores, and hyphens
            for (int i = 1; i < namePart.Length; i++)
            {
                if (!IsPropertyChar(namePart[i]))
                    throw new XPropException(XPropErrorCode.ParseError,
                        $"Invalid property segment name '{namePart}': contains invalid character '{namePart[i]}'");
            }
        }

        private static void ValidateArrayIndex(string indexStr)
        {
            if (string.IsNullOrEmpty(indexStr))
                throw new XPropException(XPropErrorCode.ParseError, "Empty array index");

            if (!MatchesAll(indexStr, IsDigit))
                throw new XPropException(XPropErrorCode.ParseError,
                    $"Invalid array index (non-numeric): {indexStr}");

            // Check for leading zeros
            if (indexStr.Length > 1 && indexStr[0] == '0')
                throw new XPropException(XPropErrorCode.ParseError,
                    $"Leading zeros not permitted in array index: [{indexStr}]");

            int indexVal = int.Parse(indexStr);
            if (indexVal > MAX_ARRAY_INDEX)
                throw new XPropException(XPropErrorCode.LimitExceeded,
                    $"Array index {indexVal} exceeds maximum of {MAX_ARRAY_INDEX}");
        }
    }
}
