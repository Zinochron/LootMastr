using System;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace LootMastr.Data;

/// <summary>
/// Prints the game data a damage model needs but that nothing in the plugin can confirm from a
/// schema alone.
///
/// Five things were read out of the installed assemblies and four of them could only be read as
/// shapes, not values. Rather than build on a plausible reading of each, this dumps them next to
/// something checkable — the character sheet, a known item — so they can be settled by looking.
/// Every unknown here is one line of output away from certain, which is a great deal cheaper than
/// a damage estimate that is quietly ten percent out.
/// </summary>
public static class StatProbe
{
    public static unsafe string Run(ItemCatalog items, JobCatalog jobs)
    {
        var report = new StringBuilder();
        report.AppendLine($"LootMastr {Build.Version} stat probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        ParamGrow(report);
        Jobs(report, jobs);
        LocalAttributes(report);
        HighQuality(report, items);
        EquippedMateria(report, items);
        ExamineContainer(report, items);

        return report.ToString();
    }

    /// <summary>
    /// The one genuine blocker. The damage formula needs MAIN, SUB and DIV per level — 440 / 420 /
    /// 2780 at level 100 by community reckoning — and <c>ParamGrow</c> has no column with any of
    /// those names. If those three numbers appear below, the mapping is settled and the hardcoded
    /// table can be derived from the sheet instead.
    /// </summary>
    private static void ParamGrow(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("ParamGrow — looking for MAIN / SUB / DIV:");

        var sheet = Services.Data.GetExcelSheet<ParamGrow>();

        foreach (var level in new uint[] { 80, 90, 100 })
        {
            if (!sheet.TryGetRow(level, out var row))
            {
                report.AppendLine($"  level {level}: no row");
                continue;
            }

            report.AppendLine($"  level {level}: BaseSpeed={row.BaseSpeed} LevelModifier={row.LevelModifier} " +
                              $"HpModifier={row.HpModifier} MpModifier={row.MpModifier} " +
                              $"ItemLevelSync={row.ItemLevelSync} ScaledQuestXP={row.ScaledQuestXP}");
        }
    }

    /// <summary>
    /// Whether <c>ClassJob.PrimaryStat</c> is a <c>BaseParam</c> row id. If it is, a paladin reads 1
    /// (strength), a ninja 2 (dexterity), a black mage 4 (intelligence) and a sage 5 (mind).
    /// </summary>
    private static void Jobs(StringBuilder report, JobCatalog jobs)
    {
        report.AppendLine();
        report.AppendLine("ClassJob — PrimaryStat and the job modifier:");

        var sheet = Services.Data.GetExcelSheet<ClassJob>();

        foreach (var abbreviation in new[] { "PLD", "NIN", "BLM", "SGE", "SAM", "WHM" })
        {
            var job = jobs.FindByAbbreviation(abbreviation);
            if (!job.IsValid || !sheet.TryGetRow(job.Id, out var row))
                continue;

            report.AppendLine($"  {abbreviation}: PrimaryStat={row.PrimaryStat} " +
                              $"STR={row.ModifierStrength} DEX={row.ModifierDexterity} " +
                              $"INT={row.ModifierIntelligence} MND={row.ModifierMind}");
        }
    }

    /// <summary>
    /// What the plugin measures against what the character sheet shows. If these disagree, nothing
    /// downstream is worth reading.
    /// </summary>
    private static unsafe void LocalAttributes(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("Your measured attributes — compare against the character window:");

        var state = PlayerState.Instance();
        if (state == null || !state->IsLoaded)
        {
            report.AppendLine("  not loaded");
            return;
        }

        report.AppendLine($"  job={state->CurrentClassJobId} level={state->CurrentLevel}");

        Line("Strength", Attributes.Strength);
        Line("Dexterity", Attributes.Dexterity);
        Line("Intelligence", Attributes.Intelligence);
        Line("Mind", Attributes.Mind);
        Line("Critical hit", Attributes.CriticalHit);
        Line("Direct hit", Attributes.DirectHitRate);
        Line("Determination", Attributes.Determination);
        Line("Skill speed", Attributes.SkillSpeed);
        Line("Spell speed", Attributes.SpellSpeed);
        Line("Tenacity", Attributes.Tenacity);
        Line("Physical damage", Attributes.PhysicalDamage);
        Line("Magical damage", Attributes.MagicalDamage);
        Line("Delay", Attributes.Delay);

        void Line(string name, uint id) =>
            report.AppendLine($"  {name,-16} [{id,2}] = {state->GetAttributeByIndex((PlayerAttribute)id)}");
    }

    /// <summary>
    /// Whether <c>BaseParamSpecial</c> is the high quality bonus on top of the normal values, or the
    /// high quality totals. Printed side by side on the first craftable piece of gear found, where
    /// a delta is small and a total is not.
    /// </summary>
    private static void HighQuality(StringBuilder report, ItemCatalog items)
    {
        report.AppendLine();
        report.AppendLine("BaseParamSpecial — delta on top, or the HQ totals?");

        var sample = Enumerable.Range(1, 45000)
                               .Select(id => (uint)id)
                               .Where(id => items.TryGetStats(id, out var s) && s.CanBeHq && s.Params.Count > 0)
                               .Take(2)
                               .ToList();

        if (sample.Count == 0)
        {
            report.AppendLine("  found no craftable gear to compare");
            return;
        }

        foreach (var id in sample)
        {
            items.TryGetStats(id, out var stats);

            report.AppendLine($"  {items.GetItemName(id)} (i{items.GetItem(id).ItemLevel})");
            report.AppendLine($"    normal: {Describe(stats.Params)}");
            report.AppendLine($"    special: {Describe(stats.HqParams)}");
        }

        static string Describe(System.Collections.Generic.IReadOnlyList<ItemStat> stats) =>
            stats.Count == 0 ? "none" : string.Join(", ", stats.Select(s => $"[{s.BaseParam}]={s.Value}"));
    }

    /// <summary>
    /// Whether <c>InventoryItem.Materia</c> holds <c>Materia</c> sheet row ids or item ids. The
    /// catalogue's reverse table is keyed by item id, so if these are sheet rows it needs the other
    /// direction as well.
    /// </summary>
    private static unsafe void EquippedMateria(StringBuilder report, ItemCatalog items)
    {
        report.AppendLine();
        report.AppendLine("Your equipped items, with melds:");

        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);

        if (container == null || !container->IsLoaded)
        {
            report.AppendLine("  equipped container not loaded");
            return;
        }

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;

            var melds = new StringBuilder();

            for (byte m = 0; m < slot->GetMateriaCount(); m++)
            {
                var id = slot->GetMateriaId(m);
                var grade = slot->GetMateriaGrade(m);

                var known = items.TryGetMateria(id, out var effect)
                                ? $"item, [{effect.BaseParam}]+{effect.Value}"
                                : "not an item id — probably a Materia row";

                melds.Append($" [{m}] id={id} grade={grade} ({known})");
            }

            report.AppendLine($"  {items.GetItemName(slot->ItemId)} hq={slot->IsHighQuality()}" +
                              (melds.Length == 0 ? " — no melds" : melds.ToString()));
        }
    }

    /// <summary>
    /// Whether the examine container holds the inspected character's real items — melds included.
    /// If it does, the one gap in the model closes: <c>AgentInspect</c> gives item ids only, so the
    /// gain of a new piece is currently computed without its materia.
    ///
    /// Run this with somebody's examine window open.
    /// </summary>
    private static unsafe void ExamineContainer(StringBuilder report, ItemCatalog items)
    {
        report.AppendLine();
        report.AppendLine("Examine container (open somebody's examine window first):");

        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.Examine);

        if (container == null)
        {
            report.AppendLine("  no such container");
            return;
        }

        report.AppendLine($"  loaded={container->IsLoaded} size={container->Size}");

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;

            report.AppendLine($"  [{i}] {items.GetItemName(slot->ItemId)} " +
                              $"hq={slot->IsHighQuality()} melds={slot->GetMateriaCount()}");
        }
    }
}
