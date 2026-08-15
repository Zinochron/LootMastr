# LootMastr — implementation notes

Written for whoever touches this next, including future me.

## Layout

| Folder | What lives there |
|---|---|
| `Data/` | Reading the game: items, jobs, party, the tier definition, the loot window |
| `Roster/` | The static: who is in it, what each slot wants, what they hold |
| `Planning/` | Pure calculation: need lists, the week simulator, the ranking |
| `Import/` | XIVGear and Etro gear sets |
| `Automation/` | Everything that changes something: assigning, announcing, ticking off |
| `UI/` | ImGui window and tabs |

`Data`, `Roster` and `Planning` never change game state. `Automation` is the only place that does.

`Planning/` additionally has no Dalamud references at all, which is what lets `Harness/` compile it
on its own. If that list ever needs a Dalamud type, something has leaked.

## The one constraint everything follows from

The game has no way to award an item to another player — except one. With the party on the
**Lootmaster** loot rule, the leader gets a *loot recipient* choice per item in the loot window.
Consequences:

- Everything in `Automation` is gated on `SafetyGuard.CheckAssign`, which wants: logged in, not
  zoning, loot window open, **local player is party leader**, and at least one item reporting
  `LootMode.LootMasterGreedOnly`.
- The rule only applies to a preformed or undersized party and has to be set in the duty finder
  **before entering**. There is no way to fix it from inside, so the Loot tab says so plainly
  rather than failing quietly.
- Lootmaster mode is read from the items themselves, not from a party setting. The loot window is
  what actually decides.

## What is still missing

`LootAssigner.PerformAssignment` decides correctly and refuses to act. ClientStructs exposes the
whole read side — `Loot.Instance()->Items`, `RollState`, `RollResult`, `LootMode`, `AgentLoot` —
but nothing typed for the recipient choice, and `AddonNeedGreed` only offers `NumItems`,
`SelectedItemIndex` and a `CurrentDropDownOwnerNode`. Firing a guessed `AtkValue` payload at that
would be guessing with somebody's weekly lockout.

**Debug → Write capture file** exists for exactly this. Take one with a Lootmaster chest on screen;
it records the party, every loot item, `AgentLoot`, and the addon's full `AtkValues` list. The
recipient rows show up there as strings, which is what the click needs.

When wiring it up, follow the two rules Sortr learned the hard way: match players **by name**
against what the window is offering rather than by index, and never judge success by a return
value — click, then wait for the window to actually show the change.

## Things that were verified against the installed Dalamud, not guessed

`Loot.Instance() → Loot*` with `Items` as `Span<LootItem>`; `LootItem.{ItemId, ItemCount,
RollState, RollResult, RollValue, WeeklyLootItem, Time, LootMode}`; `RollState { UpToNeed=0,
UpToGreed=1, UpToPass=2, Rolled=17, Unavailable=21 }`; `RollResult { UnAwarded, Needed, Greeded,
Passed, Awarded }`; `LootMode { Normal=0, GreedOnly=1, Unavailable=2, LootMasterGreedOnly=3 }`;
`AgentLoot.{NumItems, SelectedSlotIndex, HoveredSlotIndex, IsAddonShown()}`;
`IGameGui.GetAddonByName → AtkUnitBasePtr` with `.Address`, `.AtkValues` as
`IEnumerable<AtkValuePtr>` and `AtkValuePtr.{ValueType, GetValue()}`;
`IChatGui.ChatMessage → void(IChatMessage)` with `.LogKind`, `.Message`, `.Sender`;
`IDutyState.DutyCompleted → void(IDutyStateEventArgs)` with `.TerritoryType` as a `RowRef`;
`IPlayerState.{IsLoaded, CharacterName, HomeWorld, ClassJob, ContentId}` — note that
`IClientState.LocalPlayer` is gone; `SpecialShop.Item` as `Collection<ItemStruct>` with
`ReceiveItems[].Item/.ReceiveCount` and `ItemCosts[].ItemCost/.CurrencyCost`.

Two ways to pin any of this down, cheapest first:

1. Write the member into a scratch file with a deliberately wrong type and read the real type out
   of the compiler error. Good for one or two members.
2. `MetadataLoadContext` over `FFXIVClientStructs.dll` in a throwaway console app, listing fields
   and enum values. Good when you need the whole shape, and the only sane way to get enum members —
   the compiler will tell you a name is wrong but never what the right ones are.

## Why tier data is data

`Data/Tiers/aac-heavyweight.json` names four fights, their books, their drop pools and three
upgrade materials. Everything else is discovered from the game:

- **Book costs** come from walking `SpecialShop` for entries paid with the tier's book items.
  Guides disagree about whether an accessory is three books or four; the shop rows cannot.
- **The augmented tome set** comes from the same walk, looking for entries paid with an *upgrade
  material*. This matters more than it sounds: augmented tome gear sits at exactly the raid item
  level, so item level alone can never tell an imported BiS piece apart from a raid piece. The
  other cost on those entries is the plain tome piece, so one pass identifies both sets by id, in
  any client language.

  Discovery has to have been run for that, though, and the first version quietly filed every
  augmented piece as a raid drop until it was. `TierDefinition.IsAugmentedName` is the fallback:
  augmented gear is spelled `Augmented …` or `Aug. …`, and the check runs **before** the item level
  check, because the level says "raid" for both. The prefixes live in the tier json since the
  wording is localised. `Roster → Re-file imports` re-runs the decision over ids already imported,
  so discovering the tier afterwards does not mean fetching every gear set again.

- **Which slot a coffer fills** is read out of its own name — coffers are named
  `<Set> <Slot> Coffer (IL nnn)` — restricted to the slots that fight's book actually buys, so a
  bad read can only land on a neighbour. `Slots.SlotFromName` holds the word list, and its order is
  load-bearing: "earring" contains "ring", so accessories are tested before the ring.
- **Which zone is which fight** is learned the first time a chest is seen there, and only when two
  or more of its drops match one fight's pool — one match could be a slot two fights share.

The json is copied into the config on first use, so corrections made in game survive a plugin
update. `Reload shipped defaults` throws them away again.

Item names in the json are the only thing that can rot. Anything that fails to resolve keeps an id
of `0` and is listed in red by `TierDefinition.Problems()` instead of throwing.

## The one number the plan is built on

`WeekSimulator` plays the rest of the tier forward and reports the week the **last** player
finishes. A candidate for a drop is judged by running that simulation with them holding the item.
Two things are deliberately assumed, and both are written on the class:

- Coffers come up evenly — the drop pool is walked round-robin, not rolled. A slot in a pool of
  four at two drops a week therefore appears every other week, which is its average rate.
- Every fight is cleared every week. A group clearing three finishes later than forecast, but the
  ranking between candidates does not move.

Weeks are counted **from now**. Week 1 is the next reset.

`Slots.NeedsRaidResource` is what keeps the model small: plain tome and crafted pieces are
satisfied the moment they are chosen, because they cost the raid nothing. Only `Raid` and
`TomeAugmented` ever compete for a drop, and for `TomeAugmented` it is the *material* that is
tracked, never the tome piece.

## A result that surprised me

Given one player who needs five pieces and one who needs only the piece that just dropped, the
ranking hands it to the player it **finishes**, not the one with more left — because in that case
both choices leave the group's last week unchanged, and finishing someone outright is strictly
better. The greedy "most remaining needs" rule inside the simulation and the ranking that wraps it
are allowed to disagree; the ranking wins, because it is the one that plays the whole tier forward.

The first version of the harness asserted the opposite and was wrong.

## Shields do not drop

No fight drops a shield; it exists only in the weapon book exchange at five books. `PlayerPlan.From`
therefore falls back from "which fight drops this slot" to "which book buys it", or a paladin's
shield would silently disappear from the plan instead of showing up as work. Covered by the harness.

## Ticking things off

Two independent paths, because neither is reliable alone:

- `ObtainTracker` watches chat. It matches the **item** through the message's `ItemPayload`, so it
  does not depend on the client language, and the **player** through a `PlayerPayload` first,
  falling back to a roster name appearing in the text. The chat channels it listens to were picked
  by hand and may be wrong on a non-English client — every message it considered, and what it made
  of it, is listed in `Debug → Chat lines the tracker considered`.
- `Loot → Record` does it by hand.

`ClearTracker` counts books, and asks first. A book counted twice bends every forecast after it,
and there is no way to notice from the outside.

## Testing order that does not cost a raid night

1. `/lootmastr` → **Tier** → *Discover exchange*. Every book and material should resolve, and the
   exchange table should fill with sensible costs. Assign each coffer to its slot.
2. **Roster** → *Sync from party*, then import one gear set. Check that the grid marks raid pieces
   and tome upgrades, not everything as raid.
3. **Plan** → the forecast should give plausible week numbers, and the priority rows should list
   the people who actually still need each slot.
4. Form a two person party, set **Lootmaster** in the duty finder, enter an old dungeon undersized.
   The loot window behaves identically and nothing is on a weekly lockout.
5. **Debug** → live state should say leader `you` and lootmaster `True`. Write a capture file.
6. **Loot** → the ranking should appear. Assign by hand in the game window and confirm the tracker
   ticks it off; if not, the reason is in the chat probe.
7. Only then wire up `PerformAssignment`, and test it in the same dungeon before a raid night.

## Known loose end

`.github/workflows/pr-build.yml` uploads `LootMastr/bin/x64/Release/LootMastr/*`, inherited from
the template. Locally the SDK puts the built plugin in `bin/x64/Release/` and leaves only the
Dalamud download in the `LootMastr/` subfolder — Sortr's output looks the same. Worth checking on
the first pull request whether CI differs, rather than changing it on the strength of one machine.
