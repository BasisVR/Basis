#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Basis.Shims.Editor
{
    /// <summary>
    /// One entry inside a collection initializer, kept with the trivia that surrounds it so a
    /// rewrite puts the comments back where the author left them.
    /// </summary>
    internal sealed class CilboxSourceEntry
    {
        /// <summary>Blank and comment-only lines above the entry, verbatim, including their indent.</summary>
        public List<string> LeadingLines = new List<string>();

        /// <summary>
        /// The entry's own indentation. Held per entry rather than per list because
        /// <c>commonWhiteListType</c> mixes tabs with lines that start at column zero, and a list
        /// that cannot be reproduced exactly is a list the window refuses to edit.
        /// </summary>
        public string Indent;

        /// <summary>The entry expression exactly as written.</summary>
        public string Expression;

        /// <summary>
        /// The rest of the entry's line after its comma — the gap plus a <c>// …</c> comment, kept
        /// verbatim because these files align those comments into a column and re-spacing them
        /// would turn one edit into a diff across the whole list. Null when there is no comment.
        /// </summary>
        public string TrailingComment;

        /// <summary>The trailing comment without its alignment padding, for display.</summary>
        public string TrailingCommentText => TrailingComment?.Trim();

        /// <summary>
        /// The value the compiler sees, for the expressions this file's authors actually use:
        /// a string literal, <c>nameof(A.B)</c>, <c>$"get_{nameof(A.B)}"</c> and the
        /// <c>typeof(T).GetProperty(nameof(T.P)).GetGetMethod().Name</c> spelling. Null when the
        /// expression is something else — those entries are shown but never edited.
        /// </summary>
        public string Resolved;

        /// <summary>True when the entry is a plain string literal, so it can be retyped safely.</summary>
        public bool IsPlainLiteral;
    }

    /// <summary>
    /// A parsed collection initializer, remembering where its body sits in the file so a rewrite
    /// can splice the new body back without touching a byte of anything else.
    /// </summary>
    internal sealed class CilboxSourceList
    {
        public string FieldName;
        public int BodyStart;
        public int BodyEnd;
        public string Indent = "\t\t\t";
        public string OpeningTrivia = "\n";
        public string ClosingIndent = "\t\t";
        public List<CilboxSourceEntry> Entries = new List<CilboxSourceEntry>();

        /// <summary>Comment or blank lines left dangling between the last entry and the closing brace.</summary>
        public List<string> TrailingLines = new List<string>();

        /// <summary>True for a <c>{ "a", "b" }</c> initializer written on one line.</summary>
        public bool SingleLine;

        /// <summary>
        /// False when re-rendering the untouched parse would not reproduce the original text. The
        /// window treats such a list as read-only rather than risk mangling a whitelist.
        /// </summary>
        public bool RoundTrips;
    }

    /// <summary>An entry of a <c>Dictionary&lt;Type, HashSet&lt;string&gt;&gt;</c> method whitelist.</summary>
    internal sealed class CilboxSourceMethodEntry
    {
        /// <summary>The <c>typeof(...)</c> argument as written, e.g. <c>UnityEngine.GameObject</c>.</summary>
        public string TypeExpression;

        /// <summary>The inner <c>new HashSet&lt;string&gt;{ … }</c> body, parsed as its own list.</summary>
        public CilboxSourceList Methods;

        /// <summary>Character range of this whole dictionary entry, for removal.</summary>
        public int EntryStart;
        public int EntryEnd;
    }

    /// <summary>
    /// Reads and rewrites the whitelist collections that live as <c>static readonly</c> fields in
    /// <c>CilboxBasisCommon</c> / <c>CilboxAvatarBasis</c> / <c>CilboxPropBasis</c> /
    /// <c>CilboxSceneBasis</c>.
    ///
    /// <para>These lists are the sandbox boundary, so the rules here are deliberately timid. The
    /// parser only ever understands the shapes those four files actually use; anything it cannot
    /// reproduce character-for-character is reported as read-only and left to be edited by hand.
    /// Every write re-renders the <em>unmodified</em> parse first and refuses to continue unless it
    /// matches the file on disk exactly, so a formatting style the parser does not know about
    /// causes an edit to be declined rather than a whitelist to be silently rewritten.</para>
    /// </summary>
    internal static class BasisCilboxPermissionSource
    {
        // ------------------------------------------------------------------ scanning

        /// <summary>
        /// Walks <paramref name="text"/> from <paramref name="start"/> and returns the index of the
        /// brace matching the one at <paramref name="start"/>, skipping over string literals,
        /// character literals and comments. Returns -1 if the file is unbalanced.
        /// </summary>
        private static int MatchBrace(string text, int start, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '/')
                    {
                        while (i < text.Length && text[i] != '\n') i++;
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        i = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (i < 0) return -1;
                        i++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    i = SkipString(text, i);
                    if (i < 0) return -1;
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipChar(text, i);
                    if (i < 0) return -1;
                    continue;
                }

                if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>Index of the closing quote of the literal opening at <paramref name="i"/>.</summary>
        private static int SkipString(string text, int i)
        {
            bool verbatim = i > 0 && text[i - 1] == '@';
            i++;
            while (i < text.Length)
            {
                char c = text[i];
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        return i;
                    }
                }
                else
                {
                    if (c == '\\') { i += 2; continue; }
                    if (c == '"') return i;
                }
                i++;
            }
            return -1;
        }

        private static int SkipChar(string text, int i)
        {
            i++;
            while (i < text.Length)
            {
                if (text[i] == '\\') { i += 2; continue; }
                if (text[i] == '\'') return i;
                i++;
            }
            return -1;
        }

        /// <summary>
        /// Splits an initializer body on its top-level commas. Commas inside nested braces,
        /// parentheses, brackets, strings or comments do not split.
        /// </summary>
        private static List<string> SplitTopLevel(string body)
        {
            var chunks = new List<string>();
            int depth = 0;
            int chunkStart = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];

                if (c == '/' && i + 1 < body.Length)
                {
                    if (body[i + 1] == '/')
                    {
                        while (i < body.Length && body[i] != '\n') i++;
                        continue;
                    }
                    if (body[i + 1] == '*')
                    {
                        int end = body.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (end < 0) break;
                        i = end + 1;
                        continue;
                    }
                }

                if (c == '"') { i = SkipString(body, i); if (i < 0) break; continue; }
                if (c == '\'') { i = SkipChar(body, i); if (i < 0) break; continue; }

                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    chunks.Add(body.Substring(chunkStart, i - chunkStart));
                    chunkStart = i + 1;
                }
            }

            string tail = body.Substring(chunkStart);
            if (tail.Trim().Length > 0) chunks.Add(tail);
            return chunks;
        }

        // ------------------------------------------------------------------ list parsing

        /// <summary>
        /// Parses the body of a collection initializer into entries. <paramref name="body"/> is the
        /// text strictly between the initializer's braces.
        /// </summary>
        private static bool TryParseBody(string body, CilboxSourceList list)
        {
            bool ok = body.IndexOf('\n') < 0
                ? TryParseSingleLineBody(body, list)
                : TryParseMultiLineBody(body, list);

            // A partial parse must not reach the UI as if it were the whole list — display comes
            // from the compiled assembly anyway, and the source is only consulted to allow editing.
            if (!ok)
            {
                list.Entries.Clear();
                list.TrailingLines.Clear();
            }
            return ok;
        }

        /// <summary>Handles <c>{ "a", "b" }</c> written on a single line.</summary>
        private static bool TryParseSingleLineBody(string body, CilboxSourceList list)
        {
            list.SingleLine = true;

            List<string> chunks = SplitTopLevel(body);
            if (chunks.Count == 0) return false;

            foreach (string chunk in chunks)
            {
                string expression = chunk.Trim();
                if (expression.Length == 0) return false;
                if (expression.StartsWith("//", StringComparison.Ordinal)) return false;

                var entry = new CilboxSourceEntry { Expression = expression };
                Resolve(entry);
                list.Entries.Add(entry);
            }

            int lead = 0;
            while (lead < body.Length && char.IsWhiteSpace(body[lead])) lead++;
            list.OpeningTrivia = body.Substring(0, lead);

            int tail = body.Length;
            while (tail > lead && char.IsWhiteSpace(body[tail - 1])) tail--;
            list.ClosingIndent = body.Substring(tail);

            list.Indent = string.Empty;
            return true;
        }

        private static bool TryParseMultiLineBody(string body, CilboxSourceList list)
        {
            int firstNewline = body.IndexOf('\n');
            list.OpeningTrivia = body.Substring(0, firstNewline + 1);

            int lastNewline = body.LastIndexOf('\n');
            list.ClosingIndent = body.Substring(lastNewline + 1);
            if (list.ClosingIndent.Trim().Length != 0) return false;

            string inner = body.Substring(firstNewline + 1, lastNewline - firstNewline);
            List<string> chunks = SplitTopLevel(inner);

            CilboxSourceEntry previous = null;

            foreach (string chunk in chunks)
            {
                var entry = new CilboxSourceEntry();
                string rest = chunk;

                // Only a chunk that follows a comma can open mid-line, so only then can its first
                // line be the previous entry's trailing comment. The first chunk always begins at
                // the start of a line, and its comment belongs to the entry below it.
                if (previous != null)
                {
                    int lineEnd = rest.IndexOf('\n');
                    string head = lineEnd >= 0 ? rest.Substring(0, lineEnd) : rest;
                    string headTrim = head.Trim();

                    if (headTrim.StartsWith("//", StringComparison.Ordinal))
                    {
                        previous.TrailingComment = head;
                        rest = lineEnd >= 0 ? rest.Substring(lineEnd + 1) : string.Empty;
                    }
                    else if (headTrim.Length != 0)
                    {
                        // Two entries share a line — a shape this renderer does not reproduce.
                        return false;
                    }
                    else if (lineEnd >= 0)
                    {
                        rest = rest.Substring(lineEnd + 1);
                    }
                }

                // Blank and comment-only lines above the entry stay attached to it.
                var leading = new List<string>();
                while (true)
                {
                    int nl = rest.IndexOf('\n');
                    if (nl < 0) break;
                    string line = rest.Substring(0, nl);
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        leading.Add(line);
                        rest = rest.Substring(nl + 1);
                        continue;
                    }
                    break;
                }

                // The text after the final comma is not an entry — it is whatever the author left
                // between the last one and the closing brace.
                if (rest.Trim().Length == 0)
                {
                    list.TrailingLines.AddRange(leading);
                    if (rest.Length != 0) return false;
                    continue;
                }

                entry.LeadingLines = leading;

                int indentEnd = 0;
                while (indentEnd < rest.Length && (rest[indentEnd] == ' ' || rest[indentEnd] == '\t')) indentEnd++;
                entry.Indent = rest.Substring(0, indentEnd);

                entry.Expression = rest.Trim();
                if (entry.Expression.Contains("\n")) return false;

                Resolve(entry);
                list.Entries.Add(entry);
                previous = entry;
            }

            // New entries copy the first entry's indentation.
            list.Indent = list.Entries.Count > 0 ? list.Entries[0].Indent : "\t\t\t";
            return list.Entries.Count > 0;
        }

        /// <summary>Fills in <see cref="CilboxSourceEntry.Resolved"/> for the shapes these files use.</summary>
        private static void Resolve(CilboxSourceEntry entry)
        {
            string e = entry.Expression;

            if (e.Length >= 2 && e[0] == '"' && e[e.Length - 1] == '"' && !e.Substring(1, e.Length - 2).Contains("\""))
            {
                entry.Resolved = Unescape(e.Substring(1, e.Length - 2));
                entry.IsPlainLiteral = true;
                return;
            }

            // nameof(A.B.C) -> C
            if (e.StartsWith("nameof(", StringComparison.Ordinal) && e.EndsWith(")", StringComparison.Ordinal))
            {
                entry.Resolved = LastSegment(e.Substring(7, e.Length - 8));
                return;
            }

            // $"get_{nameof(A.B.C)}" -> get_C
            if (e.StartsWith("$\"", StringComparison.Ordinal) && e.EndsWith("\"", StringComparison.Ordinal))
            {
                string body = e.Substring(2, e.Length - 3);
                int brace = body.IndexOf('{');
                if (brace > 0 && body.EndsWith("}", StringComparison.Ordinal))
                {
                    string prefix = body.Substring(0, brace);
                    string hole = body.Substring(brace + 1, body.Length - brace - 2).Trim();
                    if (hole.StartsWith("nameof(", StringComparison.Ordinal) && hole.EndsWith(")", StringComparison.Ordinal))
                    {
                        entry.Resolved = prefix + LastSegment(hole.Substring(7, hole.Length - 8));
                    }
                }
                return;
            }

            // typeof(T).GetProperty(nameof(T.P)).GetGetMethod().Name -> get_P
            int propIndex = e.IndexOf(".GetProperty(nameof(", StringComparison.Ordinal);
            if (propIndex > 0 && e.EndsWith(".GetGetMethod().Name", StringComparison.Ordinal))
            {
                int argStart = propIndex + ".GetProperty(nameof(".Length;
                int argEnd = e.IndexOf(')', argStart);
                if (argEnd > argStart)
                {
                    entry.Resolved = "get_" + LastSegment(e.Substring(argStart, argEnd - argStart));
                }
            }
        }

        private static string LastSegment(string dotted)
        {
            dotted = dotted.Trim();
            int dot = dotted.LastIndexOf('.');
            return dot >= 0 ? dotted.Substring(dot + 1) : dotted;
        }

        private static string Unescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ------------------------------------------------------------------ rendering

        private static string Render(CilboxSourceList list)
        {
            var sb = new StringBuilder();

            if (list.SingleLine)
            {
                sb.Append(list.OpeningTrivia);
                for (int i = 0; i < list.Entries.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(list.Entries[i].Expression);
                }
                sb.Append(list.ClosingIndent);
                return sb.ToString();
            }

            sb.Append(list.OpeningTrivia);
            foreach (CilboxSourceEntry entry in list.Entries)
            {
                foreach (string line in entry.LeadingLines)
                {
                    sb.Append(line).Append('\n');
                }
                sb.Append(entry.Indent ?? list.Indent).Append(entry.Expression).Append(',');
                if (!string.IsNullOrEmpty(entry.TrailingComment))
                {
                    sb.Append(entry.TrailingComment);
                }
                sb.Append('\n');
            }
            foreach (string line in list.TrailingLines)
            {
                sb.Append(line).Append('\n');
            }
            sb.Append(list.ClosingIndent);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ locating fields

        /// <summary>
        /// Finds the initializer body of <paramref name="fieldName"/> and parses it. Returns null
        /// when the field is missing or its formatting is one this parser will not rewrite.
        /// </summary>
        public static CilboxSourceList ReadList(string filePath, string fieldName)
        {
            string text = ReadFile(filePath);
            if (text == null) return null;
            return ReadList(text, filePath, fieldName);
        }

        private static CilboxSourceList ReadList(string text, string filePath, string fieldName)
        {
            int declaration = FindFieldDeclaration(text, fieldName);
            if (declaration < 0) return null;

            int brace = FindInitializerBrace(text, declaration);
            if (brace < 0) return null;

            int close = MatchBrace(text, brace, '{', '}');
            if (close < 0) return null;

            var list = new CilboxSourceList
            {
                FieldName = fieldName,
                BodyStart = brace + 1,
                BodyEnd = close,
            };

            string body = text.Substring(list.BodyStart, close - list.BodyStart);
            if (!TryParseBody(body, list)) return null;

            list.RoundTrips = Render(list) == body;
            return list;
        }

        /// <summary>
        /// Index of the field's name token, matched as a whole word after a <c>=</c>-terminated
        /// declaration so a mention inside a comment or another expression cannot be picked up.
        /// </summary>
        private static int FindFieldDeclaration(string text, string fieldName)
        {
            int from = 0;
            while (true)
            {
                int i = text.IndexOf(fieldName, from, StringComparison.Ordinal);
                if (i < 0) return -1;
                from = i + fieldName.Length;

                bool leftOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int after = i + fieldName.Length;
                bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
                if (!leftOk || !rightOk) continue;

                // Only a declaration is followed by '=' before the next ';' or '{'.
                int j = after;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                if (j < text.Length && text[j] == '=' && (j + 1 >= text.Length || text[j + 1] != '=')) return i;
            }
        }

        /// <summary>Index of the <c>{</c> that opens the initializer following a declaration.</summary>
        private static int FindInitializerBrace(string text, int declaration)
        {
            for (int i = declaration; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ';') return -1;
                if (c == '"') { i = SkipString(text, i); if (i < 0) return -1; continue; }
                if (c == '(')
                {
                    int close = MatchBrace(text, i, '(', ')');
                    if (close < 0) return -1;
                    i = close;
                    continue;
                }
                if (c == '{') return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ method dictionaries

        /// <summary>
        /// Parses a <c>Dictionary&lt;Type, HashSet&lt;string&gt;&gt;</c> field into one entry per
        /// <c>typeof(...)</c> key. Entries whose inner set will not round-trip come back with a
        /// <see cref="CilboxSourceList.RoundTrips"/> of false and are shown read-only.
        /// </summary>
        public static List<CilboxSourceMethodEntry> ReadMethodDictionary(string filePath, string fieldName)
        {
            string text = ReadFile(filePath);
            if (text == null) return null;

            int declaration = FindFieldDeclaration(text, fieldName);
            if (declaration < 0) return null;
            int brace = FindInitializerBrace(text, declaration);
            if (brace < 0) return null;
            int close = MatchBrace(text, brace, '{', '}');
            if (close < 0) return null;

            int bodyStart = brace + 1;
            string body = text.Substring(bodyStart, close - bodyStart);

            var result = new List<CilboxSourceMethodEntry>();
            int cursor = bodyStart;

            foreach (string chunk in SplitTopLevel(body))
            {
                int chunkStart = cursor;
                cursor += chunk.Length + 1; // + the comma consumed by the split

                int open = chunk.IndexOf('{');
                if (open < 0) continue;

                int entryOpen = chunkStart + open;
                int entryClose = MatchBrace(text, entryOpen, '{', '}');
                if (entryClose < 0) continue;

                string entryText = text.Substring(entryOpen, entryClose - entryOpen + 1);

                int typeofIndex = entryText.IndexOf("typeof(", StringComparison.Ordinal);
                if (typeofIndex < 0) continue;
                int typeofClose = MatchBrace(entryText, typeofIndex + "typeof".Length, '(', ')');
                if (typeofClose < 0) continue;

                var entry = new CilboxSourceMethodEntry
                {
                    TypeExpression = entryText.Substring(typeofIndex + 7, typeofClose - typeofIndex - 7).Trim(),
                    EntryStart = chunkStart,
                    EntryEnd = cursor,
                };

                int setIndex = entryText.IndexOf("new HashSet<string>", typeofClose, StringComparison.Ordinal);
                if (setIndex >= 0)
                {
                    int setBrace = -1;
                    for (int i = setIndex + "new HashSet<string>".Length; i < entryText.Length; i++)
                    {
                        char c = entryText[i];
                        if (c == '(') { i = MatchBrace(entryText, i, '(', ')'); if (i < 0) break; continue; }
                        if (c == '{') { setBrace = i; break; }
                        if (!char.IsWhiteSpace(c)) break;
                    }

                    if (setBrace >= 0)
                    {
                        int setClose = MatchBrace(entryText, setBrace, '{', '}');
                        if (setClose >= 0)
                        {
                            var methods = new CilboxSourceList
                            {
                                FieldName = entry.TypeExpression,
                                BodyStart = entryOpen + setBrace + 1,
                                BodyEnd = entryOpen + setClose,
                            };
                            string setBody = entryText.Substring(setBrace + 1, setClose - setBrace - 1);
                            if (TryParseBody(setBody, methods))
                            {
                                methods.RoundTrips = Render(methods) == setBody;
                            }
                            else
                            {
                                // An empty or single-line set: understood, listed, but not editable.
                                methods.RoundTrips = false;
                            }
                            entry.Methods = methods;
                        }
                    }
                }

                result.Add(entry);
            }

            return result;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Splices <paramref name="list"/>'s rendered body back over the range it was parsed from.
        ///
        /// <para>The list must have been read from the same file contents that are on disk now, and
        /// must have round-tripped when it was parsed. Both are re-checked here: the body currently
        /// occupying the parsed range has to match a render of the list's <em>original</em> entries,
        /// otherwise the file changed underneath us or the parser never understood it, and the write
        /// is refused.</para>
        /// </summary>
        public static bool TryWrite(string filePath, CilboxSourceList list, List<CilboxSourceEntry> originalEntries, out string error)
        {
            error = null;

            if (list == null)
            {
                error = "The list was not parsed.";
                return false;
            }

            if (!list.RoundTrips)
            {
                error = "This list uses formatting the editor will not rewrite. Edit the file by hand.";
                return false;
            }

            string text = ReadFile(filePath, out bool wasCrlf);
            if (text == null)
            {
                error = "Could not read " + filePath;
                return false;
            }

            if (list.BodyEnd > text.Length || list.BodyStart > list.BodyEnd)
            {
                error = "The file changed on disk. Reload the window and try again.";
                return false;
            }

            string current = text.Substring(list.BodyStart, list.BodyEnd - list.BodyStart);
            var verifier = new CilboxSourceList
            {
                Indent = list.Indent,
                OpeningTrivia = list.OpeningTrivia,
                ClosingIndent = list.ClosingIndent,
                TrailingLines = list.TrailingLines,
                SingleLine = list.SingleLine,
                Entries = originalEntries,
            };

            if (Render(verifier) != current)
            {
                error = "The file changed on disk since it was read. Reload the window and try again.";
                return false;
            }

            string updated = text.Substring(0, list.BodyStart) + Render(list) + text.Substring(list.BodyEnd);
            return TryWriteAllText(filePath, updated, wasCrlf, out error);
        }

        /// <summary>Replaces a method entry's inner set body, leaving the entry's own layout alone.</summary>
        public static bool TryWriteMethods(string filePath, CilboxSourceMethodEntry entry, List<CilboxSourceEntry> originalEntries, out string error)
        {
            error = null;
            if (entry?.Methods == null)
            {
                error = "This entry has no editable method set.";
                return false;
            }
            return TryWrite(filePath, entry.Methods, originalEntries, out error);
        }

        /// <summary>A deep copy of the entry list, for the before-image a write verifies against.</summary>
        public static List<CilboxSourceEntry> Snapshot(CilboxSourceList list)
        {
            var copy = new List<CilboxSourceEntry>(list.Entries.Count);
            foreach (CilboxSourceEntry entry in list.Entries)
            {
                copy.Add(new CilboxSourceEntry
                {
                    LeadingLines = new List<string>(entry.LeadingLines),
                    Indent = entry.Indent,
                    Expression = entry.Expression,
                    TrailingComment = entry.TrailingComment,
                    Resolved = entry.Resolved,
                    IsPlainLiteral = entry.IsPlainLiteral,
                });
            }
            return copy;
        }

        /// <summary>Builds an entry for a new plain string value, indented like the list it joins.</summary>
        public static CilboxSourceEntry NewEntry(CilboxSourceList list, string value)
        {
            var entry = new CilboxSourceEntry
            {
                Indent = list?.Indent ?? "\t\t\t",
                Expression = "\"" + Escape(value) + "\"",
            };
            Resolve(entry);
            return entry;
        }

        /// <summary>Retypes a plain string entry, keeping its comments.</summary>
        public static void SetValue(CilboxSourceEntry entry, string value)
        {
            entry.Expression = "\"" + Escape(value) + "\"";
            Resolve(entry);
        }

        // ------------------------------------------------------------------ file IO

        private static string ReadFile(string filePath) => ReadFile(filePath, out _);

        /// <summary>
        /// Reads the file with line endings normalised to <c>\n</c> so offsets and comparisons are
        /// platform-independent. <paramref name="wasCrlf"/> carries the original style back to
        /// <see cref="TryWriteAllText"/> — these files are CRLF, and rewriting them as LF would
        /// turn a one-line whitelist edit into a whole-file diff.
        /// </summary>
        private static string ReadFile(string filePath, out bool wasCrlf)
        {
            wasCrlf = false;
            try
            {
                string raw = File.ReadAllText(filePath);
                wasCrlf = raw.Contains("\r\n");
                return raw.Replace("\r\n", "\n");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryWriteAllText(string filePath, string text, bool asCrlf, out string error)
        {
            error = null;
            try
            {
                if (asCrlf) text = text.Replace("\n", "\r\n");
                // UTF-8 without a BOM, matching how these files are already stored.
                File.WriteAllText(filePath, text, new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
#endif
