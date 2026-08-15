# Sharing the sheet — is it possible, and how

Analysis, not implemented. The question was whether the roster can be shared "in chat or similar"
so the static can work on it together.

Short answer: yes, in two useful steps, and neither needs a server. The one that sounds most
obvious — sending it through the game's chat — is the one worth not building.

## What has to be shared

Only the roster. Everything else either lives in the game already or is derived:

| | Shared? | Why |
|---|---|---|
| Members, jobs, gear planner links | yes | typed in by hand, the expensive part |
| Per-slot source (raid / tome+ / …) | yes | the actual sheet |
| Obtained ticks, upgrade ticks | yes | this is what changes weekly |
| Book counts | yes | half of what the forecast runs on |
| Tier definition | separately | group-wide, and rediscoverable from game data in one click |
| Forecast, rankings | no | recomputed from the above in milliseconds |

Estimated size: a member is roughly 300–400 bytes as compact json, so eight are around 3 KB.
Gzip on data that repetitive gets it well under a kilobyte, and base64 adds a third back — call it
**1000–1300 characters**. That matters because it decides which transports are even on the table.

## Option A — a paste-able code (recommended first)

Serialize → gzip → base64 → clipboard. The other side pastes it back.

- **Fits a single Discord message.** The 2000 character limit is the binding constraint and the
  estimate above sits under it. If it ever does not, dropping the BiS item ids (they are only kept
  so a set can be re-filed without refetching) takes out the largest repetitive field.
- **No infrastructure, no accounts, nothing leaves the machine that the user did not paste.**
- **Familiar.** Penumbra, Glamourer and BossMod all share presets exactly this way, so the static
  already knows the gesture.
- **Cost:** roughly a day. Two buttons, a versioned envelope, and a merge (see below).

Limitation: it is a snapshot. Someone has to send a fresh code after a raid night. For a static
that already coordinates in Discord this is closer to how they work than a live system would be.

## Option B — a shared file (recommended second)

A json file in a folder everyone syncs anyway — OneDrive, Dropbox, a Google Drive folder. The
plugin reads it on window open and writes on change.

- **Real collaboration**, without hosting anything or holding anyone's data.
- **Dovetails with the Excel plan.** The same merge logic serves both, and the spreadsheet becomes
  a view of the shared file rather than a separate import path.
- **Cost:** a few days, mostly the merge and the "the file changed under you" handling.

Limitation: everyone needs the folder synced, and sync services do produce conflict copies. That is
survivable because of the ownership rule below.

## Option C — in-game chat (possible, and worth not doing)

Technically it works: chunk the base64 across messages with a marker prefix, have receiving clients
strip and reassemble them.

Against it, in order of severity:

1. **It is spam.** ~500 bytes per chat message means eight to ten messages for one sheet, in a
   channel people are trying to call mechanics in.
2. **Automated sending is the single most scrutinised thing a Dalamud plugin can do**, and doing it
   in bulk is how a plugin becomes a problem for its author.
3. **The transport is unreliable in the ways that matter.** Messages are rate limited, can be
   dropped, and arrive as text — so it needs sequencing, retries and reassembly to do worse what
   the clipboard does perfectly.
4. Everyone needs the plugin anyway, so it buys nothing over a pasted code.

The only thing chat is genuinely good for is what `ChatAnnouncer` already does: one short line
naming who gets what, for the humans to read.

## Option D — a hosted backend

Live sync, no shared folder, changes appear as they happen.

Only worth it if the group actually wants live editing during a raid night, because the costs are
real and permanent: something to host and keep running, or a third-party paste service. Either way
**character names would leave the machine**, which is a decision for the group to make explicitly
rather than something to switch on by default. If it is ever built it should be opt-in, with the
URL visible and a plain statement of what is uploaded.

Plugin-to-plugin IPC does not help here — Dalamud IPC is between plugins on one client, not between
players.

## The part that is actually hard: merging

Not the transport. Two people editing the same sheet is what breaks these systems, and
last-write-wins on the whole document loses a raid night's ticks.

The rule that makes it tractable:

> **Each player owns their own row.** Merge per member, not per document.

- Key on `Name@World`, which the roster already uses.
- Give each `RosterMember` a `LastEdited` UTC stamp, set wherever the config is saved.
- Merging two sheets = union of members; where both have a key, the newer stamp wins **for that
  member only**.
- The tier definition is group-wide, not per-player, so it travels separately and the raid leader
  owns it. It is also rediscoverable from game data in one click, which makes a bad merge cheap.

This holds up because it matches how the data is actually produced: people tick off their own
pieces, and the leader's loot tracking writes to the row of whoever received the item. Genuine
simultaneous edits to *the same player's* row are rare, and when they do happen losing one tick is
recoverable in a way that losing a whole sheet is not.

Worth adding either way: a summary before applying an import — "3 players updated, 1 added,
Astra's body slot goes from Raid ✓ back to Raid" — so a bad merge is visible before it lands rather
than after.

## Recommendation

Build **A** now. It is a day of work, needs nothing from anyone, fits how a static already talks to
itself, and forces the versioned envelope and the merge rule into existence — which is most of
**B**, whenever that becomes worth it.

Do not build **C**. Consider **D** only if the group asks for live editing, and make it opt-in.
