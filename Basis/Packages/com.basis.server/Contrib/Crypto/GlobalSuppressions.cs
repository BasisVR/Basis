using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("CodeReloadSafety", "UAL0010", Justification = "Also built as the standalone server: statics live for the process and session state is reset by the server's own shutdown.")]
[assembly: SuppressMessage("CodeReloadSafety", "UAL0013", Justification = "Also built as the standalone server: statics live for the process and session state is reset by the server's own shutdown.")]
