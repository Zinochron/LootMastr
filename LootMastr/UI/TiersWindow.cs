using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using LootMastr.Data;

namespace LootMastr.UI;

/// <summary>
/// What the tier drops and what its books buy. Everything here is editable, because a tier
/// definition written before a patch lands should be fixable in game rather than in a rebuild.
///
/// Its own window, for the same reason managing statics is: this is setup, not a raid night. It is
/// touched once when a tier opens and then only when something in it turns out to be wrong, and
/// while it is open it wants the whole screen — four item levels, four fights, three materials, a
/// price table and the discovered exchange do not want to share a tab bar with the loot list.
/// </summary>
public sealed class TiersWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly TierCatalog tiers;
    private readonly ItemCatalog items;

    public TiersWindow(Configuration config, TierCatalog tiers, ItemCatalog items)
        : base("LootMastr — tier###LootMastrTier")
    {
        this.config = config;
        this.tiers = tiers;
        this.items = items;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        Size = new Vector2(900, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Shared by every item picker; only one popup is ever open at a time.</summary>
    private string query = string.Empty;

    public void Open()
    {
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        if (!config.CanWrite)
        {
            Widgets.ReadOnlyNotice("the tier belongs to the static");
            ImGuiHelpers.ScaledDummy(4f);
        }

        using var gate = ImRaii.Disabled(!config.CanWrite);

        DrawToolbar();
        DrawProblems();

        ImGuiHelpers.ScaledDummy(6f);
        DrawIdentity();

        ImGuiHelpers.ScaledDummy(6f);
        DrawTomestones();

        ImGuiHelpers.ScaledDummy(6f);
        DrawEncounters();

        ImGuiHelpers.ScaledDummy(10f);
        DrawUpgrades();

        ImGuiHelpers.ScaledDummy(10f);
        DrawSpecials();

        ImGuiHelpers.ScaledDummy(10f);
        DrawExchange();
    }

    /// <summary>
    /// Whether the last fight's books trade down, stated in one line.
    ///
    /// It changes who is stuck, so it has to be visible: a player short on accessory books but
    /// sitting on spare weapon books is not short on anything. It used to sit under an editable
    /// table of the tier's book costs, and that table is gone — the costs come out of the shop the
    /// player is standing at, and hand-editing what the game itself told us was an invitation to
    /// break a working tier.
    /// </summary>
    private void DrawConversions()
    {
        var tier = tiers.Tier;

        if (tier.Conversions.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted, "No downtrading — every book is only good for its own fight.");
            return;
        }

        foreach (var conversion in tier.Conversions)
        {
            var from = tier.Encounter(conversion.FromEncounter)?.Name ?? $"#{conversion.FromEncounter}";
            var into = string.Join(", ", conversion.ToEncounters.Select(e => tier.Encounter(e)?.Name ?? $"#{e}"));

            Widgets.Coloured(Widgets.Done, $"Downtrading: {conversion.Ratio} × {from} → 1 × any of {into}");
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

            var bound = tiers.OpenShopRowCount > 0;
            var count = tiers.DiscoverRewards();

            if (count == 0)
                Services.Chat.Print("LootMastr: found nothing — check that the books below resolve.");
            else if (bound)
                Services.Chat.Print($"LootMastr: found {count} exchange entries in the open shop.");
            else
            {
                // Worth saying out loud. Without a shop in front of it this reads every NPC in the
                // game that takes those books, and for a current tier that is how entries from a
                // shop nobody opened ended up in the list.
                Services.Chat.Print($"LootMastr: found {count} exchange entries by searching all of " +
                                    "the game's shop data — no shop was open. Redo this standing at " +
                                    "the exchange NPC if anything looks wrong.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Fill in drops"))
        {
            var filled = tiers.ApplyStandardLayout(tiers.Tier);
            config.Save();

            Services.Chat.Print(filled > 0
                                    ? $"LootMastr: set the usual drops for {filled} fight(s)."
                                    : "LootMastr: every fight already has its drops set.");
        }

        Widgets.HelpMarker("Sets which fight drops what, the way every tier so far has done it: " +
                           "accessories first, head/hands/feet second, body and legs third, the " +
                           "weapon fourth. Fights that already have drops set are left alone.\n\n" +
                           "\"Discover exchange\" does this too — this is for when you have changed " +
                           "your mind and cleared one.");

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

        var available = TierCatalog.AvailableTiers();

        if (available.Count == 0)
        {
            ImGui.TextDisabled("No tier files found.");
            return;
        }

        foreach (var (id, name) in available)
        {
            if (!ImGui.Selectable(name))
                continue;

            if (tiers.Load(id))
                Services.Chat.Print($"LootMastr: loaded \"{name}\".");
            else
                Services.Chat.PrintError($"LootMastr: could not read \"{name}\".");
        }
    }

    /// <summary>
    /// Name and item levels. The levels are editable because they are what gear classification
    /// actually runs on — a wrong one here is what makes a whole roster read as raid gear.
    /// </summary>
    private void DrawIdentity()
    {
        var tier = tiers.Tier;

        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        var name = tier.Name;
        if (ImGui.InputText("Tier name", ref name, 64))
        {
            tier.Name = name;
            config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"saves as {TierCatalog.SlugFor(tier.Name)}.json");

        Level("Raid gear", () => tier.RaidItemLevel, v => tier.RaidItemLevel = v);
        ImGui.SameLine();
        Level("Raid weapon", () => tier.RaidWeaponItemLevel, v => tier.RaidWeaponItemLevel = v);
        ImGui.SameLine();
        Level("Tomestone", () => tier.TomeItemLevel, v => tier.TomeItemLevel = v);
        ImGui.SameLine();
        Level("Augmented", () => tier.AugmentedItemLevel, v => tier.AugmentedItemLevel = v);

        Widgets.HelpMarker("Item levels decide what belongs to this tier. Raid gear and augmented " +
                           "tomestone gear normally share one — that is expected, and the " +
                           "\"Augmented\" in the name is what tells them apart.\n\n" +
                           "\"Discover exchange\" fills these in on its own; they are editable for " +
                           "the rare tier it gets wrong.");
    }

    /// <summary>
    /// The other currency, and the only one the plugin cannot read out of the game.
    ///
    /// The book prices come from the shop the player is standing at and are deliberately not
    /// editable — hand-editing what the game itself said was an invitation to break a working tier.
    /// These are the opposite case: the tomestone vendor is a shop this plugin never opens, so every
    /// number here was typed in by somebody. Seeded with the going rates, editable because that is
    /// the only way they can ever be corrected.
    /// </summary>
    private void DrawTomestones()
    {
        var tier = tiers.Tier;

        ImGui.TextUnformatted("Tomestones");
        Widgets.HelpMarker("What the tomestone vendor charges, and how much everybody has to spend " +
                           "there. This is usually the slower of the two clocks: clearing faster " +
                           "earns books faster, and does nothing at all for tomestones.");
        ImGui.Separator();

        Count("Per week", 0, 2000, () => tier.TomestonesPerWeek, v => tier.TomestonesPerWeek = v);
        Widgets.HelpMarker("The weekly cap. Everyone is assumed to hit it every week — a group that " +
                           "knows it will not can say so by lowering this.");

        ImGui.SameLine(0f, 20f);

        Count("Weeks banked before the tier", 0, 20, () => tier.PriorTomeWeeks, v => tier.PriorTomeWeeks = v);
        Widgets.HelpMarker("Savage opens a week after the patch, so everyone starts with at least " +
                           "one week saved up — more if the group starts the tier late.\n\n" +
                           "This one number decides whether the first body piece is affordable in " +
                           "week one or week three, which is why it is asked for rather than guessed.");

        var priced = tier.CostRules.Where(r => r.Upgrade == null && r.Slots.Count > 0).ToList();

        if (priced.Count == 0)
        {
            Widgets.Coloured(Widgets.Muted,
                             "No cost rules yet — discover the exchange first and the prices appear here.");
            return;
        }

        using var table = ImRaii.Table("##tomePrices", 3,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Category");
        ImGui.TableSetupColumn("Tomes", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Weeks of income", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var rule in priced)
        {
            using var id = ImRaii.PushId(rule.Label);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(rule.Label) ? "(unnamed)" : rule.Label);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);

            var cost = rule.TomeCost;
            if (ImGui.InputInt("##tome", ref cost, 0))
            {
                rule.TomeCost = System.Math.Clamp(cost, 0, 9999);
                config.Save();
            }

            // What the price actually costs somebody, which is the part that decides a plan. 825 is
            // not a number anybody has a feel for; "just under two weeks" is.
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();

            if (rule.TomeCost <= 0)
                Widgets.Coloured(Widgets.Muted, "not sold");
            else
                ImGui.TextDisabled($"{rule.TomeCost / (double)System.Math.Max(1, tier.TomestonesPerWeek):0.0}");
        }
    }

    /// <summary>A plain bounded number. The item-level fields are <c>ushort</c>; these are not.</summary>
    private void Count(string label, int min, int max, Func<int> get, Action<int> set)
    {
        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);

        var value = get();
        if (!ImGui.InputInt(label, ref value, 0))
            return;

        set(System.Math.Clamp(value, min, max));
        config.Save();
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

        var all = tiers.Tier.AllCoffersDrop;
        if (ImGui.Checkbox("Every fight drops one of each of its coffers", ref all))
        {
            tiers.Tier.AllCoffersDrop = all;
            config.Save();
        }

        Widgets.HelpMarker("On: the first fight puts up all four accessories every week, the last " +
                           "puts up its one weapon, and the drop counts below are ignored.\n\n" +
                           "Off: each fight drops the number of coffers set below, drawn from its " +
                           "pool, and the same coffer can come up more than once.");

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

            if (tiers.Tier.AllCoffersDrop)
            {
                // Derived, not set: with everything dropping, the count is however big the pool is.
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled($"{encounter.DropSlots.Count} (all)");
                continue;
            }

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
                        : string.Join(", ", encounter.DropSlots.Select(s => s.ShortCofferLabel()));

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
            if (!ImGui.Checkbox(slot.CofferLabel(), ref on))
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
        Widgets.HelpMarker("The three things that turn tomestone gear into its augmented version. " +
                           "Anything the tier's books buy that is not a piece of gear is offered " +
                           "first — that is where the materials always are, and it saves knowing " +
                           "what this expansion decided to call them.");
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

            if (DrawMaterialSuggestions(upgrade) || !Widgets.ItemSearch(items, ref query, out var picked))
                continue;

            upgrade.ItemId = picked;
            upgrade.ItemName = items.GetItemName(picked);
            config.Save();
        }
    }

    /// <summary>
    /// The discovered exchange entries that are not gear, offered as a list.
    ///
    /// Those are the ones the reward table shows as unassigned, and an upgrade material is always
    /// one of them — the shop the books buy from sells the coffers, the materials, and a handful of
    /// mounts and minions. Searching the whole item sheet for a name nobody remembers was the only
    /// way to fill these in, and it is still there underneath for the tier where this comes up empty.
    /// </summary>
    private bool DrawMaterialSuggestions(TierUpgrade upgrade)
    {
        var candidates = tiers.Tier.Rewards.Where(r => !r.IsAssigned).ToList();
        if (candidates.Count == 0)
            return false;

        ImGui.TextDisabled("From the exchange");
        ImGui.Separator();

        var chosen = false;

        foreach (var reward in candidates)
        {
            using var id = ImRaii.PushId((int)reward.ItemId);

            Widgets.Icon(items.GetItem(reward.ItemId).IconId, 18f);
            ImGui.SameLine();

            if (!ImGui.Selectable(reward.ItemName))
                continue;

            upgrade.ItemId = reward.ItemId;
            upgrade.ItemName = reward.ItemName;

            // Also file it in the reward table, so the same item stops being offered as a material
            // for the other two sides and the exchange stops calling it unassigned.
            reward.Slot = null;
            reward.Upgrade = upgrade.Side;

            config.Save();
            chosen = true;
        }

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.TextDisabled("Or search every item");
        ImGui.Separator();

        return chosen;
    }

    /// <summary>
    /// The two things a chest hands out that no need list can describe.
    ///
    /// Neither fills a gear slot, so neither can go through the ranking — they are decided by hand in
    /// the Loot tab, with the plugin supplying an order rather than an answer. All this section does
    /// is say which items they are, because the plugin has no way to work that out: a stone and a
    /// mount look like any other unrecognised drop.
    /// </summary>
    private void DrawSpecials()
    {
        var tier = tiers.Tier;

        ImGui.TextUnformatted("Special drops");
        Widgets.HelpMarker("Named here, handed out in the Loot tab. Both are matched by item, never " +
                           "by name — a mount whose name happens to contain \"ring\" would otherwise " +
                           "be filed as an accessory coffer.");
        ImGui.Separator();

        Special("Weapon stone", tier.WeaponToken, v => tier.WeaponToken = v, 2,
                "The stone the tomestone weapon needs on top of its tomestone price. Usually the " +
                "second fight.");

        Special("Mount", tier.Mount, v => tier.Mount = v, tier.Encounters.Count,
                "The guaranteed drop from the last fight.");
    }

    private void Special(string label, TierSpecialDrop? current, Action<TierSpecialDrop?> set,
                         int defaultEncounter, string help)
    {
        using var id = ImRaii.PushId(label);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{label}:");
        Widgets.HelpMarker(help);
        ImGui.SameLine();

        var name = current?.ItemName;
        var text = string.IsNullOrWhiteSpace(name) ? "not set" : name;

        using (ImRaii.PushColor(ImGuiCol.Text, current == null ? Widgets.Muted : Widgets.Done))
        {
            if (ImGui.SmallButton($"{text}##pick"))
            {
                query = string.Empty;
                ImGui.OpenPopup("##specialPopup");
            }
        }

        // Clearing has to be possible and has to be deliberate: an item set by mistake would
        // otherwise put a widget in the Loot tab that never goes away.
        if (current != null)
        {
            ImGui.SameLine();
            var fight = tiers.Tier.Encounter(current.Encounter)?.Name ?? $"#{current.Encounter}";
            ImGui.TextDisabled($"from {fight}");

            ImGui.SameLine();
            if (ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
            {
                set(null);
                config.Save();
            }

            Widgets.Tooltip("Ctrl+click to forget this item.");
        }

        using var popup = ImRaii.Popup("##specialPopup");
        if (!popup.Success)
            return;

        if (!Widgets.ItemSearch(items, ref query, out var picked))
            return;

        set(new TierSpecialDrop
        {
            ItemId = picked,
            ItemName = items.GetItemName(picked),
            Encounter = current?.Encounter ?? Math.Max(1, defaultEncounter),
        });

        config.Save();
    }

    private void DrawExchange()
    {
        ImGui.TextUnformatted("Book exchange");
        Widgets.HelpMarker("Read from the game. Assign each coffer to the slot it fills so the planner " +
                           "knows what buying it would achieve.");
        ImGui.Separator();

        DrawConversions();

        var tier = tiers.Tier;

        // Where a rule and the shop disagree about a price. Worth surfacing next to the shop data
        // rather than beside the rules — this is the half that can be checked against the game.
        foreach (var mismatch in tier.CostMismatches())
            Widgets.Coloured(Widgets.Wanted, $"? {mismatch}");

        ImGuiHelpers.ScaledDummy(4f);

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
                        : reward.Slot?.CofferLabel() ?? "unassigned";

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
            if (!ImGui.Selectable(slot.CofferLabel()))
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

    public void Dispose()
    {
    }
}
