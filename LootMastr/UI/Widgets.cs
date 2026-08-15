using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using LootMastr.Data;

namespace LootMastr.UI;

public static class Widgets
{
    public static readonly Vector4 Done = new(0.35f, 0.75f, 0.40f, 1f);
    public static readonly Vector4 Wanted = new(0.90f, 0.70f, 0.25f, 1f);
    public static readonly Vector4 Muted = new(0.55f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 Bad = new(0.90f, 0.35f, 0.35f, 1f);

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

    /// <summary>Colour a need cell by what it still costs the raid.</summary>
    public static Vector4 ColourFor(GearSource source, bool satisfied)
    {
        if (!source.NeedsRaidResource())
            return Muted;

        return satisfied ? Done : Wanted;
    }
}
