using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootMastr.Data;

/// <summary>
/// The <c>BaseParam</c> row ids the damage formula needs. They are also the values of
/// <c>PlayerAttribute</c> — the two tables are the same list, which is what makes one set of
/// numbers usable for both the local player and an inspected one.
/// </summary>
public static class Attributes
{
    public const uint Strength = 1;
    public const uint Dexterity = 2;
    public const uint Vitality = 3;
    public const uint Intelligence = 4;
    public const uint Mind = 5;
    public const uint Piety = 6;
    public const uint PhysicalDamage = 12;
    public const uint MagicalDamage = 13;
    public const uint Delay = 14;
    public const uint Tenacity = 19;
    public const uint DirectHitRate = 22;
    public const uint CriticalHit = 27;
    public const uint Determination = 44;
    public const uint SkillSpeed = 45;
    public const uint SpellSpeed = 46;

    /// <summary>Everything worth storing. Reading all 74 would keep a lot of zeroes.</summary>
    public static readonly uint[] Wanted =
    [
        Strength, Dexterity, Vitality, Intelligence, Mind, Piety,
        PhysicalDamage, MagicalDamage, Delay,
        Tenacity, DirectHitRate, CriticalHit, Determination, SkillSpeed, SpellSpeed,
    ];
}

/// <summary>What one character's attributes were when they were last read.</summary>
public readonly record struct MeasuredStats(uint JobId, int Level, IReadOnlyDictionary<uint, int> Values)
{
    public bool IsUsable => Level > 0 && Values.Count > 0;
}

/// <summary>
/// Reads a character's <b>finished</b> attributes — the numbers on their character sheet, with
/// materia, food and every trait already in them.
///
/// This is the whole reason a damage estimate is possible for other people. Their melds are not
/// readable: <c>AgentInspect.ItemData</c> carries an item id and nothing else, no materia and no
/// high quality flag. But <c>UIState.Inspect.BaseParams</c> is the totals the game itself computed,
/// indexed by <c>BaseParam</c> row id, and that sidesteps having to reconstruct anything.
///
/// So measured stats are the truth and arithmetic is only ever used for the <i>difference</i> a
/// swapped item would make.
/// </summary>
public sealed class AttributeReader
{
    /// <summary>The local player, from their own attribute table.</summary>
    public unsafe bool TryReadLocal(out MeasuredStats stats)
    {
        stats = default;

        var state = PlayerState.Instance();
        if (state == null || !state->IsLoaded)
            return false;

        var values = new Dictionary<uint, int>(Attributes.Wanted.Length);

        foreach (var id in Attributes.Wanted)
        {
            var value = state->GetAttributeByIndex((PlayerAttribute)id);
            if (value != 0)
                values[id] = value;
        }

        stats = new MeasuredStats(state->CurrentClassJobId, state->CurrentLevel, values);
        return stats.IsUsable;
    }

    /// <summary>
    /// Whoever the examine window is showing right now. Read while the window is up, which is the
    /// only moment these numbers exist — the game does not keep them once it closes.
    ///
    /// <paramref name="entityId"/> is who the numbers have to belong to, and checking it is not
    /// pedantry: the items and the attributes arrive in separate answers, so at the instant a new
    /// examine window opens this table can still hold the <i>previous</i> character's numbers. Gear
    /// from one player and stats from another would produce a damage figure that is wrong rather
    /// than missing, and nothing downstream could tell. Pass 0 to read whatever is there.
    /// </summary>
    public unsafe bool TryReadInspected(uint entityId, out MeasuredStats stats)
    {
        stats = default;

        var state = UIState.Instance();
        if (state == null)
            return false;

        var inspect = state->Inspect;

        if (entityId != 0 && inspect.EntityId != entityId)
            return false;

        var span = inspect.BaseParams;

        var values = new Dictionary<uint, int>(Attributes.Wanted.Length);

        foreach (var id in Attributes.Wanted)
        {
            if (id >= span.Length)
                continue;

            var value = (int)span[(int)id];
            if (value != 0)
                values[id] = value;
        }

        stats = new MeasuredStats(inspect.ClassJobId, inspect.Level, values);
        return stats.IsUsable;
    }
}
