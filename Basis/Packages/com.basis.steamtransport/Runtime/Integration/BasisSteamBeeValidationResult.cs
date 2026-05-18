namespace Basis.Scripts.Networking.Steam
{
    public sealed class BasisSteamBeeValidationResult
    {
        public bool IsValid;
        public string ErrorMessage = string.Empty;
        public string WorldUrl = string.Empty;
        public string WorldPassword = string.Empty;
        public string WorldName = string.Empty;
    }
}
