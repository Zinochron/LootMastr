# Planner harness

`Program.cs` asserts on the parts of LootMastr that are pure calculation. It is not a test project
and is not built with the plugin — the planner has no Dalamud dependencies precisely so it can be
checked without a game running.

To run it:

```bash
dotnet new console -o /tmp/lootmastr-harness --force
cp Harness/Program.cs /tmp/lootmastr-harness/
cp LootMastr/Data/GearSlot.cs LootMastr/Data/RaidRole.cs LootMastr/Data/TierDefinition.cs LootMastr/Roster/*.cs LootMastr/Planning/*.cs LootMastr/Planning/Dps/*.cs /tmp/lootmastr-harness/
cd /tmp/lootmastr-harness && dotnet add package Newtonsoft.Json --version 13.0.3 && cd -
rm /tmp/lootmastr-harness/LootPlanner.cs
dotnet run --project /tmp/lootmastr-harness
```

It exits non-zero if anything fails. What gets copied is the whole of `Planning/` bar `LootPlanner`,
which is the one file there that reaches for the roster and the tier catalogue, plus the four data
types those files read. **If that ever needs a Lumina or Dalamud reference to compile, something
game-facing has leaked into `Planning/`** — that is the check this list is really performing.

`Planning/Dps/` in particular carries no game references at all. The damage formula is arithmetic
and belongs on the side of the line that can be asserted on without a client running, which is how
a recast of five seconds was caught before anybody saw it on screen.

## What it pins down

- Swapping a piece counts both sides complete: a materia adds to a stat the item already has rather
  than becoming a second entry for it, and trading a crafted piece holding five melds for a raid piece
  holding two is never a pure gain however much better the item is.
- Tomestones are a second currency with a weekly cap: the week somebody finishes paying comes out
  the same whether it is computed in closed form or reached by spending week by week, a material
  prefers whoever can wear the piece under it but still goes out when nobody can, and an alt is not
  in a field a main is standing in however many pieces that main has already won.
- Need lists only contain slots that actually cost the raid something — plain tome and crafted
  pieces never compete for a drop.
- Gear is classified from item level plus name alone, with no shop data: raid and augmented tome
  gear share an item level, and only the `Augmented …` prefix separates them. Levels the tier does
  not know are left undecided rather than guessed.
- A coffer's slot is read out of its name, never landing outside the slots that book buys — and
  "Grand Champion's Earring Coffer" resolves to the earrings rather than to a ring.
- Target, given and actual stay separate: a piece that was handed over but is not being worn is
  flagged in the grid and still counts as satisfied, so it never comes up for assignment twice.
  Nothing is flagged before that character's gear has been read.
- Raid gear is unique, so at most one raid ring is ever counted however many a set claims, and a
  raid ring paired with an augmented one is two separate needs.
- Books are spent the week they cover something, and books already held count.
- Shop prices are taken as the most common one, never the cheapest, so a separately sold shield
  does not price every weapon in the tier at three books.
- With "every coffer drops" on a fight puts up one of each slot and drop counts are ignored;
  with it off the pool is walked at the set rate.
- Costs come from the category rules, and the last fight's books trade one for one into earlier
  ones — but only the ones not still owed to that fight, so a weapon never gets traded away.
  Own books are spent before anything is converted, and conversion never runs backwards.
- The loot policy is three settings and one rule. Roles are a gate — damage, then tanks, then
  healers by default — and a healer ahead on every other count still waits behind a damage dealer
  until the gate is switched off. Inside a role the `Spread` slider reaches both ends: at 0 the top
  of the player order takes a whole week's coffers, at 1 they go to whoever has won least. A tie
  falls to the declared order, and switching the player order off leaves need in charge.
- The ranking and the simulated week name the same player, at three points on that slider. Two
  rules answering the same question is the bug this pins down.
- The damage formula: a recast of 2.50 seconds at exactly SUB, truncated to hundredths, and shorter
  as speed goes up. Every stat that should raise damage does, which is what catches a sign error —
  the way this formula goes wrong is not subtly, but backwards in one term.
- Speed changes no single hit and does change the DPS. Tenacity counts for a tank and is left out
  for everybody else. A caster's recast follows spell speed and ignores skill speed.
- The absolute scale, as a band rather than an equality: a geared tank's 100 potency in the
  thousands and their DPS in the tens of thousands. This is the check that catches a stray factor
  of a hundred, which the first version had — it produced 1.2 million damage per second.
- A missing weapon, main stat or job modifier produces no estimate at all rather than a low one.
- Nobody starves when a coffer pool is scarce.
- The same roster produces the same plan twice.

## One result worth knowing

Given a player who needs five pieces and a player who needs only the piece that just dropped, the
planner hands it to the player it *finishes*, not the one with more left — because in that scenario
both choices leave the group's last week unchanged, and finishing someone outright is strictly
better. The greedy "most remaining needs" rule inside the simulation and the ranking that wraps it
are allowed to disagree; the ranking is the authority, because it is the one that plays the whole
tier forward.
