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
    }
    
    // used to determine which item key stores are local or networked
    // used as reference by the library provider to determine network type of item
    public enum NetworkType
    {
        Local = 0,
        Networked = 1,
    }
}
