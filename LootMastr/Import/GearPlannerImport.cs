using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LootMastr.Data;
using Newtonsoft.Json.Linq;

namespace LootMastr.Import;

public sealed record ImportedSet(string Name, string Job, IReadOnlyDictionary<GearSlot, uint> Items);

public sealed record ImportResult(bool Ok, string Message, IReadOnlyList<ImportedSet> Sets)
{
    public static ImportResult Failure(string message) => new(false, message, []);
}

/// <summary>
/// Pulls a gear set out of XIVGear or Etro. Only the item ids matter here — materia and stats are
/// somebody else's problem; LootMastr just needs to know which slot wants which piece.
/// </summary>
public sealed class GearPlannerImport : IDisposable
{
    private static readonly Regex UuidPattern =
        new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>XIVGear slot names. Note the singular <c>Hand</c> and <c>Wrist</c>.</summary>
    private static readonly Dictionary<string, GearSlot> XivGearSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weapon"] = GearSlot.Weapon,
        ["OffHand"] = GearSlot.OffHand,
        ["Head"] = GearSlot.Head,
        ["Body"] = GearSlot.Body,
        ["Hand"] = GearSlot.Hands,
        ["Legs"] = GearSlot.Legs,
        ["Feet"] = GearSlot.Feet,
        ["Ears"] = GearSlot.Earrings,
        ["Neck"] = GearSlot.Necklace,
        ["Wrist"] = GearSlot.Bracelets,
        ["RingLeft"] = GearSlot.Ring1,
        ["RingRight"] = GearSlot.Ring2,
    };

    private static readonly Dictionary<string, GearSlot> EtroSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = GearSlot.Weapon,
        ["offHand"] = GearSlot.OffHand,
        ["head"] = GearSlot.Head,
        ["body"] = GearSlot.Body,
        ["hands"] = GearSlot.Hands,
        ["legs"] = GearSlot.Legs,
        ["feet"] = GearSlot.Feet,
        ["ears"] = GearSlot.Earrings,
        ["neck"] = GearSlot.Necklace,
        ["wrists"] = GearSlot.Bracelets,
        ["fingerL"] = GearSlot.Ring1,
        ["fingerR"] = GearSlot.Ring2,
    };

    public async Task<ImportResult> FetchAsync(string input, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ImportResult.Failure("Paste an XIVGear or Etro link first.");

        if (input.Contains("bis|", StringComparison.OrdinalIgnoreCase))
        {
            return ImportResult.Failure(
                "XIVGear \"bis\" links cannot be read directly. Open the set on xivgear.app, " +
                "export it as a shortlink, and paste that instead.");
        }

        var match = UuidPattern.Match(input);
        if (!match.Success)
            return ImportResult.Failure("No gear set id found in that link.");

        var uuid = match.Value;
        var preferEtro = input.Contains("etro.gg", StringComparison.OrdinalIgnoreCase);

        // A bare id could be from either site, so whichever is not the obvious guess is still tried.
        var attempts = preferEtro
                           ? new Func<string, CancellationToken, Task<ImportResult>>[] { FetchEtro, FetchXivGear }
                           : [FetchXivGear, FetchEtro];

        ImportResult? last = null;

        foreach (var attempt in attempts)
        {
            try
            {
                var result = await attempt(uuid, token).ConfigureAwait(false);
                if (result.Ok)
                    return result;

                last = result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Services.Log.Warning(ex, $"Gear set import failed for {uuid}.");
                last = ImportResult.Failure(ex.Message);
            }
        }

        return last ?? ImportResult.Failure("Could not read that gear set.");
    }

    private async Task<ImportResult> FetchXivGear(string uuid, CancellationToken token)
    {
        var response = await http.GetAsync($"https://api.xivgear.app/shortlink/{uuid}", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ImportResult.Failure($"XIVGear returned {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var root = JObject.Parse(body);

        var job = root.Value<string>("job") ?? string.Empty;
        var sets = new List<ImportedSet>();

        // A shortlink is either a whole sheet with a "sets" array, or one bare set.
        if (root["sets"] is JArray array)
        {
            foreach (var entry in array)
            {
                if (entry is JObject set)
                    sets.Add(ReadXivGearSet(set, job, root.Value<string>("name")));
            }
        }
        else
        {
            sets.Add(ReadXivGearSet(root, job, root.Value<string>("name")));
        }

        sets.RemoveAll(s => s.Items.Count == 0);

        return sets.Count == 0
                   ? ImportResult.Failure("That XIVGear sheet has no gear in it.")
                   : new ImportResult(true, $"Read {sets.Count} set(s) from XIVGear.", sets);
    }

    private static ImportedSet ReadXivGearSet(JObject set, string job, string? sheetName)
    {
        var items = new Dictionary<GearSlot, uint>();

        if (set["items"] is JObject slots)
        {
            foreach (var (key, value) in slots)
            {
                if (!XivGearSlots.TryGetValue(key, out var slot))
                    continue;

                var id = value?["id"]?.Value<long>() ?? 0;
                if (id > 0)
                    items[slot] = (uint)id;
            }
        }

        var name = set.Value<string>("name");
        if (string.IsNullOrWhiteSpace(name))
            name = sheetName ?? "Imported set";

        return new ImportedSet(name, job, items);
    }

    private async Task<ImportResult> FetchEtro(string uuid, CancellationToken token)
    {
        var response = await http.GetAsync($"https://etro.gg/api/gearsets/{uuid}/", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ImportResult.Failure($"Etro returned {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var root = JObject.Parse(body);

        var items = new Dictionary<GearSlot, uint>();

        foreach (var (key, slot) in EtroSlots)
        {
            var token2 = root[key];
            if (token2 == null || token2.Type == JTokenType.Null)
                continue;

            // Etro has returned both a bare id and an object with an id over the years.
            var id = token2.Type == JTokenType.Object ? token2["id"]?.Value<long>() ?? 0 : token2.Value<long>();
            if (id > 0)
                items[slot] = (uint)id;
        }

        if (items.Count == 0)
            return ImportResult.Failure("That Etro set has no gear in it.");

        var set = new ImportedSet(
            root.Value<string>("name") ?? "Imported set",
            root.Value<string>("jobAbbrev") ?? string.Empty,
            items);

        return new ImportResult(true, "Read 1 set from Etro.", [set]);
    }

    public void Dispose() => http.Dispose();
}
