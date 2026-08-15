# Planner harness

`Program.cs` asserts on the parts of LootMastr that are pure calculation. It is not a test project
and is not built with the plugin — the planner has no Dalamud dependencies precisely so it can be
checked without a game running.

To run it:

```bash
dotnet new console -o /tmp/lootmastr-harness --force
cp Harness/Program.cs /tmp/lootmastr-harness/
cp LootMastr/Data/GearSlot.cs LootMastr/Data/RaidRole.cs LootMastr/Data/TierDefinition.cs LootMastr/Roster/RosterMember.cs LootMastr/Planning/PlayerPlan.cs LootMastr/Planning/PriorityRules.cs LootMastr/Planning/WeekSimulator.cs /tmp/lootmastr-harness/
dotnet run --project /tmp/lootmastr-harness
```

It exits non-zero if anything fails. The seven copied files are the whole of the planner: if that
list ever needs to grow, something game-facing has leaked into `Planning/`.

## What it pins down

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
- Both ring slots are filled by the one ring coffer.
- Books are spent the week they cover something, and books already held count.
- Costs come from the category rules, and the last fight's books trade one for one into earlier
  ones — but only the ones not still owed to that fight, so a weapon never gets traded away.
  Own books are spent before anything is converted, and conversion never runs backwards.
- A drop goes to whoever is furthest from done, with damage dealers winning an even tie.
- Nobody starves when a coffer pool is scarce.
- The same roster produces the same plan twice.

## One result worth knowing

Given a player who needs five pieces and a player who needs only the piece that just dropped, the
planner hands it to the player it *finishes*, not the one with more left — because in that scenario
both choices leave the group's last week unchanged, and finishing someone outright is strictly
better. The greedy "most remaining needs" rule inside the simulation and the ranking that wraps it
are allowed to disagree; the ranking is the authority, because it is the one that plays the whole
tier forward.
