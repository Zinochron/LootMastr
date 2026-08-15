using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using LootMastr.Data;
using LootMastr.Roster;

namespace LootMastr.UI;

public static class Widgets
{
    public static readonly Vector4 Done = new(0.35f, 0.75f, 0.40f, 1f);
    public static readonly Vector4 Wanted = new(0.90f, 0.70f, 0.25f, 1f);
    public static readonly Vector4 Muted = new(0.55f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 Bad = new(0.90f, 0.35f, 0.35f, 1f);

    /// <summary>
    /// Tomestone upgrades, kept apart from raid drops. Blue against the orange rather than another
    /// warm colour: the two have to be told apart at a glance in a dense grid, and orange and blue
    /// stay distinct for the red-green colour blind, who are most of the people this would fail.
    /// </summary>
    public static readonly Vector4 Augment = new(0.40f, 0.68f, 0.95f, 1f);

    /// <summary>Awarded but not on the character — done for the raid, still open for the player.</summary>
    public static readonly Vector4 NotWorn = new(0.82f, 0.55f, 0.95f, 1f);

    public static void Icon(uint iconId, float size = 20f)
    {
        var scaled = new Vector2(size * ImGuiHelpers.GlobalScale);

        if (iconId == 0)
        {
            ImGui.Dummy(scaled);
            return;
        }

        var texture = Services.Textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        if (texture == null)
            ImGui.Dummy(scaled);
        else
            ImGui.Image(texture.Handle, scaled);
    }

    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void Coloured(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Colour a need cell by its state. The glyph in the label carries the same distinction, so
    /// nothing here is the only way to read a cell.
    /// </summary>
    public static Vector4 ColourFor(GearSource source, SlotState state) => state switch
    {
        SlotState.NotPlanned => Muted,
        SlotState.Done => Done,
        SlotState.AssignedNotWorn => NotWorn,
        _ => source == GearSource.TomeAugmented ? Augment : Wanted,
    };

    /// <summary>
    /// The mark after a cell's label. Target is the word, actual is the mark: nothing means still
    /// owed, a tick means worn, and a tick with a warning means handed over but not on.
    /// </summary>
    public static string MarkFor(SlotState state) => state switch
    {
        SlotState.Done => " ✓",
        SlotState.AssignedNotWorn => " ✓!",
        _ => string.Empty,
    };
}
