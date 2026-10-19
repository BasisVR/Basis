using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class BasisMediaTickLoopTests
{
    sealed class Consumer : IBasisMediaTickConsumer
    {
        public int Ticks;
        public Action<Consumer> OnTick;
        public BasisMediaTickStage TickStage => BasisMediaTickStage.Output;

        public void MediaTick()
        {
            Ticks++;
            OnTick?.Invoke(this);
        }
    }

    readonly List<IBasisMediaTickConsumer> ran = new();

    void Run(List<IBasisMediaTickConsumer> list) => BasisMediaTickLoop.Run(list, ran);

    [Test]
    public void A_consumer_that_removes_itself_does_not_make_the_loop_skip_the_next_one()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer { OnTick = self => list.Remove(self) };
        var second = new Consumer();
        list.Add(first);
        list.Add(second);

        Run(list);

        Assert.That(first.Ticks, Is.EqualTo(1));
        Assert.That(second.Ticks, Is.EqualTo(1), "the consumer shifted into the vacated slot still ticks");
        Assert.That(list, Is.EqualTo(new IBasisMediaTickConsumer[] { second }));
    }

    [TearDown]
    public void TearDown() => LogAssert.ignoreFailingMessages = false;

    [Test]
    public void A_consumer_that_removes_itself_and_throws_does_not_stop_the_rest()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer { OnTick = self => { list.Remove(self); throw new InvalidOperationException("scratch"); } };
        var second = new Consumer();
        list.Add(first);
        list.Add(second);
        // The loop reports the throw through a once-per-site logger, which
        // may already have fired this domain; the failure is not asserted on.
        LogAssert.ignoreFailingMessages = true;

        Run(list);

        Assert.That(second.Ticks, Is.EqualTo(1));
    }

    [Test]
    public void A_consumer_that_removes_an_earlier_one_does_not_tick_anything_twice()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer();
        var second = new Consumer { OnTick = _ => list.Remove(first) };
        var third = new Consumer();
        list.AddRange(new IBasisMediaTickConsumer[] { first, second, third });

        Run(list);

        Assert.That(first.Ticks, Is.EqualTo(1));
        Assert.That(second.Ticks, Is.EqualTo(1));
        Assert.That(third.Ticks, Is.EqualTo(1));
    }

    [Test]
    public void A_consumer_that_removes_an_earlier_one_and_itself_still_leaves_the_rest_ticked()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer();
        var second = new Consumer();
        var third = new Consumer();
        second.OnTick = self => { list.Remove(first); list.Remove(self); };
        list.AddRange(new IBasisMediaTickConsumer[] { first, second, third });

        Run(list);

        Assert.That(first.Ticks, Is.EqualTo(1));
        Assert.That(second.Ticks, Is.EqualTo(1));
        Assert.That(third.Ticks, Is.EqualTo(1), "two slots vanished at once and the last consumer still ticks");
    }

    [Test]
    public void A_consumer_that_re_registers_itself_after_another_ticks_once()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer();
        var second = new Consumer();
        first.OnTick = self => { list.Remove(self); list.Add(self); };
        list.Add(first);
        list.Add(second);

        Run(list);

        Assert.That(first.Ticks, Is.EqualTo(1), "met again after re-registering, it does not run twice");
        Assert.That(second.Ticks, Is.EqualTo(1));
    }

    [Test]
    public void A_consumer_that_removes_the_last_one_ends_the_loop_cleanly()
    {
        var list = new List<IBasisMediaTickConsumer>();
        var first = new Consumer();
        var second = new Consumer();
        first.OnTick = _ => list.Remove(second);
        list.Add(first);
        list.Add(second);

        Run(list);

        Assert.That(first.Ticks, Is.EqualTo(1));
        Assert.That(second.Ticks, Is.EqualTo(0));
    }
}
