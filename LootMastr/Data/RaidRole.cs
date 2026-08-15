namespace LootMastr.Data;

/// <summary>
/// Raid roles as the distribution cares about them. The game splits damage into melee, physical
/// ranged and caster; loot priority does not, so they collapse into one.
///
/// Kept in its own file with no game dependencies so the planner can be compiled on its own.
/// </summary>
public enum RaidRole
{
    Unknown,
    Tank,
    Healer,
    Dps,
}
