using System;

namespace HVR.Basis.Comms
{
    // This is a hack around a probable bug in Basis.
    // - I'm getting OnAvatarReady called before OnNetworkReady for the local player, and
    // - I'm getting OnNetworkReady called before OnAvatarReady for remote players.
    // This inconsistency makes it annoying to build an avatar that initializes correctly for no-network singleplayer use.
    //
    // This class creates a new event called once after both OnAvatarReady and OnNetworkReady are called.
    public class Nethack
    {
        private readonly Action<bool> _onReadyBothAvatarAndNetwork;

        private bool _avatarReady;
        private bool _networkReady;
        private bool _isLocallyOwned;

        public Nethack(Action<bool> onReadyBothAvatarAndNetwork)
        {
            _onReadyBothAvatarAndNetwork = onReadyBothAvatarAndNetwork;
        }

        public void AfterAvatarReady()
        {
            if (_avatarReady) return;
            _avatarReady = true;
            if (_avatarReady && _networkReady) _onReadyBothAvatarAndNetwork(_isLocallyOwned);
        }

        public void AfterNetworkReady(bool isLocallyOwned)
        {
            if (_networkReady) return;
            _networkReady = true;
            _isLocallyOwned = isLocallyOwned;
            if (_avatarReady && _networkReady) _onReadyBothAvatarAndNetwork(_isLocallyOwned);
        }
    }
}
