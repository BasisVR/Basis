public partial class BundledContentHolder
{
    public enum Selector
    {
        Avatar = 0,
        System = 1,
        Prop = 2
    }
    public enum Mode
    {
        Avatar = 0,
        World = 1,
        Prop = 2,
        Legacy = 3, // this will be used to flag items before new basis metadata for now
    }
    
    // used to determine which item key stores are local or networked
    // used as reference by the library provider to determine network type of item
    public enum NetworkType
    {
        Local = 0,
        Networked = 1,
    }

    // used to determine the way an item can be placed into a world
    public enum PlacementType
    {
        SpawnAtRaycast = 0, // will spawn the item using the players desired location using raycast placement
        SpawnInFrontOfPlayer = 1, // will spawn the item at the players eye height and in front of them, and face towards them
        SpawnAtPlayerOrigin = 2, // will spawn the item at the players origin
    }
}
