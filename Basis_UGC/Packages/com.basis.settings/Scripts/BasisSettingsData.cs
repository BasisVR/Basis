using System;
using System.Collections.Generic;

[Serializable]
public class BasisSettingsData
{
    // Using Dictionary for O(1) fast lookups
    public Dictionary<string, BasisDataItem> itemsDictionary = new Dictionary<string, BasisDataItem>();

    // Find item by identifier with O(1) lookup time
    public BasisDataItem FindItemByIdentifier(string identifier)
    {
        itemsDictionary.TryGetValue(identifier, out var item);
        return item;
    }
}
