using Basis.Network.Core;
public class BasisServerLogger : INetLogger
{
    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        switch (level)
        {
            case NetLogLevel.Warning:
                BNL.LogWarning(str);
                break;
            case NetLogLevel.Error:
                BNL.LogError(str);
                break;
                // case NetLogLevel.Trace:
                //  BNL.Log(str);
                //break;
                //  case NetLogLevel.Info:
                //   BNL.Log(str);
                //break;
        }
    }
}
