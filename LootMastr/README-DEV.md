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

**`LiveLootItem.Index` is derived from where the block sits, not from how many items were
recognised.** It looks like a detail and is not: that number is the game's own slot index, and every
single thing the plugin does to a chest is keyed on it — which row to select, whether the right
window opened. Counting recognised items instead meant one coffer the catalogue could not name
shifted every item after it by one.

The loot rule comes from `ContentsFinder.Instance()->LootRules`, not from the caption, which is
localised.

**An awarded coffer stays in the chest.** A recording proved it: a slot handed over minutes earlier
still opened its own assignment window when pressed again. So "the item is gone" is not a signal for
anything, and neither is the window's contents. See *Knowing what has already been handed over*.

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
whole design leans on. Every step is **verified against what the game put on screen**: the targeting
window has to be showing the right loot index and item name, and the confirmation has to name both
the intended player and the intended item. Nothing is irreversible until the Yes, and that is only
pressed once the dialog's own text checks out.

Assignment modes map onto the flow directly. **Confirm** does steps 1–2 and leaves the game's own
Yes/No for the human, which is a better confirmation than anything the plugin could put on screen.
**Automatic** answers it too. One item at a time, because each walks all three windows.

### Which item is being assigned is the selection, not the press

This cost two rounds of testing, so it is worth stating flatly: **the Loot Recipient button carries
no item.** It acts on whichever row the chest currently has selected. A recording of a failed run
shows it plainly — the plugin pressed the button meaning the ring, and the game opened the earring,
because the earring was the last row the player had clicked by hand and the plugin's attempt to move
the selection had done nothing whatsoever. Repeating it just kept re-offering a coffer that had
already been handed over, and the ring was never once tried.

Two things came out of that.

`AtkComponentList.SelectItem` is not enough on its own. It moves the list's own highlight without
telling the addon, so the button went on reading the old row. The event that does reach the addon is
the list item click, and `AtkComponentList.DispatchItemEvent(index, AtkEventType.ListItemClick)` is
the game building that event — index and all — rather than the plugin assembling one. Hand-made list
events are what crashed the client, so asking the game for one is also the safe way round.

And the selection is **read back and checked before the button is pressed**: `AddonNeedGreed.
SelectedItemIndex` or `AgentLoot.SelectedSlotIndex`, either will do, both were seen tracking a
hand-clicked row. If neither says what it should after a few tries, nothing is pressed and the
status line asks the player to click the row themselves. A press on the wrong row is not a no-op.

The list itself is found by asking each node id in turn and taking the one holding as many rows as
`AddonNeedGreed.NumItems`. Taking the first list in the window is what the earlier version did, and
a wrong list is a selection that never moves.

### One attempt where there is no confirmation

The three-window flow has one place where a wrong callback is *itself* a decision. Each item in a
Lootmaster chest offers two actions, and action `0` is **Greed only** — pressing it settles that
item for good. An early version tried a list of payload shapes on the chest until one worked, on
the reasoning that a wrong shape does nothing. It does something: it greeds the item.

So the chest gets exactly **one** press, and reports failure rather than working around it. What is
retried is the *selection*, which changes nothing on its own and is checked before anything is
pressed.

The rule worth carrying: **retry only where a verification gate stands between the attempt and the
consequence.**

### The same coffer twice

A chest can hold two of a kind — the recorded Deltascape chest had two earring coffers. The gear is
unique, so the second cannot go to whoever is taking the first. `LootAssigner.Refresh` therefore
decides the window in order, feeding each decision the ones above it as `PendingAward`s, instead of
ranking every item against the same untouched roster.

### Knowing what has already been handed over

**The chat line, and nothing else.** Three sources were tried and all three are blind:

- `RollResult` lives in `Loot.Items`, which is **empty** in Lootmaster mode, so every item reads as
  undecided however many have been given away.
- The item disappearing from the chest is not a signal — it does not disappear.
- The plugin's own click is not a signal either. Pressing the buttons is not the same as the item
  moving: assigning a unique coffer to someone who already owns one is **refused**, with an error
  dialog, and the row stays exactly where it was. That happened during a recording, and treating a
  finished click as a finished assignment marks it done when nothing changed hands.

So `ObtainTracker` calls `LootAssigner.MarkHandedOver(itemId)` when it sees an obtain line, whoever
it names — including someone outside the roster, since the coffer is gone either way. `assigned`
holds those, keyed by slot and item id, cleared when the chest closes. Recording something by hand
counts too, and the runner's own finish only marks the row *offered*, which shows in the table and
leaves the button live.

The button is per row rather than one for "next". Which coffer is being handed over is the leader's
call, and a list that decides for you goes wrong the moment it believes an item is still open when
it is not.

The fallback if a client's obtain line ever arrives on a channel `ObtainTracker` is not listening to:
the Record button on the row, and the Debug tab lists every line it considered.

### It crashed the client once, and what fixed it

The version that crashed synthesised **all four** steps as raw events, including the two list
clicks, and passed a null `AtkEventData`. A list handler reads that data to work out which row was
hit — knowing *which* events a click sends turned out not to be the same as being able to send them.

No list event is hand-made any more. `AtkComponentList.DispatchItemEvent` has the game build it,
index and all, which is precisely the part that could not be reconstructed from outside. What is
left is two button presses, and those get a real event *and* real event data rather than a null.

This lived behind `Configuration.EnableAssignment`, off by default, for as long as that was one
explanation rather than a tested one. It has since been driven through enough live chests to drop
the switch. Keep the shape of the argument though: a specific fix for a specific cause is worth
more than a retry, and neither is worth much until somebody has watched it work.

### What a click actually sends

Recorded off three assignments in a live chest, via `PreReceiveEvent`:

```
NeedGreed            ListItemClick  param=0   select the row
NeedGreed            ButtonClick    param=5   "Loot Recipient"
NeedGreedTargeting   ListItemClick  param=0   pick the name
NeedGreedTargeting   ButtonClick    param=0   confirm
```

Two earlier versions guessed a `FireCallback` number instead: `[0, index]` pressed **Greed only**
and settled an item, `[1, index]` did nothing. The recording is what replaced that.

**Every one of those parameters identifies the control, not the row.** They stayed the same across
three different rows and three different recipients. The row lives in the list's own state — which
is the whole subject of *Which item is being assigned is the selection, not the press* above, and
the single most expensive thing to have got wrong here.

Events are sent with their `Listener`, `Target` and `Node` pointing at the window, the way a real
one arrives. A zeroed `AtkEvent` invites the game's own handler to walk a null pointer, and that
takes the client down rather than just failing.

Verification still wraps every step, because a recording is one client on one patch.

### Debug → the recorder

`AddonWatcher` hooks `PreReceiveEvent` on the two loot windows, `PostSetup` for every addon (keeping
the windows that appear while `NeedGreed` is up), and `PostRefresh` on `NeedGreed` itself. It
produced both tables above and stays for whenever the flow changes.

The `PostRefresh` snapshots exist because opening the window was never enough: what an award does to
a coffer's row is the one thing no capture had ever shown, since the window is only snapshotted as
it opens and by then nothing has happened. Comparison ignores the first seven values — the countdown
lives in the header and refreshes every second, and would otherwise push every real change out.

The press hook catches the plugin's own `ReceiveEvent` calls too, since they go through the same
vtable. That is how the failed run was diagnosed: a `ButtonClick param=5` in the log with **no**
`ListItemClick` before it is a selection that never happened.

On by default: the press hook is scoped to two windows and the window hook returns immediately
unless a chest is open, so it costs nothing — and a checkbox that has to be remembered already cost
one recording session. Worth knowing that `PostSetup` fires **once** per addon lifetime, so a window
that was opened earlier and is being reused will not appear in the window list again.

When wiring it up, follow the two rules Sortr learned the hard way: match players **by name**
against what the window is offering rather than by index, and never judge success by a return
value — click, then wait for the window to actually show the change.

## Things that were verified against the installed Dalamud, not guessed

`Loot.Instance() → Loot*` with `Items` as `Span<LootItem>`; `LootItem.{ItemId, ItemCount,
RollState, RollResult, RollValue, WeeklyLootItem, Time, LootMode}`; `RollState { UpToNeed=0,
UpToGreed=1, UpToPass=2, Rolled=17, Unavailable=21 }`; `RollResult { UnAwarded, Needed, Greeded,
Passed, Awarded }`; `LootMode { Normal=0, GreedOnly=1, Unavailable=2, LootMasterGreedOnly=3 }`;
`AgentLoot.{NumItems, SelectedSlotIndex, HoveredSlotIndex, HoveredItemId, IsAddonShown()}`;
`AddonNeedGreed.{NumItems, SelectedItemIndex}`;
`AtkComponentList.{SelectItem(int, bool), DispatchItemEvent(int, AtkEventType), GetItemCount(),
SelectedItemIndex}` and `AtkEventType.ListItemClick = 35`;
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

  **Those matched rows then bound the rest of the read.** `DiscoverRewards` used to walk every
  `SpecialShop` row in the game, keyed only on the book item. That is fine for an old tier whose
  books nothing else wants, and wrong for a current one: the same books turn up in another NPC's
  rows and the tier came back full of entries from a shop nobody had opened. With no shop open it
  still falls back to the whole sheet — that is the only thing it can do — but it says so in chat,
  because the two answers are trustworthy to very different degrees.

  `DiscoverAugments` is deliberately *not* bound this way. Augmenting is a different NPC, so
  restricting it to the coffer shop would find nothing.

- **The upgrade materials**, indirectly. They are always among the rewards the exchange found and
  could not file as gear — the shop sells coffers, materials, and a few mounts and minions — so the
  material picker offers the unassigned rewards before it offers the whole item sheet. Picking one
  files it against that side in the reward table too, so it stops being offered for the other two.
  Searching thirty thousand items for a name nobody remembers is still there underneath, for the
  tier where the discovery came up empty.

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

## The loot policy is three settings, and one rule reads them

`DropOrder.Rank` is the only thing anywhere that decides who a drop goes to. It reads three
settings and nothing else:

| Setting | What it does |
|---|---|
| `RoleOrder` + `UseRoleOrder` | Damage, then tanks, then healers. A **gate**: a healer waits while a tank still wants the piece, whatever else the rules prefer. |
| `UsePlayerOrder` | Whether the roster's own order counts inside a role. |
| `Spread` (0…1) | How much of the loot to share out. 0 funnels it to the top of the order; 1 gives every drop to whoever is furthest behind. |

The arithmetic mixes a **place** against a **quantity**, on one scale: `(1 − spread) × position +
spread × served`, where position is the candidate's place in the declared order and served is how
many items they have already won. **One item won counts as one place.** Role is applied above that as
a gate rather than as a term, so sliding all the way to "share it out" shares within a role rather
than handing a piece past one.

That calibration is the whole of the slider's feel, and getting it wrong made the slider useless.
The first version mixed two *ranks* — position in the declared order against position on a sorted
neediness list. With two candidates both axes are {0, 1}, so the scores are always `s` against
`1 − s` and the order flips at exactly 0.5 no matter how far apart the two are. It was reported as
"the slider behaves like a switch", and it was. Normalising the ranks would not have helped: the
extremes are 0 and 1 by construction either way. Only an absolute quantity moves the tipping point.
Now someone at the top of the order who has won three items is passed at 0.25, one who has won a
single item at 0.50, and one who has won nothing is never passed at all. Open needs break ties
inside a single item and can never outweigh one.

**This replaced five weights feeding a simulation, and the reason was not accuracy.** The old model
ranked candidates by running a full simulation *per candidate* and comparing finish weeks. It gave
defensible answers that nobody could explain or steer: a multiplier on a projected week is a
preference the arithmetic can outvote, so a healer saving four weeks beat a damage dealer saving
one, and the only way to stop it was a slider whose effect could not be predicted. What a static
actually decides is small and categorical — which roles first, which players first, how much to
share — and all three of those are now settings rather than something inferred.

The simulation is still run. It no longer decides anything; it says **when** everyone would be
finished, which is its own question and the one the Plan tab's top table asks.

### One rule, so the tables cannot disagree

There used to be two. The loot window used the per-candidate ranking; the projection used a rule of
its own inside `WeekSimulator.Best` ("whoever has most left"). They genuinely disagreed, which is
how the same coffer came to name one player in the plan and another in the chest. Both now call
`DropOrder.Rank`, and the harness asserts they name the same player at three points on the slider.

`Forecast` still decides the coming week itself rather than letting the simulator do it, for a
different reason: each award carries `Considered` — the ranking it actually came off. "Next drops"
used to print the winner from the week's calculation beside runners-up from a fresh one that knew
nothing about the earlier drops of that same week, and a table that argues with itself reads as a
bug whichever half is right.

"If it dropped right now" is a deliberate exception: it judges every kind of drop on its own with
nothing else handed out. It can name someone else, and that is not a contradiction — the week above
has already given the earlier drops away.

## What the simulation is still for

`WeekSimulator` plays the rest of the tier forward and reports the week the **last** player
finishes. Two things are deliberately assumed, and both are written on the class:

- Coffers come up evenly — the drop pool is walked round-robin, not rolled. A slot in a pool of
  four at two drops a week therefore appears every other week, which is its average rate.
- Every fight is cleared every week. A group clearing three finishes later than forecast, but the
  ranking between candidates does not move.

Weeks are counted **from now**. Week 1 is the next reset.

`Slots.NeedsRaidResource` is what keeps the model small: plain tome and crafted pieces are
satisfied the moment they are chosen, because they cost the raid nothing. Only `Raid` and
`TomeAugmented` ever compete for a drop, and for `TomeAugmented` it is the *material* that is
tracked, never the tome piece.

## Sharing out counts what people have had, not what they still owe

The neediness half of the rule sorts on **items already won** first and open needs second. It reads
backwards until you try the other way round: ordering by "most left" alone hands every coffer of a
fight to the same player, because taking one does not make their list short enough to matter. Won
items are what a group means when it says someone has had their share.

This is also why a test that looks obvious can fail for the right reason. Asserting that "shared
out, the body coffer goes to the player with four needs" fails in a full simulation — by the time
fight 3 comes round, that player has already taken the head and hands from fight 2 and is now the
one who has had most. The rule is right; the assertion was measuring the week, not the rule. Rule
claims are asserted against `DropOrder.Rank` directly, and only claims about a whole week go
through the simulator.

## Expert mode

`Configuration.ExpertMode`, off by default, two radio buttons rather than a checkbox — both settings
are a position, and a static that keeps its lists by hand is not missing anything.

Off, a slot is a word and a tick: "Raid, done". That is the whole of what a distribution needs, and
it is maintainable by hand. On, every slot carries the **item actually equipped** and the **item
aimed at**, which is what a damage estimate needs — and which is only maintainable because the gear
scan fills the equipped side in on its own.

Both live in the same `SlotNeed`: `BisItemId` from the import, `EquippedItemId` and `EquippedSource`
from the scan. Expert mode did not add a data model, it added a view onto one that was already
there and mostly invisible.

### The roster in expert mode

The grid does not survive eleven slots times two sides, so it is replaced rather than widened:

- A folded-away **player list** — who is in the static, their job, link and books. What you touch
  when somebody joins, which is rarely. It reuses `DrawPlayerCell` / `DrawImportCell` /
  `DrawTokenCell` unchanged, so a link or a book count behaves identically in both modes.
- **One tab per player**, and inside it two panes: *Wearing* on the left, *Aiming at* on the right.

Two panes rather than a table with an Is and an Ought column. Item names run to forty characters —
a table would wrap or cut them — and the question this view answers is "what is still wrong with
this set", which is read down one column at a time.

The slot name sits at a fixed offset (`SlotLabel`) so both panes line up without being tables. Each
pane is an `ImRaii.Child` so a long name clips instead of shoving the other column off screen.

Editing a need goes through **`DrawNeedPopup`**, shared with the simple grid. Two views with their
own idea of how a need is edited is how you end up with two subtly different sets of rules.

### Measured stats, not reconstructed ones

**The game will tell you another player's finished attributes, and it will not tell you their
melds.** `AgentInspect.ItemData` carries an item id, an icon and nothing else — no materia, no high
quality flag. But `UIState.Inspect.BaseParams` is a span of 74 totals the game itself computed,
indexed by `BaseParam` row id, with materia, food and every trait already in them.

That one fact decides the whole shape of the damage estimate:

> Measured stats are the truth. Arithmetic is only ever used for the **difference** a swapped item
> would make.

`AttributeReader` reads those totals for the local player (`PlayerState.GetAttributeByIndex`) and
for whoever the examine window is showing. `GearScanner` stores them on the roster row as a plain
`BaseParam` id → value map, because for anybody but the local player they exist **only while that
window is open** — closing it takes them with it.

`BaseParam` row ids and `PlayerAttribute` values are the same list. That is not a coincidence worth
relying on blindly, but it was verified column by column across all 73 entries, and it is what lets
one set of constants serve both readers. They live in `Data/Attributes`.

Two consequences worth stating, because they simplify a lot:

- **Equipped materia is never stored.** It is already inside the measured totals. Only
  `SlotNeed.BisMateria` exists, for the target set — the one thing that cannot be measured, because
  nobody is wearing it.
- **Food is the same.** Only `RosterMember.TargetFoodItemId`, from the import.

### What the stat probe settled

`Debug → Write stat probe` exists because four pieces of game data were readable as *shapes* and not
as values. Two runs answered all but one of them. The probe stays, because a patch can move any of
this and the same two clicks re-check it.

**The level constants are two thirds read from the game now.** `ParamGrow` has no column called
MAIN, SUB or DIV, but two of them are in there under other names:

| Level | `BaseSpeed` | `LevelModifier` |
|---|---|---|
| 80 | 380 | 1300 |
| 90 | 400 | 1900 |
| 100 | 420 | 2780 |

Those are **SUB** and **DIV** exactly, at all three levels. `LevelTable` therefore reads them from
the sheet rather than carrying them.

**MAIN is not in that sheet** and stays a constant — 340 / 390 / 440 for 80 / 90 / 100. It is not a
guess, though: a level 100 paladin's untouched stats measured 421 dexterity, 441 mind and 265
intelligence against job modifiers of 95, 100 and 60, which is `floor(440 × mod / 100)` plus a clan
bonus of one to three every time. Three independent confirmations of 440. The other two levels are
unverified and no longer matter much.

A second confirmation fell out of the same numbers: **an untouched substat sits at exactly SUB.**
Direct hit, skill speed and spell speed all measured 420 on a set carrying none of them.

**`ClassJob.PrimaryStat` is a `BaseParam` row id.** PLD 1, NIN 2, BLM 4, SGE 5 — strength, dexterity,
intelligence, mind. And `ModifierStrength` and friends are the job modifier the weapon damage term
needs: 100 for a paladin, 112 for a samurai, 115 for a black mage's intelligence.

**`BaseParamSpecial` is the high quality bonus, not the high quality total.** A craftable saw reads
`normal [70]=29 [71]=16` against `special [70]=3 [71]=3`. HQ value is normal plus special.

**Weapon damage is not in the attribute table.** `PlayerAttribute` 12 and 13 both measured **0** on a
paladin holding a sword. Delay is there and correct (2240 for a 2.24 second weapon), but damage has
to come off the item — which is exact anyway, since weapon damage cannot be melded.

**`InventoryType.Examine` holds the inspected character's real `InventoryItem`s.** `loaded=True`,
fourteen slots, right item names, right high quality flags, and non-zero meld counts. This is more
than was expected: melds *are* readable for other players after all, through the inventory container
rather than through `AgentInspect`. Nothing depends on it yet — measured totals already cover the
current set — but it is the way in if the gain of a new piece ever needs its materia modelled
properly rather than assumed to carry over.

**Still open: whether `InventoryItem.Materia[i]` is a `Materia` sheet row or an item id.** The
character it was run on has no melds anywhere, so the probe had nothing to read. It does not block
anything — equipped melds are not stored, because they are already inside the measured totals — and
the answer is one melded item away whenever it is wanted.

### Reading gear without being asked

Expert mode lives or dies on the equipped side being current, and nobody presses a button eight
times a week. `GearScanner` therefore arms itself on `IClientState.TerritoryChanged` and reads the
party on its own — `Configuration.AutoReadGearOnEnter`, on by default, expert mode only.

Three things it waits for, and each one was worth waiting for:

- **Eight seconds.** The party list is not populated the instant the zone changes and the condition
  flags lag it, so `TerritoryChanged` only writes down that a scan is owed; nothing is read there.
- **`BoundByDuty`.** Otherwise every trip to the market board examines the party.
- **Out of combat.** If the settle timer lands mid-pull it re-arms rather than giving up — the
  duties worth scanning start fast.

**The role check is the whole safety of doing this unasked.** A static's tank turning up on a damage
job for a farm run is normal; writing that gear onto their tank row would quietly wreck the plan. So
`StartForRoster` reads only players the roster knows *and* whose current job is the role the roster
expects, and a mismatch is skipped and named — never applied, never silent.

`Start()` (the button) is unchanged and still reads everyone present, roster or not.
`StartFor(member)` is the single-target entry point the sheets needed; all three funnel into one
`Begin`, so the queue, the cooldown handling and the "never clears Obtained" rule cannot drift apart.

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

The Tier tab lists disagreements between a rule and the shop, one line per category, under *Book
exchange*. Comparing every reward individually produced twenty weapons all disagreeing with the same
rule in the same way, which says nothing twenty times.

**There is no editor for the costs any more.** There was one, a table of the eight rules with the
price editable. It came from a time when the numbers had to be typed in; now they are read off the
shop the player is standing at, and an editable copy of what the game just told us is somewhere to
break a working tier rather than somewhere to fix one. What is left in its place is the mismatch
list above and one line saying whether the tier trades books down — the part of that section that
was actually load-bearing, since it decides who is stuck.

### Whether every coffer drops

`TierDefinition.AllCoffersDrop`, **on by default** — that is how savage tiers have worked for years.
A fight puts up one of each slot it can, all four accessories every week, and drop counts are
ignored. Off, it drops `DropCount` out of its pool and the same coffer can come up twice; the pool
is then walked round-robin, which reproduces each slot's average rate without pretending to roll
dice.

It only affects weeks the projection has to guess at. **The coming week always lists every coffer a
fight can put up**, whatever the setting says. A drop count is a rate — it can tell you that two of
the four accessories will turn up, and it cannot tell you *which* two. Answering "the first two in
the pool" made the table wrong and useless at once: the whole point of it is to know who each coffer
is for before it drops.

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
- **What to spend them on this week**, from `LootPlanner.ShouldBuyNow` — the plan's own week-1
  purchases, not everything the books would stretch to. The first version listed the latter, which
  for anyone mid-tier is most of their remaining list: it reads as a shopping list rather than an
  instruction, and two of the entries are usually alternatives where buying one puts the other out
  of reach. The plan already decides; this reports the decision.

Those purchases happen **before** the coming week's drops in `Forecast`, and they are fed into the
same `PendingAward` list the drops are ranked against. Books in hand were earned by clears that have
already happened, so spending them is not something to model at the end of the week — and a player
about to buy the body piece should not also be handed the body coffer.

*Next drops* is drops only — a coffer is a decision made in the instance with seven other people
wanting it, an exchange is one player walking to an NPC, and mixing them into a table about who
should be given what made neither readable.

*Expected schedule* keeps both, in the order they happen: one tab per week, its fights listed in
order and then a **Book exchange** line with everything that week's books buy. Purchases were filed
under the fight whose books pay for them, which is true and unhelpful — a fight heading in that list
means "go and clear this", and a purchase is not that.

There was a second tab, *Planned book exchanges*, listing every purchase of the whole tier. It went
once a week could be read whole. It had existed because a week could not: the purchases were
scattered through the fights, so seeing them together needed a view of their own. Fix the week and
the view stops answering a question anybody was asking.

**And what it costs includes the trade.** `BookLedger.Pay` reports the conversion it had to make,
which rides on the award as `PlannedAward.Traded` and shows as `3 × M2S (exchange 1 × M4S)`. Without
it the plan says "buy the head piece with three second-fight books" to a player holding two, which
is not wrong so much as unfollowable. Reconstructing it from the counts afterwards would be a second
calculation to disagree with the first — the ledger already knows, so it says.

### A material is named after the piece, not the side

`PlannedAward.What` for an upgrade reads "Body upgrade", not "Left upgrade", and `TakeUpgrade` hands
back which piece it consumed so the award can say.

This was reported as the schedule showing duplicates: one line plain, the same line again with
"(books)". It was not duplicating anything. A player with augmented head, body and legs owes
**three** armour materials, all of them `GearSide.Left`, and a week where one dropped and another
was bought printed the same words twice. Correct, and indistinguishable from a bug — which for a
plan somebody has to act on is the same thing. The harness pins it: no player is given the same
label twice in one week, and every material award names a piece.

### Two calculations, on purpose this time

`LootPlanner.ComingWeek()` answers "this reset": books in hand spent first, then every coffer each
fight can put up, each award carrying the ranking it came off. It drives *Next drops* and the Loot
tab's book column.

`LootPlanner.Schedule()` is one simulator run from week 1 and answers "the whole tier". It drives
the finish weeks and both week-by-week tabs.

They agree about week 1's drops without being made to, because both hand the same pool to the same
rule — which is the only kind of agreement worth having. What they do *not* share is a run, and that
matters: the schedule used to be "the coming week, then the simulator from week 2", and since the
simulator is the only thing that hands books out, **week 1 gave nobody a book**. Every purchase in
the plan sat one week later than it should, for as long as that split existed. Now week 1 means one
book from each fight, week 2 means two, on top of whatever each player already holds — and spare
last-fight books trade down inside that, through `BookLedger`, exactly as they do anywhere else.

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
3. **Plan** → the forecast should give plausible week numbers, and *Next drops* should list every
   coffer each fight can put up, each named for someone who actually still needs it.
4. Form a two person party, set **Lootmaster** in the duty finder, enter an old dungeon undersized.
   The loot window behaves identically and nothing is on a weekly lockout.
5. **Debug** → live state should say leader `you` and lootmaster `True`. Write a capture file.
6. **Loot** → the ranking should appear. Assign by hand in the game window and confirm the tracker
   ticks it off; if not, the reason is in the chat probe.
7. Then the row's own *Assign* button, in that same dungeon, more than once in a row — the failures
   that mattered all showed up on the second assignment, not the first.

## Known loose end

`.github/workflows/pr-build.yml` uploads `LootMastr/bin/x64/Release/LootMastr/*`, inherited from
the template. Locally the SDK puts the built plugin in `bin/x64/Release/` and leaves only the
Dalamud download in the `LootMastr/` subfolder — Sortr's output looks the same. Worth checking on
the first pull request whether CI differs, rather than changing it on the strength of one machine.
