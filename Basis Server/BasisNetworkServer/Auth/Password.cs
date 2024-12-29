#nullable enable

using System.Runtime.Serialization;
using Encoding = System.Text.Encoding;
using static Basis.Network.Core.Serializable.SerializableBasis;

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
        public Deserialized(AuthenticationMessage msg)
        {
            Password = new UserPassword(Encoding.UTF8.GetString(msg.bytes));
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
            if (serverPassword.V == string.Empty)
            {
                BNL.Log("No server password set, user is allowed");
                return true;
            }
            if (userPassword.V == string.Empty)
            {
                BNL.Log("User had an empty password, user is rejected");
                return false;
            }
            return serverPassword.V == userPassword.V;
        }

        public bool IsAuthenticated(AuthenticationMessage msg)
        {
            var deserialized = new Deserialized(msg);
            return CheckPassword(serverPassword, deserialized.Password);
        }
    }
}