namespace HVR.Basis.Comms
{
    public interface IHVRInitializable
    {
        public void OnHVRAvatarReady(bool isWearer);
        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer);
    }
}
