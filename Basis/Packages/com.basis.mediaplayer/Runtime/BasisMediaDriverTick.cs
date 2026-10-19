using System;
using Basis.EventDriver;
using UnityEngine;

/// <summary>
/// One tick per frame, taken from <see cref="BasisEventDriver"/> where a client
/// provides one and from the component's own Update where nothing does. Both
/// paths stay live: whichever reaches a frame first runs the tick and the other
/// stands down for that frame.
///
/// Both are needed. The driver sets its instance in its own OnEnable, so a
/// component enabled ahead of it would see none and could never pick one up;
/// the driver also drops every subscriber when it is torn down, without telling
/// them. And the package runs with no driver at all in its own editor smoke
/// test and in the standalone harnesses, which is where its automated coverage
/// lives.
/// </summary>
internal sealed class BasisMediaDriverTick
{
    readonly Action tick;
    BasisEventDriver subscribedTo;
    int tickedFrame = -1;

    internal BasisMediaDriverTick(Action tick)
    {
        this.tick = tick;
    }

    /// <summary>From the component's OnEnable.</summary>
    internal void Arm() => Subscribe();

    /// <summary>From the component's OnDisable.</summary>
    internal void Disarm()
    {
        BasisEventDriver.OnUpdate -= Run;
        subscribedTo = null;
    }

    /// <summary>From the component's Update.</summary>
    internal void RunFromUpdate()
    {
        Subscribe();
        Run();
    }

    void Subscribe()
    {
        BasisEventDriver driver = BasisEventDriver.Instance;
        if (driver == null || ReferenceEquals(driver, subscribedTo)) return;
        BasisEventDriver.OnUpdate -= Run;
        BasisEventDriver.OnUpdate += Run;
        subscribedTo = driver;
    }

    void Run()
    {
        if (tickedFrame == Time.frameCount) return;
        tickedFrame = Time.frameCount;
        tick();
    }
}
