using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Basis.Tests.UI
{
    [Serializable]
    public struct BasisUILayoutSnapshotEntry
    {
        public string Path;
        public bool ActiveSelf;
        public bool ActiveInHierarchy;
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 AnchoredPosition;
        public Vector2 SizeDelta;
        public Vector2 RectSize;
        public Vector3 LocalScale;
    }

    [Serializable]
    public class BasisUILayoutSnapshot
    {
        public int SettlePasses;
        public int NoOpResizes;
        public List<BasisUILayoutSnapshotEntry> Entries = new List<BasisUILayoutSnapshotEntry>();

        public static BasisUILayoutSnapshot Capture(RectTransform root)
        {
            BasisUILayoutSnapshot snapshot = new BasisUILayoutSnapshot();
            Walk(root, null, snapshot.Entries);
            return snapshot;
        }

        private static void Walk(Transform node, string parentPath, List<BasisUILayoutSnapshotEntry> entries)
        {
            string path = parentPath == null
                ? node.name
                : parentPath + "/" + node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":" + node.name;

            if (node is RectTransform rect)
            {
                entries.Add(new BasisUILayoutSnapshotEntry
                {
                    Path = path,
                    ActiveSelf = node.gameObject.activeSelf,
                    ActiveInHierarchy = node.gameObject.activeInHierarchy,
                    AnchorMin = rect.anchorMin,
                    AnchorMax = rect.anchorMax,
                    Pivot = rect.pivot,
                    AnchoredPosition = rect.anchoredPosition,
                    SizeDelta = rect.sizeDelta,
                    RectSize = rect.rect.size,
                    LocalScale = rect.localScale,
                });
            }

            for (int i = 0; i < node.childCount; i++)
            {
                Walk(node.GetChild(i), path, entries);
            }
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static BasisUILayoutSnapshot FromJson(string json) => JsonUtility.FromJson<BasisUILayoutSnapshot>(json);

        public List<string> Diff(BasisUILayoutSnapshot current, float tolerance)
        {
            List<string> problems = new List<string>();
            Dictionary<string, BasisUILayoutSnapshotEntry> baseline = Index(Entries);
            Dictionary<string, BasisUILayoutSnapshotEntry> observed = Index(current.Entries);

            foreach (KeyValuePair<string, BasisUILayoutSnapshotEntry> pair in baseline)
            {
                if (!observed.TryGetValue(pair.Key, out BasisUILayoutSnapshotEntry entry))
                {
                    problems.Add($"missing node: {pair.Key}");
                    continue;
                }
                CompareEntry(pair.Value, entry, tolerance, problems);
            }

            foreach (string key in observed.Keys)
            {
                if (!baseline.ContainsKey(key))
                {
                    problems.Add($"unexpected node: {key}");
                }
            }

            return problems;
        }

        private static Dictionary<string, BasisUILayoutSnapshotEntry> Index(List<BasisUILayoutSnapshotEntry> entries)
        {
            Dictionary<string, BasisUILayoutSnapshotEntry> map = new Dictionary<string, BasisUILayoutSnapshotEntry>(entries.Count);
            foreach (BasisUILayoutSnapshotEntry entry in entries)
            {
                map[entry.Path] = entry;
            }
            return map;
        }

        private static void CompareEntry(BasisUILayoutSnapshotEntry expected, BasisUILayoutSnapshotEntry actual, float tolerance, List<string> problems)
        {
            if (expected.ActiveSelf != actual.ActiveSelf)
            {
                problems.Add($"{expected.Path} activeSelf: {expected.ActiveSelf} -> {actual.ActiveSelf}");
            }
            if (expected.ActiveInHierarchy != actual.ActiveInHierarchy)
            {
                problems.Add($"{expected.Path} activeInHierarchy: {expected.ActiveInHierarchy} -> {actual.ActiveInHierarchy}");
            }
            if (!expected.ActiveInHierarchy || !actual.ActiveInHierarchy)
            {
                return;
            }

            CompareVector(expected.Path, "anchorMin", expected.AnchorMin, actual.AnchorMin, tolerance, problems);
            CompareVector(expected.Path, "anchorMax", expected.AnchorMax, actual.AnchorMax, tolerance, problems);
            CompareVector(expected.Path, "pivot", expected.Pivot, actual.Pivot, tolerance, problems);
            CompareVector(expected.Path, "anchoredPosition", expected.AnchoredPosition, actual.AnchoredPosition, tolerance, problems);
            CompareVector(expected.Path, "sizeDelta", expected.SizeDelta, actual.SizeDelta, tolerance, problems);
            CompareVector(expected.Path, "rectSize", expected.RectSize, actual.RectSize, tolerance, problems);
            CompareVector(expected.Path, "localScale", expected.LocalScale, actual.LocalScale, tolerance, problems);
        }

        private static void CompareVector(string path, string field, Vector3 expected, Vector3 actual, float tolerance, List<string> problems)
        {
            if (Mathf.Abs(expected.x - actual.x) > tolerance ||
                Mathf.Abs(expected.y - actual.y) > tolerance ||
                Mathf.Abs(expected.z - actual.z) > tolerance)
            {
                problems.Add($"{path} {field}: ({Fmt(expected)}) -> ({Fmt(actual)})");
            }
        }

        private static string Fmt(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", value.x, value.y, value.z);
        }
    }
}
