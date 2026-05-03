using System;
using System.Collections.Generic;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;

namespace Basis.BasisUI
{
    [Serializable]
    public class EmbeddedItemDefinition
    {
        public ItemKey Key = new ItemKey
        {
            EmbeddedSettings = EmbeddedSettings.BEEUrl,
            PinnedSettings = PinnedSettings.Default
        };

        public bool HasCustomIconAddress;
        public string IconAddress;

        public bool HasCustomBounds;
        public BasisBounds Bounds = new BasisBounds(Vector3.one, Vector3.zero);

        public bool HasCustomSpawnOffset;
        public Vector3 SpawnOffsetFromPlayerReference = new Vector3(0f, 0f, 0.5f);
    }

    [CreateAssetMenu(fileName = "EmbeddedItemsCatalog", menuName = "Basis/Embedded Items Catalog", order = 1)]
    public class EmbeddedItemsCatalogAsset : ScriptableObject
    {
        public List<EmbeddedItemDefinition> Entries = new List<EmbeddedItemDefinition>();
    }
}
