# LootMastr

Loot planning and distribution for savage statics, as a Dalamud plugin.

Most loot tools tell you who is *allowed* to take a drop. LootMastr works out who *should* — by
playing the rest of the tier forward, drops, books and tomestones together, and picking the
assignment that gets the whole group into full BiS soonest.

Open it with `/lootmastr`.

## Installing

In game: **Dalamud Settings → Experimental → Custom Plugin Repositories**, add

```
https://plugins.antimates.org/repo.json
```

then Save, and find LootMastr in the plugin installer. Updates arrive on their own from there.

Everyone in the static wants the same version. The plugin syncs a shared document between clients,
and two installs a few releases apart can disagree about what is in it — so the repository is worth
adding rather than passing a zip around.

## What it does

- **Keeps the static's need list.** Import each player's gear set from XIVGear or Etro, or fill the
  grid in by hand. Every slot is marked as raid, tomestone, augmented tomestone or crafted, and only
  the ones that actually cost the raid something are ever planned around.
- **Counts books and tomestones.** A player two books short of buying a slot outright should not be
  competing for the coffer, and 450 tomestones a week is a real constraint on when a set is
  finished. Both are part of the plan rather than an afterthought.
- **Forecasts the tier.** Who finishes in which week, what is expected to drop, and where each piece
  is expected to go — with the reasoning behind every placement in one line.
- **Answers the call.** When a chest opens, the Loot tab ranks who should get each item, and can
  hand it over for you.
- **Records what happened.** Every handover goes into a history with a date and a time, so "who got
  the second twine" has an answer weeks later.
- **Reads gear from the game.** Optionally estimates what a piece is worth to each player in damage
  per second, so a coffer can go where it helps most rather than where it is merely due.

## Sharing with the static

One person maintaining the sheet is how these things usually work, and it means the sheet is wrong
between raid nights. LootMastr can instead keep the static's roster, tier and settings on a server,
with per-character read, write and admin rights — so everybody sees the same plan and only the
people who should can change it.

It is off until you set it up, and the URL it talks to is shown in plain text wherever a static is
configured. Nothing leaves the machine before then.

The raid schedule lives there too, with reminders before each session — a notification, a line in
chat, or a countdown next to the clock, whichever you want.

## Loot rules

Assigning loot to another player is only possible with the party on the **Lootmaster** loot rule,
which has to be set in the duty finder before the party enters and only works for a preformed or
undersized party. Without it LootMastr still ranks every drop — it just cannot apply the result,
and says so.

With it, LootMastr opens the assignment window, picks the planned player, and leaves the game's own
"Allow X to claim Y?" for you to answer — or answers that too, if you tell it to. It only ever
confirms once that dialog names the player and the item it meant.

Nothing is automatic unless you ask for it. The default is to show you the choice and wait.

## Which tier

Ships with AAC Heavyweight (Savage). Book costs, the augmented tomestone set and which zone belongs
to which fight are all read out of the game rather than typed in, so they cannot drift out of date.
A new tier is a json file under `LootMastr/Data/Tiers`, not a rebuild, and everything in it is
editable in the tier window — `/lootmastr tier`.

## Commands

| | |
|---|---|
| `/lootmastr` | open and close the main window |
| `/lootmastr roster` `plan` `loot` `settings` | straight to a tab |
| `/lootmastr statics` | the statics window, where sharing is set up |
| `/lootmastr tier` | the tier window |
| `/lootmastr scan` | read the gear of everyone in the party who is in the roster |

## If something goes wrong

Run `/lootmastr debug`. It reveals a tab that says what the plugin is currently seeing — whether it
found the loot window, whether it recognised the zone, and every chat line it considered together
with what it made of each one. If an item was received and never ticked off, the reason is in there.

The same tab writes a capture file next to the config with the raw values behind the loot window.
That file is what to attach to a bug report: the indices it reads are a patch away from moving, and
the capture says which one moved.

## Building

```bash
dotnet build --configuration Release
```

Needs XIVLauncher with in-game features enabled, for the Dalamud assemblies under
`%AppData%\XIVLauncher\addon\Hooks\dev`.

`Harness/` checks the planner without a game running — the parts of LootMastr that are pure
calculation have no Dalamud dependencies precisely so they can be asserted on. See its README.

`LootMastr/README-DEV.md` is the long version: what was verified against the game rather than
guessed, and why each awkward decision is the way it is.

## Licence

AGPL-3.0-or-later. See [LICENSE.md](LICENSE.md).
