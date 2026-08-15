using System;
using LootMastr.Data;

namespace LootMastr.Planning;

/// <summary>
/// The weights the simulator judges an assignment by. They are settings rather than constants
/// because "damage dealers first" means different things to different groups, and because being
/// able to see the rule that produced a suggestion is half of trusting it.
/// </summary>
[Serializable]
public sealed class PriorityRules
{
    /// <summary>
    /// How much a damage dealer finishing late counts against a plan, relative to a tank or healer.
    /// Above 1 pulls loot towards damage dealers; 1 makes the roles equal.
    /// </summary>
    public double DpsWeight { get; set; } = 1.5;

    public double TankWeight { get; set; } = 1.0;
    public double HealerWeight { get; set; } = 1.0;

    /// <summary>
    /// Weeks of simulated delay one already-received item is worth when two players are otherwise
    /// tied. Purely a tiebreak: it can never outweigh a real difference in finish week.
    /// </summary>
    public double FairnessWeight { get; set; } = 0.05;

    /// <summary>
    /// Weight on the group's last finisher against the weighted average. At 1 the plan only cares
    /// about the slowest player; lower values let it trade a late tank for two early damage dealers.
    /// </summary>
    public double LastFinisherWeight { get; set; } = 1.0;

    public double WeightFor(RaidRole role) => role switch
    {
        RaidRole.Dps => DpsWeight,
        RaidRole.Tank => TankWeight,
        RaidRole.Healer => HealerWeight,
        _ => 1.0,
    };
}
