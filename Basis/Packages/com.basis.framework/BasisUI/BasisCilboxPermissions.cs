using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisCilboxPermissions
    {
        private const string FileName = "cilboxPermissions.json";

        [Serializable]
        public class Approval
        {
            public string ContentHash;
            public string ContentName;
            public string ContentKind;
            public List<string> Items = new List<string>();
            public string ApprovedUtc;
            [NonSerialized] public bool Remembered;
        }

        [Serializable]
        private class SavedApprovals
        {
            public List<Approval> approvals = new List<Approval>();
        }

        private static List<Approval> remembered;
        private static readonly Dictionary<string, Approval> session = new Dictionary<string, Approval>(StringComparer.Ordinal);
        private static readonly HashSet<string> declined = new HashSet<string>(StringComparer.Ordinal);

        public static event Action OnChanged;
        public static event Action<string> OnRevoked;

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        private static void EnsureLoaded()
        {
            if (remembered != null) return;
            remembered = new List<Approval>();
            if (!File.Exists(FilePath)) return;
            try
            {
                SavedApprovals data = JsonUtility.FromJson<SavedApprovals>(File.ReadAllText(FilePath));
                if (data?.approvals == null) return;
                foreach (Approval approval in data.approvals)
                {
                    if (approval == null || string.IsNullOrEmpty(approval.ContentHash) || approval.Items == null || IndexOf(remembered, approval.ContentHash) >= 0) continue;
                    approval.Remembered = true;
                    remembered.Add(approval);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisCilboxPermissions] Failed to load {FilePath}: {e.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                SavedApprovals data = new SavedApprovals();
                data.approvals.AddRange(remembered);
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisCilboxPermissions] Failed to save {FilePath}: {e.Message}");
            }
        }

        public static Approval Find(string contentHash)
        {
            if (string.IsNullOrEmpty(contentHash)) return null;
            if (session.TryGetValue(contentHash, out Approval sessionApproval)) return sessionApproval;
            EnsureLoaded();
            int index = IndexOf(remembered, contentHash);
            return index >= 0 ? remembered[index] : null;
        }

        public static bool Covers(string contentHash, IReadOnlyCollection<string> items)
        {
            Approval approval = Find(contentHash);
            if (approval == null) return false;
            foreach (string item in items)
            {
                if (!approval.Items.Contains(item)) return false;
            }
            return true;
        }

        public static bool IsDeclined(string contentHash) => !string.IsNullOrEmpty(contentHash) && declined.Contains(contentHash);

        public static void Allow(string contentHash, string contentName, string contentKind, IEnumerable<string> items, bool remember)
        {
            if (string.IsNullOrEmpty(contentHash)) return;
            EnsureLoaded();
            Approval approval = new Approval
            {
                ContentHash = contentHash,
                ContentName = contentName ?? string.Empty,
                ContentKind = contentKind ?? string.Empty,
                Items = new List<string>(items),
                ApprovedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"),
                Remembered = remember,
            };
            declined.Remove(contentHash);
            int index = IndexOf(remembered, contentHash);
            if (remember)
            {
                session.Remove(contentHash);
                if (index >= 0) remembered[index] = approval;
                else remembered.Add(approval);
                Save();
            }
            else
            {
                session[contentHash] = approval;
            }
            OnChanged?.Invoke();
        }

        public static void Decline(string contentHash)
        {
            if (string.IsNullOrEmpty(contentHash)) return;
            declined.Add(contentHash);
        }

        public static List<Approval> GetAll()
        {
            EnsureLoaded();
            List<Approval> all = new List<Approval>(session.Values);
            all.AddRange(remembered);
            return all;
        }

        public static void Remove(string contentHash)
        {
            if (string.IsNullOrEmpty(contentHash)) return;
            EnsureLoaded();
            bool removed = session.Remove(contentHash);
            int index = IndexOf(remembered, contentHash);
            if (index >= 0)
            {
                remembered.RemoveAt(index);
                removed = true;
                Save();
            }
            if (!removed) return;
            OnRevoked?.Invoke(contentHash);
            OnChanged?.Invoke();
        }

        public static void RemoveAll()
        {
            List<Approval> all = GetAll();
            if (all.Count == 0) return;
            session.Clear();
            remembered.Clear();
            Save();
            foreach (Approval approval in all)
            {
                OnRevoked?.Invoke(approval.ContentHash);
            }
            OnChanged?.Invoke();
        }

        private static int IndexOf(List<Approval> list, string contentHash)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].ContentHash, contentHash, StringComparison.Ordinal)) return i;
            }
            return -1;
        }
    }
}
