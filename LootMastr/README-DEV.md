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

## Where the chest is read from

**`AddonNeedGreed`'s AtkValues, not `Loot.Instance()->Items`.** This is not a preference; the first
version used `Loot.Items` and was blind at exactly the moment that matters.

While the leader still gets to choose between assigning an item and putting it up for greed,
`Loot.Items` is **empty**. It only fills once that choice has been made — so the plugin registered a
chest one step too late, after the decision it exists to help with. Captures from a live Deltascape
Savage chest, in the config directory, show `Loot items:` empty against `AgentLoot.NumItems=9` with
all nine sitting in the addon's values.

The layout seen there is a seven-value header (`[6]` is the localised `Loot Rule: …` caption) and
then eight values per item: id, icon, two zeros, name, count, two more. `LootWindowReader` does not
trust those offsets. It finds each block by its own shape — a `UInt` that resolves to a real item
whose name appears four values later — and only falls back to the fixed offsets if that finds
nothing. `Loot.Items` is still read, but purely to enrich what was found with roll state, which
genuinely does not exist yet at that point.

The loot rule comes from `ContentsFinder.Instance()->LootRules`, not from the caption, which is
localised.

## Handing an item over

Three windows, recorded from a live Lootmaster chest rather than guessed:

| | Window | What it holds |
|---|---|---|
| 1 | `NeedGreed` | the chest |
| 2 | `NeedGreedTargeting` | `[0]` loot index, `[4]` item name, `[6]` candidate count, `[7…]` names |
| 3 | `SelectYesno` | `[0]` "Allow &lt;player&gt; to claim the &lt;item&gt;?" |

Two things in there are worth not forgetting.

**The candidate list is not in party order.** In the recording it read *Yuma, Shiori* while the
party list read *Shiori, Yuma* — the local player came first. Recipients are matched by name.

**The game asks a question in plain words before anything happens**, and that is the safety net the
whole design leans on. Which callback opens each window is *inferred*, not recorded, so
`LootAssignmentRunner` tries a short list of payload shapes and **verifies against what the game put
on screen** after each: the targeting window has to be showing the right loot index and item name,
and the confirmation has to name both the intended player and the intended item. A wrong shape
opens the wrong window or none, and the step is abandoned. Nothing is irreversible until the Yes,
and that is only pressed once the dialog's own text checks out.

`Note()` logs which shape worked, which is what turns the inferred list into a known one — after
the first successful run the others can go.

Assignment modes map onto the flow directly. **Confirm** does steps 1–2 and leaves the game's own
Yes/No for the human, which is a better confirmation than anything the plugin could put on screen.
**Automatic** answers it too. One item at a time, because each walks all three windows.

### One attempt where there is no confirmation

The three-window flow has one place where a wrong callback is *itself* a decision. Each item in a
Lootmaster chest offers two actions, and action `0` is **Greed only** — pressing it settles that
item for good. An early version tried a list of payload shapes on the chest until one worked, on
the reasoning that a wrong shape does nothing. It does something: it greeds the item.

So the chest gets exactly **one** attempt, with `Configuration.AssignActionId` (1), and reports
failure rather than working around it. Picking a recipient inside the targeting window may still be
tried in a few shapes, because the game asks "Allow &lt;player&gt; to claim &lt;item&gt;?" afterwards and
that check catches both a wrong name and a wrong item before anything irreversible happens.

The rule worth carrying: **retry only where a verification gate stands between the attempt and the
consequence.**

### The same coffer twice

A chest can hold two of a kind — the recorded Deltascape chest had two earring coffers. The gear is
unique, so the second cannot go to whoever is taking the first. `LootAssigner.Refresh` therefore
decides the window in order, feeding each decision the ones above it as `PendingAward`s, instead of
ranking every item against the same untouched roster.

### It can legitimately fail

Assigning a unique item to someone who already owns one is refused by the game, with an error
dialog — this happened during the recording. `VerifyGone` therefore waits for the item to actually
leave the chest and reports if it does not, rather than retrying into the error.

### Which callback opens the targeting window is still unknown

Two inferences, two different failures: `[0, index]` pressed **Greed only**, `[1, index]` did
nothing at all. Neither is a number to keep raising — that is guessing on a live chest, and one of
those guesses already cost an item.

`AddonWatcher` therefore also records `PreReceiveEvent` on `NeedGreed` and `NeedGreedTargeting`,
giving the **event type and parameter the real button sends**. That is not an inference: pressing
assign by hand on two different rows shows both the event and how the row is encoded in it. Mouse
movement events are filtered out or they bury the clicks.

Until that recording exists, `Assign` will keep failing harmlessly — one attempt, no fallback.

`AddonWatcher` also hooks `PostSetup` for every addon and keeps the ones appearing while `NeedGreed`
is up, which is what produced the window table above. Off by default, since it hooks every window
in the game.

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
  material*. The other cost on those entries is the plain tome piece, so one pass identifies both
  sets by id.

- **Which slot a coffer fills** is read out of its own name — coffers are named
  `<Set> <Slot> Coffer (IL nnn)`. Gear sold as itself rather than as a coffer skips that entirely
  and uses its own equip category. `Slots.SlotFromName` holds the word list, and its order is
  load-bearing: "earring" contains "ring", so accessories are tested before the ring.

  The name is matched against **every** slot, not only the ones the tier claims that fight drops.
  The narrower version sounded safer and in practice just left rows unassigned whenever the drop
  pools were slightly off — and a coffer with "Head" in its name is not a ring whatever the pools
  say.

  Nothing is guessed from a fight's drop pool. There used to be a "if the pool has one entry, use
  it" fallback, and since the last fight drops exactly one slot it stamped **Weapon** onto every
  mount, minion and orchestrion roll that fight's books also buy. Unassigned is the honest answer.

- **The books themselves**, when a shop is open. Standing at the exchange NPC settles which shop a
  tier means better than any name matching can, because the game is already showing the right one.
  `Discover exchange` reads the open window's item ids, finds the `SpecialShop` rows that overlap
  with them, and takes the currencies those rows charge as the books. Assigning them to fights I–IV
  goes by ascending item id, which is a guess — books are created in fight order — so it is
  reported rather than applied silently, and every one stays editable.

- **Book-for-book trades.** Most of the last fight's shop is its books buying the earlier fights'
  books, and reading those rows as gear was the other half of why they all came out as "Weapon".
  A reward that is itself one of the tier's books is recorded as a `TierTokenConversion` instead,
  which is also how the tier learns its own conversion rates.

## Classifying gear does not depend on any of that

`TierDefinition.ClassifyByLevel` is the whole rule, and it needs nothing but the item sheet:

> The **item level** says whether a piece belongs to this tier. The **name** says which half, because
> augmented tomestone gear is spelled `Augmented …` and raid gear never is.

That matters because raid gear and augmented tome gear sit at the *same* item level — 790 in this
tier — so neither signal works alone. An earlier version had this the other way round, consulting
the discovered id sets first, and quietly filed every augmented piece as a raid drop for anyone who
had not run the discovery or whose run had come back incomplete.

The discovered sets are now only consulted for items the levels do not explain. The prefixes live
in the tier json since the wording is localised, and `Roster → Re-file imports` re-runs the
decision over ids already imported, so fixing any of this never means fetching every gear set
again.

Practical consequence worth knowing: the tier's four **item levels** are load-bearing and the shop
discovery is not. A wrong item level breaks the roster grid; a missing exchange table only costs
the planner its "can buy with books" arithmetic.

### A gear set from the wrong tier

Every piece of it classifies as `Crafted`, which fills a roster row in with something that looks
deliberate and is entirely wrong. `BisImporter.BelongsToTier` refuses such a set instead: if nothing
in it reads as raid, augmented or tomestone gear for the active tier, nothing is applied, and the
player gets an orange `?` next to their name carrying the reason.

The other direction is useful rather than a problem. When a tier has **no** item levels yet, the
imported set can supply them: every savage set uses the raid weapon, and that weapon sits five above
the rest of the raid gear and above augmented tomestone gear — so one number gives all four
(`SeedItemLevelsFrom`).

### Coffers are not gear

`TierCatalog.TryMatch` decides whether something in a chest is worth planning around, and item
level cannot answer that for a **coffer**: coffers have no equip category and no item level at all.
A correctly configured tier was still calling every coffer "not tier loot" until this was handled
separately — the name is what says what is inside, so `Slots.SlotFromName` decides for them.

Order in `TryMatch` is: the discovered exchange, then upgrade materials by id, then gear that drops
as itself (equippable at a tier item level), then coffers by name.
- **Which zone is which fight** is learned the first time a chest is seen there, and only when two
  or more of its drops match one fight's pool — one match could be a slot two fights share.

The json is copied into the config on first use, so corrections made in game survive a plugin
update. Anything that fails to resolve keeps an id of `0` and is listed in red by
`TierDefinition.Problems()` instead of throwing.

A tier is fully editable in game and does not have to be one that shipped: **New tier** starts a
blank four-fight skeleton, books and materials are chosen through a **searchable item picker**
rather than by typing an exact name, and **Save tier** writes it to its own json. That is what makes
setting up an old tier for testing a five-minute job instead of a text-editor one.

Item levels are filled in by `Discover exchange` and only editable for the rare tier it gets wrong.
They come mostly off the **coffers**, whose own item level is zero but whose name carries it —
"Genji Earring Coffer (IL 340)". `BracketedLevel` matches digits inside brackets rather than the
"IL", which is localised. Augmented gear, being equippable, contributes a real item level where it
has been discovered, and the plain piece it is traded from gives the tomestone level.

The first version read *only* the augmented set, which meant it never fired at all: discovering
augments needs the upgrade materials to be filled in, and a tier being set up for the first time has
none. Whatever is still missing after all that falls back to the usual relationships — weapon is
raid + 5, tomestone is raid − 10 — and the mode is used rather than the max, so one stray item
cannot move a whole tier.

Which fight drops what is a **convention**, not something the game exposes: accessories first,
head/hands/feet second, body and legs third, the weapon fourth, with the accessory material where
its books are and the armour and weapon materials together in the third. `ApplyStandardLayout`
fills that in for fights that have nothing set, and never touches one that does. It runs on
`Discover exchange`, on `New tier`, and on its own button for when you have cleared one and changed
your mind.

There is no id field. The file name is the tier's name slugified, which removes a field that could
only ever be got wrong and keeps the two from drifting apart. The load list shows names, not files.

Tiers live in two places: shipped ones next to the assembly, and ones built in game under the
**config** directory, where a rebuild or a plugin update cannot take them with it. Same id, the
user's copy wins. Enums are written by name — these files get hand-edited and passed around, and
`"Side": 2` tells nobody anything.

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

## Everything here is unique

Raid gear, augmented gear and the materials can only be held once. Two consequences that are easy
to get wrong and invisible when you do:

- **A character can only wear one raid ring.** The second ring is normally the augmented one, or
  occasionally a crafted piece that stays best in slot. `PlayerPlan.From` therefore counts at most
  one raid ring however many a gear set claims — two would have the planner chase a coffer that
  could not be equipped even if it won it. The harness had this the wrong way round and asserted
  two coffers were wanted.
- **There is one ring coffer**, so drop-facing views say "Ring" rather than "Ring 1" —
  `Slots.CofferLabel`. The roster grid keeps both columns, because a gear set really does have two
  ring slots.

An assignment can also be refused for exactly this reason: handing someone a unique item they
already own fails, which is why success is the item leaving the chest rather than the click landing.

## Shields are not a slot

Only one job carries a shield and it always arrives with the weapon — same coffer, same eight
books. So `GearSlot.OffHand` is absent from `Slots.All` and `CofferSlot` files it under the weapon;
a paladin owes one piece, not two. The enum member stays for item data and for configs written
before this.

Note the asymmetry with rings, which is real rather than an oversight: two ring slots want two
separate coffers, while a weapon and a shield are one purchase. The harness pins down both halves,
because collapsing the wrong one silently miscounts somebody's remaining work.

`PlayerPlan.From` still falls back from "which fight drops this slot" to "which book buys it",
which is what would carry a slot that is only ever bought.

## Target, given, actual

Three separate things per slot, and conflating any two of them breaks something:

| | Field | Means |
|---|---|---|
| Target | `Source` | where the player intends to get this slot from |
| Given | `Obtained` / `UpgradeObtained` | the group handed it over — **this is what distribution goes by** |
| Actual | `EquippedItemId` / `EquippedSource` | what is on the character right now |

`SlotNeed.StateFor` folds them into what the cell shows. The case that matters is *given but not
worn* — an awarded coffer nobody opened. It is `AssignedNotWorn`: flagged in the grid so the player
knows they owe themselves an equip, and still `IsSatisfied`, so it never comes back around for
assignment. Getting that second half wrong would hand the same slot to the same person twice.

`StateFor` takes a `scanned` flag because before anyone has looked, "not wearing it" cannot be told
apart from "not known", and guessing would put a warning on every row of a fresh roster.

`GearScanner` may set the given flags and **never clears them**. Not wearing a piece is no evidence
of not owning it. The reverse does hold — wearing it proves ownership — which is what lets a scan
fill in everything anyone picked up before the plugin existed.

## Reading what people wear

`AgentInspect.ExamineCharacter(entityId, false)` is a direct call, so the scan does not have to
target anyone or drive the examine UI. `AgentInspect.ItemData` keeps `ItemId` and `GlamourItemId`
in **separate fields**, so `ItemId` is the real gear — which is what makes this viable at all. The
obvious alternative, reading `DrawData` model ids, returns whatever the character is glamoured as
and would be wrong for most statics.

`GearScanner` is a state machine on the framework tick rather than a loop, because examining is a
request answered several frames later. It waits for `AgentInspect.CurrentEntityId` to actually be
the character it asked for, with items present, and gives up after six seconds — the call's own
return tells you nothing. `UIState.Instance()->Inspect.RequestCooldown` is the game's own examine
throttle and is respected on top of `Configuration.ActionDelayMs`.

Party members in another zone have no entity id to examine and are skipped by name, not waited on.

Slots are filed from each item's own equip category rather than from its position, since the
inventory container and the examine window do not lay out the same. Rings are the exception —
item data never says which finger — so they go in the order the game listed them.

## Jobs, and how a change reaches the plan

Jobs are **never** pulled from the party on their own. A party picks up strangers and people swap
for a single pull; the roster is a static, and it only changes when somebody says so. The two ways
in are the job picker on the roster's job icon and the explicit *Sync from party* button.

Getting a change to arrive is `RosterStore.Signature()`, a fingerprint of everything the planner
reads — jobs included. `PlanTab` and `LootAssigner` both recompute when it moves, so nothing has to
be told about anything.

It walks `Slots.All` and encounters 1–4 in a fixed order rather than iterating the dictionaries.
Dictionary order is not guaranteed and `NeedFor` inserts on access, so hashing them directly would
have let the fingerprint change on its own — quietly recomputing the whole plan every frame.

## Ticking things off

Two independent paths, because neither is reliable alone:

- `ObtainTracker` watches chat. It matches the **item** through the message's `ItemPayload`, so it
  does not depend on the client language, and the **player** through a `PlayerPayload` first,
  falling back to a roster name appearing in the text. The chat channels it listens to were picked
  by hand and may be wrong on a non-English client — every message it considered, and what it made
  of it, is listed in `Debug → Chat lines the tracker considered`.
- `Loot → Record` does it by hand.

`ClearTracker` counts books, and asks first. A book counted twice bends every forecast after it,
and there is no way to notice from the outside. Confirming also raises the group's kill count for
that fight.

## What a piece costs in books

Costs are uniform per **category**, not per item, so `TierDefinition.CostRules` is eight rows where
a per-item table would be thirty:

| Buys | Books |
|---|---|
| Any accessory | 3 × T1 |
| Head, hands, feet | 4 × T2 |
| Accessory upgrade | 3 × T2 |
| Body, legs | 6 × T3 |
| Armour upgrade, weapon upgrade | 4 × T3 each |
| Weapon, shield included | 8 × T4 |

The rules are what the arithmetic runs on, because a rule cannot be half missing the way a
discovered table can. A tier with none gets them **derived from its own shop** rather than handed
this tier's numbers — an older tier prices things differently and its shop knows. Slots that come
out at the same price end up sharing a rule, which reproduces the categories the game prices by
without being told what they are.

Where the shop is consulted, the price taken is the **most common** one, never the cheapest. A
slot's gear is uniformly priced, so the odd one out is an outlier: Deltascape sells a shield
separately for three books, it files under the weapon slot, and taking the minimum priced every
weapon in the tier at three.

The Tier tab lists disagreements between a rule and the shop, one line per category. Comparing
every reward individually produced twenty weapons all disagreeing with the same rule in the same
way, which says nothing twenty times.

### Whether every coffer drops

`TierDefinition.AllCoffersDrop`. On, a fight puts up one of each slot it can — all four accessories
every week — and drop counts are ignored. Off, it drops `DropCount` out of its pool and the same
coffer can come up twice.

This is not cosmetic: four guaranteed accessories a week and two random ones out of four are
different tiers to gear through, and the forecast says so. With it off the pool is walked
round-robin, which reproduces each slot's average rate without pretending to roll dice.

### Trading books in

**T4 books convert one for one into any of T1–T3.** This is not a detail: it decides who is stuck.
A player short on accessory books but sitting on spare weapon books is not short on anything.

`BookLedger` is the whole of it, and the part that matters is `Spare`. The last fight's books are
*also* what buys the weapon, so only books above what a player still owes that fight may be traded
away. Without that reservation the simulator cheerfully trades off a weapon nobody can then afford
and reports a finish date that never arrives. `Pay` spends own books first and converts only the
shortfall, so nothing is traded that did not have to be.

Conversion runs one way only. Earlier books never buy the last fight's rewards, and the harness
pins that down along with the reservation.

## Books

The game exposes no way to read how many books a player is holding, so the numbers are entered and
have to be worth trusting. `Loot → Books` puts all of it in one grid:

- **Kills per fight**, group level. Raised automatically when a clear is confirmed, editable for
  everything that happened before the plugin was in use. Its real job is being the number an
  individual's count can be checked against — a player holding more books than the group has kills
  is impossible, so the cell turns red.
- **Books per player per fight**, editable. This is what people are holding *now*, after anything
  already spent, which is why it cannot simply be derived from kills.
- **What that buys right now**, from `LootPlanner.AffordableNow`. This is the question the counts
  exist to answer: someone who can already buy the piece outright does not need to compete for the
  coffer.

`+1 <fight>` raises the kill count and gives every roster member a book. The Roster tab's clear
prompt is the accurate path — it only counts the people who were actually in the party — and this
is the catch-up for the clears nobody confirmed in time.

## Testing order that does not cost a raid night

1. `/lootmastr` → **Tier** → *Discover exchange*. Every book and material should resolve, and the
   exchange table should fill with sensible costs. Assign each coffer to its slot.
2. **Roster** → *Sync from party*, then import one gear set. Check that the grid marks raid pieces
   and tome upgrades, not everything as raid.
2a. **Roster** → *Read gear* while standing in a party. Your own row should fill immediately; the
   others should follow one at a time. Check a glamoured character reads as their real gear, and
   that a slot you have already ticked off but are not wearing shows `✓!` rather than reverting.
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
