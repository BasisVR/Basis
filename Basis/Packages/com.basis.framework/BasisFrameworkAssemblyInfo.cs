// Exposes internal pure solve helpers (spine chain placement, remote bone chain composition, etc.)
// to the editor sweep/test assembly so the offline sweeps exercise the real runtime math.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BasisEditor")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Basis.Framework.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Basis.Framework.Sync.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Basis.Framework.UI.Tests")]
// The Cilbox shim tests moved to com.basis.shim/Tests; they still reach the same internals.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Basis.Shims.Tests")]
