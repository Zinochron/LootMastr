namespace LootMastr.Planning.Dps;

/// <summary>
/// Everything the damage formula reads about one character on one set.
///
/// The substats are <b>totals</b>, the way the character sheet shows them, with materia and food
/// already in them. That is deliberate: for anyone but the local player these come measured off the
/// game, and reconstructing them from parts would be a second answer waiting to disagree with the
/// first.
///
/// Weapon damage is the exception and comes off the item, because the attribute table does not
/// carry it — a paladin holding a sword measures 0 for physical damage. It is exact anyway, since
/// weapon damage cannot be melded.
/// </summary>
public readonly record struct StatBlock(
    int Level,
    int JobModifier,
    int MainStat,
    int WeaponDamage,
    int WeaponDelayMs,
    int CriticalHit,
    int DirectHit,
    int Determination,
    int SkillSpeed,
    int SpellSpeed,
    int Tenacity)
{
    /// <summary>Enough to compute with. A missing piece means no estimate, never a guessed one.</summary>
    public bool IsUsable => Level > 0 && JobModifier > 0 && MainStat > 0 && WeaponDamage > 0;

    /// <summary>The speed stat this job actually scales its recast with.</summary>
    public int SpeedFor(bool spellSpeed) => spellSpeed ? SpellSpeed : SkillSpeed;

    /// <summary>The same set with one stat moved, for weighing a swap.</summary>
    public StatBlock With(uint baseParam, int delta) => baseParam switch
    {
        Attributes.CriticalHit => this with { CriticalHit = CriticalHit + delta },
        Attributes.DirectHitRate => this with { DirectHit = DirectHit + delta },
        Attributes.Determination => this with { Determination = Determination + delta },
        Attributes.SkillSpeed => this with { SkillSpeed = SkillSpeed + delta },
        Attributes.SpellSpeed => this with { SpellSpeed = SpellSpeed + delta },
        Attributes.Tenacity => this with { Tenacity = Tenacity + delta },
        _ => this,
    };

    /// <summary>
    /// The <c>BaseParam</c> row ids this cares about. Duplicated from the data layer on purpose —
    /// <c>Planning</c> carries no Dalamud reference so the harness can compile it, and four
    /// constants are a smaller price than a project reference.
    /// </summary>
    public static class Attributes
    {
        public const uint Strength = 1;
        public const uint Dexterity = 2;
        public const uint Intelligence = 4;
        public const uint Mind = 5;
        public const uint Tenacity = 19;
        public const uint DirectHitRate = 22;
        public const uint CriticalHit = 27;
        public const uint Determination = 44;
        public const uint SkillSpeed = 45;
        public const uint SpellSpeed = 46;
    }
}
