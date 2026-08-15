using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LootMastr.Data;

namespace LootMastr.UI.Tabs;

/// <summary>
/// What the tier drops and what its books buy. Everything here is editable, because a tier
/// definition written before a patch lands should be fixable in game rather than in a rebuild.
/// </summary>
public sealed class TierTab : ITab
{
    private readonly Configuration config;
    private readonly TierCatalog tiers;
    private readonly ItemCatalog items;

    public TierTab(Configuration config, TierCatalog tiers, ItemCatalog items)
    {
        this.config = config;
        this.tiers = tiers;
        this.items = items;
    }

    public string Title => "Tier";
    public string Id => "tier";

    public void Draw()
    {
        var tier = tiers.Tier;

        ImGui.TextUnformatted(tier.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"raid i{tier.RaidItemLevel} / weapon i{tier.RaidWeaponItemLevel} / " +
                           $"tome i{tier.TomeItemLevel} → i{tier.AugmentedItemLevel}");

        DrawToolbar();
        DrawProblems();

        ImGuiHelpers.ScaledDummy(6f);
        DrawEncounters();

        ImGuiHelpers.ScaledDummy(10f);
        DrawUpgrades();

        ImGuiHelpers.ScaledDummy(10f);
        DrawExchange();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Discover exchange"))
        {
            var count = tiers.DiscoverRewards();
            Services.Chat.Print(count > 0
                                    ? $"LootMastr: found {count} exchange entries."
                                    : "LootMastr: found nothing — check that the book names below resolve.");
        }

        Widgets.HelpMarker("Reads the game's own shop data for everything this tier's books buy, " +
                           "including how many books each costs. Slots you have already assigned are kept.");

        ImGui.SameLine();

        if (ImGui.Button("Reload shipped defaults"))
            ImGui.OpenPopup("##reloadTier");

        using var popup = ImRaii.Popup("##reloadTier");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted("This throws away every change made here.");
        ImGui.Separator();

        foreach (var id in TierCatalog.ShippedTierIds())
        {
            if (!ImGui.Selectable(id))
                continue;

            if (tiers.LoadShipped(id))
                Services.Chat.Print($"LootMastr: loaded tier \"{id}\".");
            else
                Services.Chat.PrintError($"LootMastr: could not read tier \"{id}\".");
        }
    }

    private void DrawProblems()
    {
        var problems = tiers.Tier.Problems().ToList();
        if (problems.Count == 0)
            return;

        ImGuiHelpers.ScaledDummy(4f);

        foreach (var problem in problems)
            Widgets.Coloured(Widgets.Bad, $"! {problem}");
    }

    private void DrawEncounters()
    {
        ImGui.TextUnformatted("Fights");
        ImGui.Separator();

        using var table = ImRaii.Table("##encounters", 5,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Fight", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Book");
        ImGui.TableSetupColumn("Coffers");
        ImGui.TableSetupColumn("Upgrades");
        ImGui.TableSetupColumn("Drops", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            using var id = ImRaii.PushId(encounter.Index);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(encounter.Name);

            ImGui.TableNextColumn();
            if (encounter.TokenItemId == 0)
                Widgets.Coloured(Widgets.Bad, encounter.TokenItemName);
            else
                ImGui.TextUnformatted(encounter.TokenItemName);

            ImGui.TableNextColumn();
            DrawSlotToggles(encounter);

            ImGui.TableNextColumn();
            DrawUpgradeToggles(encounter);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            var drops = encounter.DropCount;
            if (ImGui.InputInt("##drops", ref drops))
            {
                encounter.DropCount = System.Math.Clamp(drops, 0, 8);
                config.Save();
            }
        }
    }

    private void DrawSlotToggles(TierEncounter encounter)
    {
        var label = encounter.DropSlots.Count == 0
                        ? "none"
                        : string.Join(", ", encounter.DropSlots.Select(s => s.ShortLabel()));

        if (ImGui.SmallButton($"{label}##slots"))
            ImGui.OpenPopup("##slotPopup");

        using var popup = ImRaii.Popup("##slotPopup");
        if (!popup.Success)
            return;

        foreach (var slot in Slots.All)
        {
            // Rings share a coffer, so only the canonical one is offered.
            if (slot == GearSlot.Ring2)
                continue;

            var on = encounter.DropSlots.Contains(slot);
            if (!ImGui.Checkbox(slot.Label(), ref on))
                continue;

            if (on)
                encounter.DropSlots.Add(slot);
            else
                encounter.DropSlots.Remove(slot);

            config.Save();
        }
    }

    /// <summary>
    /// Which side's material this fight drops. Guides disagree about which of twine and glaze is
    /// which, so this is two clicks to fix rather than a rebuild.
    /// </summary>
    private void DrawUpgradeToggles(TierEncounter encounter)
    {
        var label = encounter.UpgradeDrops.Count == 0
                        ? "none"
                        : string.Join(", ", encounter.UpgradeDrops);

        if (ImGui.SmallButton($"{label}##upgrades"))
            ImGui.OpenPopup("##upgradePopup");

        using var popup = ImRaii.Popup("##upgradePopup");
        if (!popup.Success)
            return;

        foreach (var side in new[] { GearSide.Weapon, GearSide.Left, GearSide.Right })
        {
            var on = encounter.UpgradeDrops.Contains(side);
            if (!ImGui.Checkbox(SideLabel(side), ref on))
                continue;

            if (on)
                encounter.UpgradeDrops.Add(side);
            else
                encounter.UpgradeDrops.Remove(side);

            config.Save();
        }
    }

    private void DrawUpgrades()
    {
        ImGui.TextUnformatted("Upgrade materials");
        ImGui.Separator();

        foreach (var upgrade in tiers.Tier.Upgrades)
        {
            var encounter = tiers.Tier.EncounterForUpgrade(upgrade.Side);
            var from = encounter?.Name ?? "nowhere";

            if (upgrade.ItemId == 0)
                Widgets.Coloured(Widgets.Bad, $"{SideLabel(upgrade.Side)}: \"{upgrade.ItemName}\" — no such item");
            else
                ImGui.TextUnformatted($"{SideLabel(upgrade.Side)}: {upgrade.ItemName} (drops in {from})");
        }
    }

    private void DrawExchange()
    {
        ImGui.TextUnformatted("Book exchange");
        Widgets.HelpMarker("Read from the game. Assign each coffer to the slot it fills so the planner " +
                           "knows what buying it would achieve.");
        ImGui.Separator();

        var tier = tiers.Tier;
        if (tier.Rewards.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "Nothing discovered yet.");
            return;
        }

        using var table = ImRaii.Table("##exchange", 4,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Book", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 50f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Reward");
        ImGui.TableSetupColumn("Fills", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var reward in tier.Rewards)
        {
            using var id = ImRaii.PushId((int)reward.ItemId);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(tier.Encounter(reward.Encounter)?.Name ?? $"#{reward.Encounter}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(reward.Cost.ToString());

            ImGui.TableNextColumn();
            Widgets.Icon(items.GetItem(reward.ItemId).IconId, 18f);
            ImGui.SameLine();
            ImGui.TextUnformatted(reward.ItemName);

            ImGui.TableNextColumn();
            DrawRewardTarget(reward);
        }
    }

    private void DrawRewardTarget(TierReward reward)
    {
        var label = reward.Upgrade != null
                        ? SideLabel(reward.Upgrade.Value)
                        : reward.Slot?.Label() ?? "unassigned";

        using (ImRaii.PushColor(ImGuiCol.Text, reward.IsAssigned ? Widgets.Done : Widgets.Wanted))
        {
            if (ImGui.SmallButton($"{label}##target"))
                ImGui.OpenPopup("##targetPopup");
        }

        using var popup = ImRaii.Popup("##targetPopup");
        if (!popup.Success)
            return;

        if (ImGui.Selectable("unassigned"))
        {
            reward.Slot = null;
            reward.Upgrade = null;
            config.Save();
        }

        ImGui.Separator();

        foreach (var slot in tiers.CandidateSlots(reward).Distinct())
        {
            if (!ImGui.Selectable(slot.Label()))
                continue;

            reward.Slot = Slots.CofferSlot(slot);
            reward.Upgrade = null;
            config.Save();
        }

        ImGui.Separator();

        foreach (var side in new[] { GearSide.Weapon, GearSide.Left, GearSide.Right })
        {
            if (!ImGui.Selectable(SideLabel(side)))
                continue;

            reward.Slot = null;
            reward.Upgrade = side;
            config.Save();
        }
    }

    private static string SideLabel(GearSide side) => side switch
    {
        GearSide.Weapon => "weapon upgrade",
        GearSide.Left => "armour upgrade",
        _ => "accessory upgrade",
    };

    public void Dispose() { }
}
