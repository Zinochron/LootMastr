# LootMastr

Loot planning and distribution for savage statics, as a Dalamud plugin.

Most loot tools tell you who is *allowed* to take a drop. LootMastr works out who *should* — by
playing the rest of the tier forward, drops and books together, and picking the assignment that
gets the whole group into full BiS soonest, with damage dealers ahead on an even call.

Open it with `/lootmastr`.

## What it does

- **Keeps the static's need list.** Import each player's gear set from XIVGear or Etro, or fill the
  grid in by hand. Every slot is marked as raid, tomestone, augmented tomestone or crafted, and only
  the ones that actually cost the raid something are ever planned around.
- **Counts books.** A player two books short of buying a slot outright should not be competing for
  the coffer, so books are part of the plan rather than an afterthought. Clearing a fight offers to
  count everyone's.
- **Forecasts the tier.** Who finishes in which week, what is expected to drop, and where each piece
  is expected to go.
- **Answers the call.** When a chest opens, the Loot tab ranks who should get each item, with the
  reasoning behind every placement in one line.
- **Ticks itself off.** What people receive is picked up from the chat log.

## Loot rules

Assigning loot to another player is only possible with the party on the **Lootmaster** loot rule,
which has to be set in the duty finder before the party enters and only works for a preformed or
undersized party. Without it LootMastr still ranks every drop — it just cannot apply the result,
and says so.

> **Assigning is not finished yet.** LootMastr decides correctly and can announce the result in
> party chat, but does not yet click the game's loot recipient control; that path has to be
> captured from a live duty first. See `LootMastr/README-DEV.md`.

## Which tier

Ships with AAC Heavyweight (Savage). Book costs, the augmented tomestone set and which zone belongs
to which fight are all read out of the game rather than typed in, so they cannot drift out of date.
A new tier is a json file under `LootMastr/Data/Tiers`, not a rebuild, and everything in it is
editable in the Tier tab.

## Building

```bash
dotnet build --configuration Release
```

Needs XIVLauncher with in-game features enabled, for the Dalamud assemblies under
`%AppData%\XIVLauncher\addon\Hooks\dev`.

`Harness/` checks the planner without a game running — see its README.
