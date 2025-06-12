using Basis.Scripts.BasisSdk.Players;
public static class BasisBundleConversionNetwork
{
    // Converts AvatarNetworkLoadInformation to BasisLoadableBundle
    public static BasisLoadableBundle ConvertFromNetwork(BasisAvatarNetworkLoad AvatarNetworkLoadInformation)
    {
        BasisLoadableBundle BasisLoadableBundle = new BasisLoadableBundle
        {
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = AvatarNetworkLoadInformation.URL,
            },
             BasisBundleConnector = new BasisBundleConnector(),
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle(),
            UnlockPassword = AvatarNetworkLoadInformation.UnlockPassword
        };

        return BasisLoadableBundle;
    }

    // Converts BasisLoadableBundle to AvatarNetworkLoadInformation
    public static BasisAvatarNetworkLoad ConvertToNetwork(BasisLoadableBundle BasisLoadableBundle)
    {
        BasisAvatarNetworkLoad AvatarNetworkLoadInformation = new BasisAvatarNetworkLoad
        {
            URL = BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation,
            UnlockPassword = BasisLoadableBundle.UnlockPassword
        };
        return AvatarNetworkLoadInformation;
    }

    // Converts byte array (serialized AvatarNetworkLoadInformation) to AvatarNetworkLoadInformation
    public static BasisAvatarNetworkLoad ConvertToNetwork(byte[] BasisLoadableBundle)
    {
        return BasisAvatarNetworkLoad.DecodeFromBytes(BasisLoadableBundle);
    }

    // Converts byte array (serialized AvatarNetworkLoadInformation) to BasisLoadableBundle
    public static BasisLoadableBundle ConvertNetworkBytesToBasisLoadableBundle(byte[] BasisLoadableBundle)
    {
        BasisAvatarNetworkLoad ANLI = BasisAvatarNetworkLoad.DecodeFromBytes(BasisLoadableBundle);
        return ConvertFromNetwork(ANLI);
    }

    // Converts AvatarNetworkLoadInformation to byte array (serialization)
    public static byte[] ConvertNetworkToByte(BasisAvatarNetworkLoad AvatarNetworkLoadInformation)
    {
        return AvatarNetworkLoadInformation.EncodeToBytes();
    }

    // Converts BasisLoadableBundle to byte array (serialization)
    public static byte[] ConvertBasisLoadableBundleToBytes(BasisLoadableBundle BasisLoadableBundle)
    {
        BasisAvatarNetworkLoad AvatarNetworkLoadInformation = ConvertToNetwork(BasisLoadableBundle);
        return AvatarNetworkLoadInformation.EncodeToBytes();
    }

    // Converts byte array (serialized BasisLoadableBundle) to BasisLoadableBundle
    public static BasisLoadableBundle ConvertBytesToBasisLoadableBundle(byte[] BasisLoadableBundleBytes)
    {
        BasisAvatarNetworkLoad ANLI = BasisAvatarNetworkLoad.DecodeFromBytes(BasisLoadableBundleBytes);
        return ConvertFromNetwork(ANLI);
    }
}
