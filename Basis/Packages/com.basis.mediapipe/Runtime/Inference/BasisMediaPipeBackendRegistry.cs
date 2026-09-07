using System;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Decouples the core package from the optional homuler integration assembly.
    /// The homuler assembly registers its factory at startup; when absent, Create()
    /// returns a no-op backend so the feature stays inert.
    /// </summary>
    public static class BasisMediaPipeBackendRegistry
    {
        private static Func<IBasisMediaPipeBackend> _factory;

        public static void Register(Func<IBasisMediaPipeBackend> factory) => _factory = factory;

        public static IBasisMediaPipeBackend Create() =>
            _factory != null ? _factory() : new BasisMediaPipeNullBackend();
    }
}
