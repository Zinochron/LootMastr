using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LootMastr.Data;
using Newtonsoft.Json.Linq;

namespace LootMastr.Import;

public sealed record ImportedSet(
    string Name,
    string Job,
    IReadOnlyDictionary<GearSlot, uint> Items,
    IReadOnlyDictionary<GearSlot, IReadOnlyList<uint>> Materia,
    uint FoodItemId,
    IReadOnlyList<string>? Skipped = null)
{
    /// <summary>
    /// Entries the parser saw and could not use, in the planner's own words.
    ///
    /// The reason this exists: a slot key this plugin does not recognise was quietly dropped, and a
    /// dropped slot is indistinguishable from a slot the set never filled. A set that imports as
    /// "eleven pieces" when it had twelve is wrong in the one way nobody checks — by looking
    /// complete.
    /// </summary>
    public IReadOnlyList<string> SkippedEntries => Skipped ?? [];

    public int MateriaCount
    {
        get
        {
            var total = 0;

            foreach (var melds in Materia.Values)
                total += melds.Count;

            return total;
        }
    }
}

public sealed record ImportResult(bool Ok, string Message, IReadOnlyList<ImportedSet> Sets)
{
    public static ImportResult Failure(string message) => new(false, message, []);
}

/// <summary>
/// Pulls a gear set out of XIVGear or Etro: which slot wants which piece, what is melded into it,
/// and what the set is eating.
///
/// Materia and food are read because a target set is the one thing that cannot be measured — the
/// equipped side comes off the character with its melds already in the totals, but a set nobody is
/// wearing has to be added up from parts.
///
/// Both planners have changed their json shape before, so the readers here are deliberately
/// tolerant and the import <b>says how much it found</b>. "12 pieces, 25 materia" is a sentence you
/// can check against the page you copied the link from; a silent zero is not.
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
        var materia = new Dictionary<GearSlot, IReadOnlyList<uint>>();

        var skipped = new List<string>();

        if (set["items"] is JObject slots)
        {
            foreach (var (key, value) in slots)
            {
                if (!XivGearSlots.TryGetValue(key, out var slot))
                {
                    skipped.Add($"slot \"{key}\"");
                    continue;
                }

                var id = value?["id"]?.Value<long>() ?? 0;
                if (id <= 0)
                {
                    skipped.Add($"{key} (no item id)");
                    continue;
                }

                items[slot] = (uint)id;

                var melds = ReadIds(value?["materia"]);
                if (melds.Count > 0)
                    materia[slot] = melds;
            }
        }

        var name = set.Value<string>("name");
        if (string.IsNullOrWhiteSpace(name))
            name = sheetName ?? "Imported set";

        return new ImportedSet(name, job, items, materia, FoodOf(set), skipped);
    }

    /// <summary>
    /// A list of item ids out of whatever shape the planner used: bare numbers, or objects with an
    /// <c>id</c>. Empty meld slots come through as 0 or -1 and are dropped.
    /// </summary>
    private static List<uint> ReadIds(JToken? token)
    {
        var result = new List<uint>();

        if (token is not JArray array)
            return result;

        foreach (var entry in array)
        {
            var id = entry.Type switch
            {
                JTokenType.Integer => entry.Value<long>(),
                JTokenType.Object => entry["id"]?.Value<long>() ?? 0,
                _ => 0,
            };

            if (id > 0)
                result.Add((uint)id);
        }

        return result;
    }

    /// <summary>The set's food, spelled three ways across the two planners and their old versions.</summary>
    private static uint FoodOf(JObject set)
    {
        foreach (var key in new[] { "food", "foodId" })
        {
            var token = set[key];
            if (token == null || token.Type == JTokenType.Null)
                continue;

            var id = token.Type == JTokenType.Object ? token["id"]?.Value<long>() ?? 0 : token.Value<long>();
            if (id > 0)
                return (uint)id;
        }

        return 0;
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
            items,
            EtroMateria(root, items),
            FoodOf(root));

        return new ImportResult(true, "Read 1 set from Etro.", [set]);
    }

    /// <summary>
    /// Etro keeps melds in one table off to the side rather than on the piece, keyed by whichever
    /// handle that version used — the piece's item id, or the slot name. Both are tried, because
    /// which one it is has changed and the cost of checking is one dictionary lookup.
    /// </summary>
    private static Dictionary<GearSlot, IReadOnlyList<uint>> EtroMateria(
        JObject root, IReadOnlyDictionary<GearSlot, uint> items)
    {
        var result = new Dictionary<GearSlot, IReadOnlyList<uint>>();

        if (root["materia"] is not JObject table)
            return result;

        foreach (var (slot, itemId) in items)
        {
            var entry = table[itemId.ToString()] ?? table[SlotKeyOf(slot)];
            if (entry == null)
                continue;

            // The inner shape is slot-number → materia item id, so the values are what matter and
            // their keys are only the meld position.
            var melds = new List<uint>();

            switch (entry)
            {
                case JObject slots:
                    foreach (var (_, value) in slots)
                    {
                        var id = value?.Type == JTokenType.Integer ? value.Value<long>() : 0;
                        if (id > 0)
                            melds.Add((uint)id);
                    }

                    break;

                case JArray:
                    melds.AddRange(ReadIds(entry));
                    break;
            }

            if (melds.Count > 0)
                result[slot] = melds;
        }

        return result;
    }

    private static string SlotKeyOf(GearSlot slot)
    {
        foreach (var (key, value) in EtroSlots)
        {
            if (value == slot)
                return key;
        }

        return string.Empty;
    }

    public void Dispose() => http.Dispose();
}
