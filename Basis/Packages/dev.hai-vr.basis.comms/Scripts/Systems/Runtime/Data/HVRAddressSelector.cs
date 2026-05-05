using System;

namespace HVR.Basis.Comms
{
    [Serializable]
    public struct HVRAddressSelector
    {
        public string path;
        public HVRAddress asset;

        public bool TryResolvePath(out string result)
        {
            if (asset != null)
            {
                result = asset.AsPath();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                result = path;
                return true;
            }

            result = "";
            return false;
        }
    }
}
