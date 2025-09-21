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
using System.Linq;
using System.Text.RegularExpressions;
using Meta.XR.Editor.UserInterface;
using UnityEditor;
using UnityEngine;

namespace Meta.XR.Editor.TelemetryUI
{
    internal static class Utils
    {
        private static Color? _buttonAcceptColor;

        public static Color ButtonAcceptColor
        {
            get
            {
                _buttonAcceptColor ??= UserInterface.Utils.HexToColor("#99c2ff");
                return _buttonAcceptColor.Value;
            }
        }

        public static IEnumerable<IUserInterfaceItem> GetGuideItemsForMarkdownText(string markdownText)
        {
            if (markdownText == null)
            {
                return Array.Empty<IUserInterfaceItem>();
            }

            markdownText = ConvertMarkdownToUnityRichText(markdownText);

            return SplitMarkdownInLines(markdownText)
                .Select(ParseMarkdownLine)
                .SelectMany(GetGuideItemsForMarkdownContent);
        }

        private struct Link
        {
            public string Content { get; set; }
            public string Url { get; set; }
        }

        private struct MarkdownContent
        {
            public string Text { get; set; }
            public IEnumerable<Link> Links { get; set; }
        }

        private static IEnumerable<IUserInterfaceItem> GetGuideItemsForMarkdownContent(MarkdownContent content)
        {
            if (!string.IsNullOrEmpty(content.Text))
            {
                yield return new Label(content.Text, UserInterface.Styles.GUIStyles.RichTextStyle);
            }

            foreach (var link in content.Links)
            {
                yield return new LinkLabel(new GUIContent(link.Content), link.Url, null);
                yield return new AddSpace(5);
            }
        }

        private static MarkdownContent ParseMarkdownLine(string line)
        {
            if (line.StartsWith("- "))
            {
                line = "• " + line[2..];
            }

            var links = new List<Link>();

            const string pattern = @"\[(.*?)\]\((.*?)\)(\.)?";
            var originalLine = line;
            line = Regex.Replace(line, pattern, match =>
            {
                var trailingDot = match.Groups[3].Value;

                var link = new Link
                {
                    Content = match.Groups[1].Value + trailingDot,
                    Url = match.Groups[2].Value
                };
                links.Add(link);

                var matchEndIndex = match.Index + match.Length;
                if (matchEndIndex == originalLine.Length || originalLine[matchEndIndex] == '\n')
                {
                    // Erase the link from the string if it's at the end of a line
                    return string.Empty;
                }

                return link.Content + trailingDot;
            });

            return new MarkdownContent
            {
                Text = line,
                Links = links
            };
        }

        private static string ConvertMarkdownToUnityRichText(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            const string boldPattern = @"\*\*(.+?)\*\*";
            const string boldReplacement = "<b>$1</b>";
            input = Regex.Replace(input, boldPattern, boldReplacement);

            const string italicsPattern = @"(\*|_)(.+?)\1";
            const string italicsReplacement = "<i>$2</i>";
            input = Regex.Replace(input, italicsPattern, italicsReplacement);

            const string underlinePattern = @"__(.+?)__";
            const string underlineReplacement = "<u>$1</u>";
            input = Regex.Replace(input, underlinePattern, underlineReplacement);

            const string strikethroughPattern = @"~~(.+?)~~";
            const string strikethroughReplacement = "<s>$1</s>";
            input = Regex.Replace(input, strikethroughPattern, strikethroughReplacement);
            return input;
        }

        private static IEnumerable<string> SplitMarkdownInLines(string markdown)
        {
            var lines = markdown.Split('\n').ToList();

            // Remove leading empty lines
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0)
            {
                lines[0] = lines[0].TrimStart();
            }

            // Remove trailing empty lines
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }
}
