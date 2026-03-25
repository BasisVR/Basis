namespace Basis.BasisUI
{
    public enum LibraryDateSortMode
    {
        Name,
        DateOldestToNewest,
        DateNewestToOldest
    }

    // Network filter state for items
    // public enum LibraryNetworkFilter
    // {
    //     All,
    //     Local, // in the library provider this will show embedded as well
    //     Network
    // }

    public enum LibraryItemTypeFilter
    {
        All,
        Embedded,
        Local,
        Networked,
        GameObject,
        Scene,
        Avatar,
        AdminOnly,
        PersistentOnly,
        NotPersistent,
        PlacedByMe,
        NotPlacedByMe,
    }
}