// Exposes internal pure solve helpers (spine chain placement, remote bone chain composition, etc.)
// to the editor sweep/test assembly so the offline sweeps exercise the real runtime math.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BasisEditor")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Basis.Framework.Tests")]
