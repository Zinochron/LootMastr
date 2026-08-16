using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.NativeWrapper;

namespace LootMastr.Data;

/// <summary>
/// Reading values out of a window by index. Thin on purpose — the value the callers want is
/// always "what is at slot N", and every one of those indices is written down in
/// <c>README-DEV.md</c> against the capture it came from.
/// </summary>
public static class AddonReader
{
    public static AtkUnitBasePtr Find(string name) => Services.GameGui.GetAddonByName(name);

    public static bool IsOpen(string name)
    {
        var addon = Find(name);
        return !addon.IsNull && addon.IsVisible;
    }

    public static List<AtkValuePtr> Values(string name)
    {
        var addon = Find(name);
        return addon.IsNull ? [] : addon.AtkValues.ToList();
    }

    public static int? Int(IReadOnlyList<AtkValuePtr> values, int index)
    {
        if (index < 0 || index >= values.Count)
            return null;

        return values[index].TryGet<int>(out var value) ? value : null;
    }

    /// <summary>
    /// Text at an index. Names arrive as plain, const or managed strings depending on how the game
    /// happened to store them, and all three stringify the same, so the type is not branched on.
    /// </summary>
    public static string? Text(IReadOnlyList<AtkValuePtr> values, int index)
    {
        if (index < 0 || index >= values.Count)
            return null;

        var text = values[index].GetValue()?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
