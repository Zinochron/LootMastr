using System;

namespace LootMastr.Planning.Dps;

/// <summary>
/// How much potency a job lands per second, and how much of that is bound to the global cooldown.
///
/// This is the one part of the damage estimate that is not arithmetic on the stats, and it used to be
/// three guessed numbers per job — a potency per GCD, an off-cooldown rate and an auto-attack share.
/// Guessing them put a sage 39% low. It is now **one measured number**: the expected potency per
/// second a rotation simulator reports for a known set, with the recast it was measured at.
///
/// **What it affects.** The profile scales one job's number as a whole. It therefore moves the DPS a
/// player is shown, and it does <i>not</i> move the ranking between two players of the same job
/// competing for the same coffer, because it divides out. It does move the ranking between two
/// <i>different</i> jobs, which is why measured beats guessed here — see the flat-damage note in
/// README-DEV.
/// </summary>
public sealed record JobProfile(
    string Abbreviation,
    double PotencyPerSecond,
    double ReferenceGcd,
    double GcdShare,
    bool UsesSpellSpeed,
    bool UsesTenacity,
    double Trait,
    double AttackPowerMultiplier)
{
    /// <summary>What an unknown job gets: a middling profile, and a caveat saying so.</summary>
    public static JobProfile Default(string abbreviation, bool magical, bool tank) =>
        new(abbreviation,
            PotencyPerSecond: 180,
            ReferenceGcd: 2.50,
            GcdShare: magical ? 0.85 : 0.70,
            UsesSpellSpeed: magical,
            UsesTenacity: tank,
            Trait: TraitFor(magical, tank),
            AttackPowerMultiplier: tank ? 190 : 237);

    public bool IsDefaulted { get; init; } = true;

    /// <summary>
    /// Potency landed per second at a given recast.
    ///
    /// The measured figure holds at its own recast; away from it, only the part bound to the global
    /// cooldown scales. <see cref="GcdShare"/> says how much that is — 1 for a job whose damage is
    /// all weaponskills, 0 for one whose damage is all off-cooldown.
    ///
    /// That split is still estimated, and deliberately the only thing left that is: it decides how
    /// the number moves with a speed stat, not what the number is. Being wrong about it costs a
    /// fraction of a percent per point of speed; being wrong about the total cost 39%.
    /// </summary>
    public double PotencyPerSecondAt(double gcd)
    {
        if (gcd <= 0 || ReferenceGcd <= 0)
            return PotencyPerSecond;

        var share = Math.Clamp(GcdShare, 0, 1);

        return PotencyPerSecond * ((share * ReferenceGcd / gcd) + (1 - share));
    }

    /// <summary>
    /// The job's damage trait: a flat multiplier on everything it does, and the single easiest term to
    /// forget. Leaving it at 1.0 for every job made every magical number 23% low.
    ///
    /// **Four Etro sets, four categories, one measurement each.** Every value below is exact against a
    /// real set, and the remaining guess is only that the other jobs in a category match the one that
    /// was measured:
    ///
    /// <list type="bullet">
    /// <item><b>Tank: 1.00</b>, attack power 190. A paladin set.</item>
    /// <item><b>Melee: 1.00</b>, attack power 237. A dragoon set.</item>
    /// <item><b>Physical ranged: 1.20</b>, attack power 237. A dancer set.</item>
    /// <item><b>Magical: 1.30</b>, attack power 237. A black mage set.</item>
    /// </list>
    ///
    /// It took all four to see the shape, and each of the first three suggested a rule the next one
    /// broke: "the convention holds", then "physical is one thing", then "it is a tank exception".
    /// Physical ranged being the odd one out is not a guess about mechanics — it is what the numbers
    /// say, whatever the in-game trait happens to be called.
    /// </summary>
    public static double TraitFor(bool magical, bool tank) =>
        magical ? 1.30
        : tank ? 1.00
        : 1.00;

    /// <summary>Physical ranged is its own case, and only the role can tell it apart from a melee.</summary>
    public static double TraitForPhysicalRanged() => 1.20;
}
