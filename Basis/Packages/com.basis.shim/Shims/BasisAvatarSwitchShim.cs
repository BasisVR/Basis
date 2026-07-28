using System;
using Basis.Scripts.BasisSdk.Players;

namespace Basis.Shims
{
    /// <summary>
    /// Bridges the persistent player's avatar-switch notification into the Cilbox
    /// sandbox. <see cref="IBasisPlayer.OnAvatarSwitched"/> is a true C# <c>event</c>
    /// (add_/remove_ accessors), which sandboxed scripts cannot subscribe to. This
    /// native shim subscribes to it here (native code has no such restriction) and
    /// re-exposes it as the plain delegate PROPERTY <see cref="Switched"/>, which a
    /// sandboxed script can += / -=.
    ///
    /// It hangs off the PLAYER, not the avatar, so it survives avatar swaps — the
    /// avatar GameObject (and any shim on it) is destroyed and recreated on a switch,
    /// but the player, and this subscription, persist.
    ///
    /// No Cilbox whitelist entry is required: the type lives under Basis.Shims.* (already
    /// type-whitelisted), its members are ordinary property/Dispose calls, its constructor
    /// takes the already-whitelisted IBasisPlayer, and the only add_/remove_ on the true
    /// event happens in this native code — never in the sandbox.
    /// </summary>
    public sealed class BasisAvatarSwitchShim
    {
        private IBasisPlayer player;

        /// <summary>Raised (parameterless) each time the wrapped player switches avatars.</summary>
        public Action Switched { get; set; }

        public BasisAvatarSwitchShim(IBasisPlayer player)
        {
            this.player = player;
            if (this.player != null)
            {
                this.player.OnAvatarSwitched += HandleSwitched;
            }
        }

        private void HandleSwitched()
        {
            Switched?.Invoke();
        }

        /// <summary>Detach from the player and drop all handlers. Idempotent.</summary>
        public void Dispose()
        {
            if (player != null)
            {
                player.OnAvatarSwitched -= HandleSwitched;
                player = null;
            }
            Switched = null;
        }
    }
}
