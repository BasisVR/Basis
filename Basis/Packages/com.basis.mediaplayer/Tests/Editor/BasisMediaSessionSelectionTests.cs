using System.Collections.Generic;
using NUnit.Framework;
using Candidate = BasisMediaSessionSelection.Candidate;

public class BasisMediaSessionSelectionTests
{
    const float Margin = 0.8f;
    readonly List<int> activate = new();
    readonly List<int> demote = new();

    static Candidate Active(float distance, bool held = false) => new Candidate { Active = true, Distance = distance, Held = held };
    static Candidate Dormant(float distance, bool held = false, bool promoted = false)
        => new Candidate { Active = false, Distance = distance, Held = held, Promoted = promoted };

    void Select(int cap, params Candidate[] ranked) => BasisMediaSessionSelection.Select(ranked, cap, Margin, activate, demote);

    [Test]
    public void A_near_equal_pair_under_the_cap_keeps_the_running_one()
    {
        // Cap 1, dormant at 9.5 m ranked above the active at 10 m: the margin
        // holds them apart, and the active one is not demoted on its own.
        Select(1, Dormant(9.5f), Active(10f));
        Assert.That(activate, Is.Empty);
        Assert.That(demote, Is.Empty);
    }

    [Test]
    public void A_clearly_nearer_dormant_player_takes_the_slot_in_one_pass()
    {
        Select(1, Dormant(7f), Active(10f));
        Assert.That(activate, Is.EqualTo(new[] { 0 }));
        Assert.That(demote, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void A_promoted_player_takes_the_slot_regardless_of_distance()
    {
        Select(1, Dormant(20f, promoted: true), Active(10f));
        Assert.That(activate, Is.EqualTo(new[] { 0 }));
        Assert.That(demote, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void A_free_slot_is_taken_without_displacing_anyone()
    {
        Select(2, Active(5f), Dormant(9f));
        Assert.That(activate, Is.EqualTo(new[] { 1 }));
        Assert.That(demote, Is.Empty);
    }

    [Test]
    public void Dwell_holds_the_status_quo_on_either_side()
    {
        Select(1, Dormant(7f, held: true), Active(10f));
        Assert.That(activate, Is.Empty, "a dormant player inside its dwell does not move");
        Assert.That(demote, Is.Empty, "and the one it would displace stays");

        Select(1, Dormant(7f), Active(10f, held: true));
        Assert.That(activate, Is.Empty, "an active player inside its dwell is not displaced");
        Assert.That(demote, Is.Empty);
    }

    [Test]
    public void A_lowered_cap_demotes_the_lowest_ranked_sessions()
    {
        Select(1, Active(5f), Active(8f), Active(12f));
        Assert.That(activate, Is.Empty);
        Assert.That(demote, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void Nothing_moves_without_a_cap()
    {
        Select(0, Dormant(1f), Active(10f));
        Assert.That(activate, Is.Empty);
        Assert.That(demote, Is.Empty);
    }

    [Test]
    public void A_paused_player_resumes_where_it_was_and_a_playing_one_moves_on()
    {
        Assert.That(BasisMediaSessionSelection.ResumeTarget(paused: true, savedSeconds: 30d, elapsedSeconds: 4d), Is.EqualTo(30d));
        Assert.That(BasisMediaSessionSelection.ResumeTarget(paused: false, savedSeconds: 30d, elapsedSeconds: 4d), Is.EqualTo(34d));
    }
}
