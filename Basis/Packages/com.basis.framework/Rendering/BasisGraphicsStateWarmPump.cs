using Basis.Scripts.Drivers;

public static class BasisGraphicsStateWarmPump
{
    private static bool _running;

    public static void Apply(bool enabled)
    {
        if (enabled == _running)
        {
            return;
        }
        _running = enabled;
        if (enabled)
        {
            BasisFrameClock.OnTick += Tick;
            BasisFrameClock.AddRequest();
        }
        else
        {
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
        }
    }

    private static void Tick()
    {
        BasisGraphicsStatePrewarm.Pump();
    }
}
