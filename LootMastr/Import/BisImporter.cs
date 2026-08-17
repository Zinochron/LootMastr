using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.Import;

/// <summary>
/// Drives one gear set import at a time and turns the result into need list entries. The fetch is
/// asynchronous but nothing is applied off the framework thread: <see cref="Poll"/> is called from
/// the draw loop and does the applying there.
/// </summary>
public sealed class BisImporter : IDisposable
{
    private readonly Configuration config;
    private readonly GearClassifier classifier;
    private readonly JobCatalog jobs;
    private readonly TierCatalog tiers;
    private readonly ItemCatalog items;
    private readonly GearPlannerImport source = new();

    private Task<ImportResult>? running;
    private RosterMember? target;

    public BisImporter(Configuration config, GearClassifier classifier, JobCatalog jobs,
                       TierCatalog tiers, ItemCatalog items)
    {
        this.config = config;
        this.classifier = classifier;
        this.jobs = jobs;
        this.tiers = tiers;
        this.items = items;
    }

    public string Status { get; private set; } = string.Empty;

    public bool IsBusy => running is { IsCompleted: false };

    /// <summary>Member the last import was for, and the sets it returned when there was a choice.</summary>
    public RosterMember? Choosing { get; private set; }

    public IReadOnlyList<ImportedSet> Choices { get; private set; } = [];

    public void Start(RosterMember member, string url)
    {
        if (IsBusy)
            return;

        Choosing = null;
        Choices = [];
        Status = "Fetching…";

        target = member;
        member.GearPlannerUrl = url.Trim();
        config.Save();

        running = Task.Run(() => source.FetchAsync(member.GearPlannerUrl));
    }

    /// <summary>Called every frame from the roster tab. Cheap when nothing is in flight.</summary>
    public void Poll()
    {
        if (running is not { IsCompleted: true } || target == null)
            return;

        var task = running;
        var member = target;
        running = null;
        target = null;

        if (task.IsFaulted)
        {
            Status = task.Exception?.GetBaseException().Message ?? "Import failed.";
            return;
        }

        var result = task.Result;
        if (!result.Ok)
        {
            Status = result.Message;
            return;
        }

        if (result.Sets.Count == 1)
        {
            Apply(member, result.Sets[0]);
            return;
        }

        // Sheets usually hold several sets — current gear, mid tier, final. Which one is BiS is a
        // judgement call, so it is the user's.
        Choosing = member;
        Choices = result.Sets;
        Status = $"{result.Sets.Count} sets in that sheet — pick one.";
    }

    public void Apply(RosterMember member, ImportedSet set)
    {
        var job = jobs.FindByAbbreviation(set.Job);
        if (job.IsValid)
            member.JobId = job.Id;

        SeedItemLevelsFrom(set);

        if (!BelongsToTier(set))
        {
            // Refused rather than applied. A set from another tier classifies as crafted from top
            // to bottom, which fills the row in with something that looks deliberate.
            member.ImportWarning =
                $"\"{set.Name}\" is not gear from {tiers.Tier.Name} — nothing was changed. " +
                "Check the link, or switch tier.";

            Status = $"{member.Name}: {member.ImportWarning}";
            Choosing = null;
            Choices = [];
            config.Save();
            return;
        }

        member.ImportWarning = string.Empty;
        var fromRaid = 0;

        foreach (var slot in Slots.All)
        {
            var need = member.NeedFor(slot);

            if (!set.Items.TryGetValue(slot, out var itemId))
            {
                // Nothing planned for this slot. The obtained flags are left alone: they mean
                // nothing while the source is None, and clearing them would lose real history if
                // the set is re-imported after a correction.
                need.Source = GearSource.None;
                need.BisItemId = 0;
                need.BisMateria = [];
                continue;
            }

            need.BisItemId = itemId;
            need.Source = classifier.Classify(itemId);
            need.BisMateria = set.Materia.TryGetValue(slot, out var melds) ? [..melds] : [];

            if (need.Source.NeedsRaidResource())
                fromRaid++;
        }

        member.TargetFoodItemId = set.FoodItemId;

        Choosing = null;
        Choices = [];

        // The materia and food counts are in the message on purpose: both planners have changed
        // their json shape before, and "25 materia" against a page you can see is the cheapest
        // possible check that the reader still understands it.
        var extras = new List<string> { $"{fromRaid} piece(s) come out of the raid" };

        if (set.MateriaCount > 0)
            extras.Add($"{set.MateriaCount} materia");

        if (set.FoodItemId != 0)
            extras.Add($"food: {items.GetItemName(set.FoodItemId)}");

        Status = $"{member.Name}: {set.Name} — {string.Join(", ", extras)}.";
        config.Save();
    }

    /// <summary>
    /// Re-runs the classification over the item ids already stored, without going back to the
    /// gear planner. Needed whenever the rules change under an existing roster — discovering the
    /// tier's augmented set, or correcting how augmented gear is spelled, both make previously
    /// imported sets readable in a way they were not before.
    /// </summary>
    public int Reclassify()
    {
        var changed = 0;

        foreach (var member in config.Roster)
        {
            foreach (var slot in Slots.All)
            {
                var need = member.NeedFor(slot);
                if (need.BisItemId == 0)
                    continue;

                var source = classifier.Classify(need.BisItemId);
                if (source == need.Source)
                    continue;

                need.Source = source;
                changed++;
            }
        }

        Status = changed == 0
                     ? "Nothing changed — every imported piece was already filed correctly."
                     : $"Re-filed {changed} slot(s) from the imported sets.";

        if (changed > 0)
            config.Save();

        return changed;
    }

    /// <summary>
    /// Whether this set is gear from the tier at all. Judged on whether anything in it classifies
    /// as raid, augmented or tomestone gear — a set from a different tier has none of that, and
    /// would otherwise come out as a full row of "crafted".
    /// </summary>
    private bool BelongsToTier(ImportedSet set)
    {
        var tier = tiers.Tier;
        if (!tier.IsTierItemLevel(tier.RaidItemLevel))
            return true;

        return set.Items.Values.Any(id => classifier.Classify(id).NeedsRaidResource() ||
                                          classifier.Classify(id) == GearSource.Tome);
    }

    /// <summary>
    /// Fills a tier's item levels in from a gear set when it has none of its own.
    ///
    /// Every savage set uses the raid weapon, and that weapon sits five above the rest of the raid
    /// gear and above augmented tomestone gear, which is what makes one number enough.
    /// </summary>
    private void SeedItemLevelsFrom(ImportedSet set)
    {
        var tier = tiers.Tier;
        if (tier.RaidItemLevel != 0 || tier.RaidWeaponItemLevel != 0)
            return;

        if (!set.Items.TryGetValue(GearSlot.Weapon, out var weaponId))
            return;

        var weapon = items.GetItem(weaponId).ItemLevel;
        if (weapon < 10)
            return;

        tier.RaidWeaponItemLevel = weapon;
        tier.RaidItemLevel = (ushort)(weapon - 5);
        tier.AugmentedItemLevel = tier.RaidItemLevel;
        tier.TomeItemLevel = (ushort)(tier.RaidItemLevel - 10);

        Status = $"Set the tier's item levels from the weapon: raid {tier.RaidItemLevel}, " +
                 $"weapon {tier.RaidWeaponItemLevel}, tome {tier.TomeItemLevel}.";

        config.Save();
    }

    public void Cancel()
    {
        Choosing = null;
        Choices = [];
        Status = string.Empty;
    }

    public void Dispose() => source.Dispose();
}
