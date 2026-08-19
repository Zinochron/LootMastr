using System;
using System.Collections.Generic;
using System.Linq;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// One decision somebody made by hand, pinned against the forecast.
///
/// It identifies a <b>drop</b>, not a week. That is the whole difference between this and a frozen
/// schedule: pinning the twine in week two says nothing about the body coffer beside it, and the
/// rest of that week goes on being worked out. A group that has agreed one thing has agreed one
/// thing.
/// </summary>
[Serializable]
public sealed class ManualAward
{
    public int Week { get; set; }

    /// <summary>Which fight it drops in. 0 for a purchase, which no fight hands over.</summary>
    public int Encounter { get; set; }

    public GearSlot? Slot { get; set; }

    public GearSide? Upgrade { get; set; }

    /// <summary>
    /// Which occurrence of this coffer within the fight, counting from zero.
    ///
    /// Almost always 0. A tier whose drop pool is smaller than its drop count can put the same
    /// coffer up twice in one clear, and without this the two would be one pin that fights itself.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Who gets it. <b>Empty means nobody, deliberately</b> — that is a decision too, and it has to
    /// be tellable apart from "no pin here", which is the absence of a row rather than a blank one.
    /// </summary>
    public string PlayerKey { get; set; } = string.Empty;

    /// <summary>A purchase rather than a drop.</summary>
    public bool Bought { get; set; }

    /// <summary>Bought from the tomestone vendor rather than with books.</summary>
    public bool WithTomestones { get; set; }

    public bool IsNobody => string.IsNullOrEmpty(PlayerKey);

    /// <summary>Whether this pin is about the same thing a simulated drop is about.</summary>
    public bool Matches(int week, int encounter, GearSlot? slot, GearSide? upgrade, int ordinal) =>
        Week == week && Encounter == encounter && Ordinal == ordinal &&
        Slot == slot && Upgrade == upgrade;

    public ManualAward Clone() => (ManualAward)MemberwiseClone();
}

/// <summary>
/// Everything a group has pinned, and whether the pins are being honoured at all.
///
/// Off by default, and off means <b>bit for bit the old behaviour</b>: the simulator asks this for
/// an override, gets nothing, and ranks as it always did. A feature nobody switches on has to change
/// nothing, and that is the one assertion in the harness worth more than the rest.
/// </summary>
[Serializable]
public sealed class ManualPlan
{
    public bool Enabled { get; set; }

    public List<ManualAward> Awards { get; set; } = new();

    /// <summary>The pin for one drop, or null when the rules should decide.</summary>
    public ManualAward? For(int week, int encounter, GearSlot? slot, GearSide? upgrade, int ordinal)
    {
        if (!Enabled)
            return null;

        foreach (var award in Awards)
        {
            if (!award.Bought && award.Matches(week, encounter, slot, upgrade, ordinal))
                return award;
        }

        return null;
    }

    /// <summary>Purchases pinned for one week, in the order they were added.</summary>
    public IEnumerable<ManualAward> PurchasesIn(int week) =>
        Enabled ? Awards.Where(a => a.Bought && a.Week == week) : [];

    /// <summary>
    /// Replaces the pin for one drop, or removes it.
    ///
    /// One row per drop at most. Somebody changing their mind twice about the same coffer should end
    /// up with one decision, not a pile of them in which the oldest quietly wins.
    /// </summary>
    public void Pin(ManualAward award)
    {
        Awards.RemoveAll(a => !a.Bought && a.Matches(award.Week, award.Encounter, award.Slot,
                                                     award.Upgrade, award.Ordinal));

        Awards.Add(award);
    }

    public void Unpin(int week, int encounter, GearSlot? slot, GearSide? upgrade, int ordinal) =>
        Awards.RemoveAll(a => !a.Bought && a.Matches(week, encounter, slot, upgrade, ordinal));

    public void Remove(ManualAward award) => Awards.Remove(award);

    /// <summary>Pins for weeks that no longer exist, which a shorter horizon leaves behind.</summary>
    public int DropBeyond(int horizon) => Awards.RemoveAll(a => a.Week > horizon);

    public ManualPlan Clone() => new()
    {
        Enabled = Enabled,
        Awards = Awards.Select(a => a.Clone()).ToList(),
    };
}

/// <summary>
/// Something the forecast could not do, said rather than silently worked around.
///
/// Every one of these is a pin the simulator refused to honour. It reports and moves on: a plan that
/// quietly does something other than what it says is worse than one with a red line in it, because
/// the red line is the only version somebody can act on.
/// </summary>
public readonly record struct PlanProblem(int Week, string What, string Message);
