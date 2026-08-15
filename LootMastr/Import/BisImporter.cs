using System;
using System.Collections.Generic;
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
    private readonly GearPlannerImport source = new();

    private Task<ImportResult>? running;
    private RosterMember? target;

    public BisImporter(Configuration config, GearClassifier classifier, JobCatalog jobs)
    {
        this.config = config;
        this.classifier = classifier;
        this.jobs = jobs;
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
                continue;
            }

            need.BisItemId = itemId;
            need.Source = classifier.Classify(itemId);

            if (need.Source.NeedsRaidResource())
                fromRaid++;
        }

        Choosing = null;
        Choices = [];
        Status = $"{member.Name}: {set.Name} — {fromRaid} piece(s) come out of the raid.";
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

    public void Cancel()
    {
        Choosing = null;
        Choices = [];
        Status = string.Empty;
    }

    public void Dispose() => source.Dispose();
}
