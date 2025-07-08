using LiteNetLib;

namespace Basis.Logging
{
    public class BasisClientLogger : INetLogger
    {
        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            switch (level)
            {
                case NetLogLevel.Warning: BNL.LogWarning(str); break;
                case NetLogLevel.Error: BNL.LogError(str); break;
            }
        }
    }
}
