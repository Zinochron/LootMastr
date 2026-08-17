using System;
using System.Collections.Generic;
using System.IO;
using LootMastr.Planning.Dps;
using Newtonsoft.Json.Linq;

namespace LootMastr.Data;

/// <summary>
/// Loads the per-job rotation profiles from <c>Data/Jobs/dps-profiles.json</c>.
///
/// Data rather than code, like the tier files: a patch that moves a job's potencies should be a
/// text edit, not a rebuild. A job with no entry falls back to a profile built from its role and is
/// flagged as defaulted, so the UI can say the DPS figure is rougher than usual for that one rather
/// than silently pretending otherwise.
/// </summary>
public sealed class JobProfileCatalog
{
    private readonly JobCatalog jobs;
    private readonly Lazy<Dictionary<string, JobProfile>> profiles;

    public JobProfileCatalog(JobCatalog jobs)
    {
        this.jobs = jobs;
        profiles = new Lazy<Dictionary<string, JobProfile>>(Load, isThreadSafe: true);
    }

    /// <summary>The profile for a job, always — a missing one is filled in from the role.</summary>
    public JobProfile For(uint jobId)
    {
        var job = jobs.Get(jobId);
        var abbreviation = job.IsValid ? job.Abbreviation : "???";

        if (profiles.Value.TryGetValue(abbreviation, out var known))
            return known;

        // Magical means the job's own primary stat is intelligence or mind — which the game says, so
        // there is no list to keep. Reading it off the role instead had every caster falling through
        // as physical, because a black mage's role is simply "dps".
        var profile = JobProfile.Default(abbreviation,
                                        magical: job.PrimaryStat is 4 or 5,
                                        tank: job.Role is RaidRole.Tank);

        // Physical ranged is its own trait, and RaidRole cannot see it — melee and ranged are both
        // "dps" there, which is right for a loot queue and wrong for a damage multiplier.
        return jobs.IsPhysicalRanged(jobId)
                   ? profile with { Trait = JobProfile.TraitForPhysicalRanged() }
                   : profile;
    }

    private static Dictionary<string, JobProfile> Load()
    {
        var result = new Dictionary<string, JobProfile>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var path = Path.Combine(Services.PluginInterface.AssemblyLocation.Directory?.FullName ?? ".",
                                    "Data", "Jobs", "dps-profiles.json");

            if (!File.Exists(path))
            {
                Services.Log.Warning($"No job profiles at {path}; every job falls back to its role.");
                return result;
            }

            var root = JObject.Parse(File.ReadAllText(path));

            foreach (var entry in root["profiles"] as JArray ?? [])
            {
                var abbreviation = entry.Value<string>("job");
                if (string.IsNullOrWhiteSpace(abbreviation))
                    continue;

                result[abbreviation] = new JobProfile(
                    abbreviation,
                    entry.Value<double?>("potencyPerSecond") ?? 180,
                    entry.Value<double?>("referenceGcd") ?? 2.50,
                    entry.Value<double?>("gcdShare") ?? 0.75,
                    entry.Value<bool?>("spellSpeed") ?? false,
                    entry.Value<bool?>("tenacity") ?? false,
                    entry.Value<double?>("trait") ?? 1.0,
                    entry.Value<double?>("attackPowerMultiplier") ?? 237)
                {
                    // Only a potency figure that came from a sim drops the caveat.
                    IsDefaulted = !(entry.Value<bool?>("calibrated") ?? false),
                };
            }

            Services.Log.Information($"Loaded {result.Count} job profiles.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not read the job profiles.");
        }

        return result;
    }
}
