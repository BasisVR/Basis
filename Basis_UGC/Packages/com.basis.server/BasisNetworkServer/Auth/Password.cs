#nullable enable

using Encoding = System.Text.Encoding;
using static Basis.Network.Core.Serializable.SerializableBasis;
using System;

namespace Basis.Network.Server.Auth
{

    /// Newtype on `string`. This represents the server's configured password.
    internal readonly struct ServerPassword
    {
        public readonly string V { get; }
        public ServerPassword(string password) { V = password; }
    }

    /// Newtype on `string`. This represents the user's password.
    internal readonly struct UserPassword
    {
        public readonly string V { get; }
        public UserPassword(string password) { V = password; }
    }

    internal readonly struct Deserialized
    {
        public readonly UserPassword Password { get; }
        public Deserialized(byte[] Bytesmsg)
        {
            string password = Encoding.UTF8.GetString(Bytesmsg);
            Password = new UserPassword(password);
        }
    }

    public class PasswordAuth : IAuth
    {
        private readonly ServerPassword serverPassword;

        /// If `serverPassword` is an empty string, the server has no password and any user can connect.
        public PasswordAuth(string serverPassword)
        {
            this.serverPassword = new ServerPassword(serverPassword);
        }

        private static bool CheckPassword(ServerPassword serverPassword, UserPassword userPassword)
        {
            if (string.IsNullOrEmpty(serverPassword.V))
            {
                BNL.Log("No server password set, user is allowed");
                return true;
            }
            if (string.IsNullOrEmpty(userPassword.V))
            {
                BNL.Log("User had an empty password, user is rejected");
                return false;
            }
            // Compare strings with explicit options
            if (string.Equals(serverPassword.V, userPassword.V, StringComparison.Ordinal))
            {
                BNL.Log("Passwords match successfully.");
                return true;
            }
            else
            {
                BNL.LogError($"Passwords do not match: ServerPassword [{serverPassword.V}], UserPassword [{userPassword.V}]");
                return false;
            }
        }

        public bool IsAuthenticated(byte[] Bytesmsg)
        {
            var deserialized = new Deserialized(Bytesmsg);
            return CheckPassword(serverPassword, deserialized.Password);
        }
    }
}
