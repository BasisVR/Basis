using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [NoAutoStaticsCleanup]
    public sealed class BasisBlendshapeNameMatcher
    {
        static readonly HashSet<string> prefixTokens = new HashSet<string>(StringComparer.Ordinal) { "bs", "blendshape", "blendshapes", "shape", "arkit", "ft", "ue", "sr", "sranipal", "exp", "expression" };
        static readonly Dictionary<string, string[]> candidateKeys = new Dictionary<string, string[]>(StringComparer.Ordinal);
        static readonly List<string> scratchTokens = new List<string>(8);
        static readonly StringBuilder scratchBuilder = new StringBuilder(16);
        readonly Dictionary<string, int> exact = new Dictionary<string, int>(StringComparer.Ordinal), ordered = new Dictionary<string, int>(StringComparer.Ordinal), sorted = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<string> lastTokens = new List<string>();
        readonly List<int> tokenCounts = new List<int>();
        public readonly Mesh Mesh;
        static readonly ConditionalWeakTable<Mesh, BasisBlendshapeNameMatcher> cache = new ConditionalWeakTable<Mesh, BasisBlendshapeNameMatcher>();
        public static BasisBlendshapeNameMatcher For(Mesh mesh) => mesh == null ? new BasisBlendshapeNameMatcher(null) : cache.GetValue(mesh, key => new BasisBlendshapeNameMatcher(key));
        public BasisBlendshapeNameMatcher(Mesh mesh)
        {
            Mesh = mesh;
            int count = mesh != null ? mesh.blendShapeCount : 0;
            List<string> tokens = scratchTokens;
            for (int index = 0; index < count; index++)
            {
                string name = mesh.GetBlendShapeName(index);
                exact.TryAdd(name, index);
                Tokenize(name, tokens);
                lastTokens.Add(tokens.Count > 0 ? tokens[tokens.Count - 1] : string.Empty);
                tokenCounts.Add(tokens.Count);
                if (tokens.Count == 0) continue;
                ordered.TryAdd(string.Join("|", tokens), index);
                tokens.Sort(StringComparer.Ordinal);
                sorted.TryAdd(string.Join("|", tokens), index);
            }
        }
        public int Find(string candidate)
        {
            if (Mesh == null || string.IsNullOrEmpty(candidate)) return -1;
            if (exact.TryGetValue(candidate, out int index)) return index;
            string[] keys = KeysFor(candidate);
            if (keys == null) return -1;
            if (ordered.TryGetValue(keys[0], out index)) return index;
            return sorted.TryGetValue(keys[1], out index) ? index : -1;
        }
        static string[] KeysFor(string candidate)
        {
            if (candidateKeys.TryGetValue(candidate, out string[] keys)) return keys;
            List<string> tokens = scratchTokens;
            Tokenize(candidate, tokens);
            if (tokens.Count > 0)
            {
                string orderedKey = string.Join("|", tokens);
                tokens.Sort(StringComparer.Ordinal);
                keys = new[] { orderedKey, string.Join("|", tokens) };
            }
            candidateKeys[candidate] = keys;
            return keys;
        }
        public int FindFirst(string[] candidates)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                int found = Find(candidates[index]);
                if (found >= 0) return found;
            }
            return -1;
        }
        public int FindByLastToken(string[] keywords, int maxTokens)
        {
            for (int index = 0; index < lastTokens.Count; index++)
            {
                if (tokenCounts[index] > maxTokens) continue;
                string last = lastTokens[index];
                for (int keyword = 0; keyword < keywords.Length; keyword++) if (last == keywords[keyword]) return index;
            }
            return -1;
        }
        public static void Tokenize(string name, List<string> tokens)
        {
            tokens.Clear();
            if (string.IsNullOrEmpty(name)) return;
            int cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('/'));
            if (cut >= 0 && name.Length - cut - 1 >= 4) name = name.Substring(cut + 1);
            StringBuilder builder = scratchBuilder;
            builder.Clear();
            char previous = '\0';
            for (int index = 0; index < name.Length; index++)
            {
                char c = name[index];
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(builder, tokens);
                    previous = '\0';
                    continue;
                }
                bool boundary = builder.Length > 0 && ((char.IsUpper(c) && char.IsLower(previous)) || (char.IsDigit(c) != char.IsDigit(previous)) || (char.IsUpper(c) && char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1])));
                if (boundary) Flush(builder, tokens);
                builder.Append(char.ToLowerInvariant(c));
                previous = c;
            }
            Flush(builder, tokens);
            if (tokens.Count > 2 && tokens[0] == "ar" && tokens[1] == "kit") tokens.RemoveRange(0, 2);
            while (tokens.Count > 1 && prefixTokens.Contains(tokens[0])) tokens.RemoveAt(0);
        }
        static void Flush(StringBuilder builder, List<string> tokens)
        {
            if (builder.Length == 0) return;
            string token = builder.ToString();
            builder.Clear();
            if (token == "left") token = "l";
            else if (token == "right") token = "r";
            tokens.Add(token);
        }
    }
}
