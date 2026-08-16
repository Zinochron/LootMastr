using System;
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

    /// <summary>Shared by every item picker; only one popup is ever open at a time.</summary>
    private string query = string.Empty;

    public void Draw()
    {
        DrawToolbar();
        DrawProblems();

        ImGuiHelpers.ScaledDummy(6f);
        DrawIdentity();

        ImGuiHelpers.ScaledDummy(6f);
        DrawEncounters();

        ImGuiHelpers.ScaledDummy(10f);
        DrawUpgrades();

        ImGuiHelpers.ScaledDummy(10f);
        DrawCostRules();

        ImGuiHelpers.ScaledDummy(10f);
        DrawExchange();
    }

    /// <summary>
    /// Book costs, by category rather than by item — which is how the game prices them, and why
    /// eight rows cover a table that would otherwise need one line per piece.
    /// </summary>
    private void DrawCostRules()
    {
        ImGui.TextUnformatted("Book costs");
        Widgets.HelpMarker("What the planner charges for buying a piece with books. These are what " +
                           "the arithmetic runs on; the discovered exchange below is only consulted " +
                           "for anything no rule covers.");
        ImGui.Separator();

        var tier = tiers.Tier;

        foreach (var mismatch in tier.CostMismatches())
            Widgets.Coloured(Widgets.Wanted, $"? {mismatch}");

        using (var table = ImRaii.Table("##costRules", 4,
                                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Buys");
                ImGui.TableSetupColumn("Books", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Covers");
                ImGui.TableHeadersRow();

                for (var i = 0; i < tier.CostRules.Count; i++)
                {
                    var rule = tier.CostRules[i];
                    using var id = ImRaii.PushId(i);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(rule.Label);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1f);
                    var cost = rule.Cost;
                    if (ImGui.InputInt("##cost", ref cost, 0))
                    {
                        rule.Cost = System.Math.Max(0, cost);
                        config.Save();
                    }

                    ImGui.TableNextColumn();
                    DrawEncounterPicker(rule);

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(rule.Upgrade != null
                                           ? SideLabel(rule.Upgrade.Value)
                                           : string.Join(", ", rule.Slots.Select(s => s.ShortLabel())));
                }
            }
        }

        DrawConversions();
    }

    private void DrawEncounterPicker(TierCostRule rule)
    {
        var name = tiers.Tier.Encounter(rule.Encounter)?.Name ?? $"#{rule.Encounter}";

        if (ImGui.SmallButton($"{name}##from"))
            ImGui.OpenPopup("##fromPopup");

        using var popup = ImRaii.Popup("##fromPopup");
        if (!popup.Success)
            return;

        foreach (var encounter in tiers.Tier.Encounters.OrderBy(e => e.Index))
        {
            if (!ImGui.Selectable(encounter.Name))
                continue;

            rule.Encounter = encounter.Index;
            config.Save();
        }
    }

    /// <summary>
    /// The last fight's books trade for earlier ones, which is worth stating explicitly: it changes
    /// who is stuck. A player short on accessory books but sitting on spare weapon books is not.
    /// </summary>
    private void DrawConversions()
    {
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextUnformatted("Trading books in");

        var tier = tiers.Tier;

        if (tier.Conversions.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No trades — every book is only good for its own fight.");
            return;
        }

        foreach (var conversion in tier.Conversions)
        {
            var from = tier.Encounter(conversion.FromEncounter)?.Name ?? $"#{conversion.FromEncounter}";
            var into = string.Join(", ", conversion.ToEncounters.Select(e => tier.Encounter(e)?.Name ?? $"#{e}"));

            ImGui.TextUnformatted($"{conversion.Ratio} × {from} → 1 × any of: {into}");
        }

        Widgets.HelpMarker("The planner only trades books it does not need for that fight's own " +
                           "rewards, so a weapon nobody could then afford never gets traded away.");
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Discover exchange"))
        {
            // Standing at the NPC settles which shop this tier means, so that is tried first and
            // the books it finds are what the rest of the discovery then keys on.
            var fromShop = tiers.DiscoverBooksFromOpenShop();
            if (!string.IsNullOrEmpty(fromShop))
                Services.Chat.Print($"LootMastr: {fromShop}");

            var count = tiers.DiscoverRewards();
            Services.Chat.Print(count > 0
                                    ? $"LootMastr: found {count} exchange entries."
                                    : "LootMastr: found nothing — check that the books below resolve.");
        }

        Widgets.HelpMarker("Reads the game's own shop data for everything this tier's books buy, " +
                           "including how many books each costs. Slots you have already assigned are kept.\n\n" +
                           "Do this standing at the exchange NPC with the shop open and it will read " +
                           "the tier's books off the window first, so nothing has to be set up by hand.");

        ImGui.SameLine();
        if (ImGui.Button("Save tier"))
            Services.Chat.Print($"LootMastr: {tiers.SaveToFile()}");

        Widgets.HelpMarker("Writes this tier to its own json file, so it can be loaded again later " +
                           "or handed to someone else. A tier built here is then as durable as one " +
                           "that shipped with the plugin.");

        ImGui.SameLine();
        if (ImGui.Button("New tier"))
        {
            tiers.CreateBlank();
            Services.Chat.Print("LootMastr: started a blank tier — fill in the item levels and books.");
        }

        Widgets.HelpMarker("Four empty fights and three materials to fill in. Use this to set up an " +
                           "older tier for testing.");

        ImGui.SameLine();
        if (ImGui.Button("Load tier"))
            ImGui.OpenPopup("##reloadTier");

        using var popup = ImRaii.Popup("##reloadTier");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted("Loading throws away every change made to the current tier.");
        ImGui.Separator();

        var any = false;

        foreach (var id in TierCatalog.ShippedTierIds())
        {
            any = true;

            if (!ImGui.Selectable(id))
                continue;

            if (tiers.LoadShipped(id))
                Services.Chat.Print($"LootMastr: loaded tier \"{id}\".");
            else
                Services.Chat.PrintError($"LootMastr: could not read tier \"{id}\".");
        }

        if (!any)
            ImGui.TextDisabled("No tier files found.");
    }

    /// <summary>
    /// Name and item levels. The levels are editable because they are what gear classification
    /// actually runs on — a wrong one here is what makes a whole roster read as raid gear.
    /// </summary>
    private void DrawIdentity()
    {
        var tier = tiers.Tier;

        ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
        var name = tier.Name;
        if (ImGui.InputText("Tier name", ref name, 64))
        {
            tier.Name = name;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
        var id = tier.Id;
        if (ImGui.InputText("Id", ref id, 48))
        {
            tier.Id = id.Trim();
            config.ActiveTierId = tier.Id;
            config.Save();
        }

        Widgets.HelpMarker("File name this tier saves to. Lower case, no spaces.");

        Level("Raid gear", () => tier.RaidItemLevel, v => tier.RaidItemLevel = v);
        ImGui.SameLine();
        Level("Raid weapon", () => tier.RaidWeaponItemLevel, v => tier.RaidWeaponItemLevel = v);
        ImGui.SameLine();
        Level("Tomestone", () => tier.TomeItemLevel, v => tier.TomeItemLevel = v);
        ImGui.SameLine();
        Level("Augmented", () => tier.AugmentedItemLevel, v => tier.AugmentedItemLevel = v);

        Widgets.HelpMarker("Item levels decide what belongs to this tier. Raid gear and augmented " +
                           "tomestone gear normally share one — that is expected, and the " +
                           "\"Augmented\" in the name is what tells them apart.");
    }

    private void Level(string label, Func<ushort> get, Action<ushort> set)
    {
        ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);

        var value = (int)get();
        if (!ImGui.InputInt($"{label}##ilvl", ref value, 0))
            return;

        set((ushort)System.Math.Clamp(value, 0, ushort.MaxValue));
        config.Save();
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
            DrawBookPicker(encounter);

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

    /// <summary>
    /// Picks the fight's book by searching the item list. Typing an exact name was the old way and
    /// it only ever worked for the tier that happened to ship.
    /// </summary>
    private void DrawBookPicker(TierEncounter encounter)
    {
        var label = string.IsNullOrWhiteSpace(encounter.TokenItemName) ? "pick a book…" : encounter.TokenItemName;
        var unresolved = encounter.TokenItemId == 0;

        using (ImRaii.PushColor(ImGuiCol.Text, unresolved ? Widgets.Bad : Widgets.Done))
        {
            if (ImGui.SmallButton($"{label}##book"))
            {
                query = string.Empty;
                ImGui.OpenPopup("##bookPopup");
            }
        }

        if (unresolved && !string.IsNullOrWhiteSpace(encounter.TokenItemName))
            Widgets.Tooltip($"\"{encounter.TokenItemName}\" does not match any item.");

        using var popup = ImRaii.Popup("##bookPopup");
        if (!popup.Success)
            return;

        if (!Widgets.ItemSearch(items, ref query, out var picked))
            return;

        encounter.TokenItemId = picked;
        encounter.TokenItemName = items.GetItemName(picked);
        config.Save();
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
            using var id = ImRaii.PushId((int)upgrade.Side);

            var encounter = tiers.Tier.EncounterForUpgrade(upgrade.Side);
            var from = encounter?.Name ?? "nowhere";

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{SideLabel(upgrade.Side)}:");
            ImGui.SameLine();

            var label = string.IsNullOrWhiteSpace(upgrade.ItemName) ? "pick a material…" : upgrade.ItemName;

            using (ImRaii.PushColor(ImGuiCol.Text, upgrade.ItemId == 0 ? Widgets.Bad : Widgets.Done))
            {
                if (ImGui.SmallButton($"{label}##material"))
                {
                    query = string.Empty;
                    ImGui.OpenPopup("##materialPopup");
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"drops in {from}");

            using var popup = ImRaii.Popup("##materialPopup");
            if (!popup.Success)
                continue;

            if (!Widgets.ItemSearch(items, ref query, out var picked))
                continue;

            upgrade.ItemId = picked;
            upgrade.ItemName = items.GetItemName(picked);
            config.Save();
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
