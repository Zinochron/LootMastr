# The static server — API contract

What the plugin sends and expects back. Written to be implementable in PHP on shared hosting; the
reference target is a manitu **Webhosting M** package (PHP 8, MySQL, `.htaccess`).

The client is `LootMastr/Sync/SyncClient.cs`, the wire types are `LootMastr/Sync/SyncDto.cs`. Where
this document and that code disagree, the code is what actually goes over the wire — but tell me,
because one of them is then wrong.

## What this thing is

**A safe deposit box, not a database.** The server stores one JSON blob per static, hands it back to
whoever proves they may have it, and remembers who may do what. It never looks inside.

That is the load-bearing decision. Every rule about loot — who gets the twine, when the tomestones
run out, what an alt may take — lives in the plugin, where a harness of 287 checks can run it without
a game attached. A server that understood any of that would be a second implementation of rules that
are hard to get right once.

Three consequences worth naming:

- The server needs **no** upgrade when the plugin learns a new field.
- A bug in the plugin cannot be fixed server-side. It also cannot be *caused* server-side.
- The Discord bot is not a second data path. It is another client with a read token.

## Authentication

Two secrets, and they are not interchangeable:

| | What it is | Who has it | Where it lives |
|---|---|---|---|
| **Password** | Proof you belong to this group | Everyone in the static | Typed each time. **Never stored by the plugin.** |
| **Token** | Proof you are *this character* | One per character per static | The client's config, sent as `Authorization: Bearer …` |

The password is only ever accepted by `POST /statics` and `POST /statics/{name}/join`. Everything
else takes a token. That split is the whole point: the password is shared and therefore weak, and it
is exchanged once for something that is not.

**Rights hang off the token, and the server decides them.** A client says which character it is only
when claiming; after that the token says it. A `role` in a request body is data, never authority — if
the server ever trusts one, the entire permission system is decoration.

The plugin refuses to send a password to anything that is not `https`, except loopback. The server
should refuse too.

## Endpoints

Base URL is whatever the user typed, e.g. `https://example.com/lootmastr`. All bodies are JSON,
`Content-Type: application/json`, UTF-8.

### `POST /statics` — create

```json
{ "name": "Vindicta", "password": "…", "character": "Sima Vanham" }
```

`201` →

```json
{ "token": "…", "role": "admin", "character": "Sima Vanham" }
```

The creator is the admin. `409` when the name is taken.

### `POST /statics/{name}/join` — claim a token

```json
{ "password": "…", "character": "Yuma Misumi" }
```

`200` →

```json
{ "token": "…", "role": "read", "character": "Yuma Misumi" }
```

A character claiming again **replaces** their token — that is how somebody who reinstalled gets back
in, and it means a lost config is not a lost seat.

New characters start at `read`. Nothing about being in the roster grants more: the roster is inside
the blob, and the server does not read the blob.

### `GET /statics/{name}` — read

`200` →

```json
{
  "schema": 1,
  "revision": 42,
  "updated_by": "Sima Vanham",
  "updated_at": "2026-08-18T20:15:00Z",
  "role": "write",
  "document": { … }
}
```

`role` is what *this token* may do, so a client learns about a promotion on its next pull without
being told.

`document` is null when the static exists but nobody has pushed yet. The client treats that as "keep
what I have", not as "empty the roster".

### `PUT /statics/{name}` — write

Requires `write` or `admin`.

```json
{ "schema": 1, "base_revision": 41, "document": { … } }
```

`200` →

```json
{ "schema": 1, "revision": 42, "updated_by": "…", "updated_at": "…", "role": "write", "overwrote": 41 }
```

**Last write wins.** `base_revision` does not block anything — the group chose that deliberately,
because gear ticks come from whoever is loot master and there is only ever one of them.

`overwrote` is set when `base_revision` was behind the stored revision, and is the entire reason the
field exists: it turns a silent loss into a line on somebody's screen. Omit it when the write was
current.

The response does not echo the document. The client just sent it.

### `GET /statics/{name}/members`

`200` →

```json
[
  { "character": "Sima Vanham", "role": "admin", "claimed_at": "2026-08-10T18:00:00Z" },
  { "character": "Yuma Misumi", "role": "read",  "claimed_at": "2026-08-11T20:31:00Z" }
]
```

### `PUT /statics/{name}/members/{character}`

Requires `admin`.

```json
{ "role": "write" }
```

`200` → the same list as above, so one round trip both changes and refreshes.

Two rules the server enforces and the client does not:

- **An admin may not demote the last admin.** `409`, with a message saying so. Otherwise a static
  becomes unadministrable and only database access fixes it.
- `{"role": "revoked"}` — or `DELETE` on the same path — drops the character's token. Use it for
  somebody who left.

### `GET /tiers` and `GET /tiers/{id}` — the public library

No token. Tier definitions carry item ids and prices and **no character names**, so there is nothing
in them to protect.

```json
[ { "id": "aac-heavyweight-savage", "name": "AAC Heavyweight (Savage)", "updated_at": "…" } ]
```

### `PUT /tiers/{id}` — publish

Any valid token, from any static. The publisher is recorded as owner (store a hash of the token, not
the token) and only they may overwrite that id. Anyone else gets `409` and a free id in the message:

```json
{ "error": "owned", "message": "That id belongs to somebody else. Publish as aac-heavyweight-savage-2." }
```

A fork rather than a conflict. Without an owner the library is vandalised in a week; with a lock and
no fork, a typo in somebody else's tier is unfixable.

## Errors

Always JSON, never a bare status code — the plugin shows `message` to the user verbatim, and "403" on
a screen tells nobody anything.

```json
{ "error": "forbidden", "message": "This character may only read this static." }
```

| Code | When |
|---|---|
| `400` | Malformed body, missing field, unreadable JSON |
| `401` | No token, unknown token, revoked token |
| `403` | Valid token, insufficient role |
| `404` | No such static, no such tier |
| `409` | Name taken, last admin, tier owned by somebody else |
| `413` | Document over the size cap |
| `429` | Rate limited (see below) |

## Schema

```sql
CREATE TABLE statics (
    id            INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    name          VARCHAR(64)  NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,          -- password_hash(PASSWORD_ARGON2ID)
    revision      BIGINT       NOT NULL DEFAULT 0,
    document      LONGTEXT     NULL,              -- the blob, as sent
    updated_by    VARCHAR(64)  NULL,
    updated_at    DATETIME     NULL,
    created_at    DATETIME     NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE static_members (
    static_id   INT UNSIGNED NOT NULL,
    character_name VARCHAR(64) NOT NULL,
    role        ENUM('read','write','admin') NOT NULL DEFAULT 'read',
    token_hash  CHAR(64)     NOT NULL,            -- hash('sha256', token)
    claimed_at  DATETIME     NOT NULL,
    PRIMARY KEY (static_id, character_name),
    UNIQUE KEY (token_hash),
    FOREIGN KEY (static_id) REFERENCES statics(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE tier_presets (
    id          VARCHAR(64)  PRIMARY KEY,
    name        VARCHAR(128) NOT NULL,
    document    LONGTEXT     NOT NULL,
    owner_hash  CHAR(64)     NOT NULL,
    updated_at  DATETIME     NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE join_attempts (
    ip        VARBINARY(16) NOT NULL,
    at        DATETIME      NOT NULL,
    KEY (ip, at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

The document sits in the row rather than in a table of its own: there is exactly one per static, it
is only ever replaced whole, and a join to fetch it would buy nothing.

**Tokens are stored hashed.** They are bearer credentials — a database that leaks plaintext tokens
leaks working access. `sha256` is enough here and `password_hash` is not: tokens are long random
strings, not guessable, and they are looked up on every request. The **password** is the opposite
case and wants `PASSWORD_ARGON2ID`, which PHP 8 has built in.

## What the server must enforce

1. **Roles come from the token.** Look up `token_hash`, take the role from that row, ignore anything
   in the body claiming otherwise.
2. **Rate-limit `join` and `create` by IP.** They are the only endpoints where a password can be
   guessed; ten attempts in fifteen minutes is generous. Nothing else needs limiting — a token is not
   guessable.
3. **Cap the document.** A static of eight is a few hundred kilobytes; refuse over 4 MB with `413`.
4. **Bump `revision` inside the same transaction as the write**, or two simultaneous pushes get the
   same number and `overwrote` starts lying.
5. **Never log a document or a token.** Character names and gear are the payload, and a log is a
   second copy of it nobody decided to keep.
6. **HTTPS only.** Refuse plain HTTP outright rather than redirecting: a redirect happens after the
   password has already crossed the wire.

## Implementation notes for shared hosting

- **Routing.** One `index.php` with `.htaccess` rewriting everything to it, then match on
  `$_SERVER['REQUEST_METHOD']` and the path. Five endpoints do not need a framework.
- **The bearer header can vanish.** Apache with CGI/FastCGI strips `Authorization` unless told not
  to. Add `SetEnvIf Authorization "(.*)" HTTP_AUTHORIZATION=$1` to `.htaccess` — this is the single
  most common reason a working implementation returns `401` to everything.
- **Read the body with `file_get_contents('php://input')`.** `$_POST` is empty for JSON.
- **Always answer JSON**, including on fatal errors — set an exception handler that emits the error
  envelope, or the plugin shows an HTML error page's first line to the user.
- **Tokens**: `bin2hex(random_bytes(32))`. Not `uniqid`, not `mt_rand`.

## The Discord bot

Nothing extra to design. Give it a character name like `Discord` and a `read` token, and it pulls the
same document everyone else does. The plugin's rules are not reimplemented in it — what a bot can
usefully post is what the document already says, and the interesting week-by-week output is in the
plugin because that is where the simulator lives.

Two things worth knowing:

- **A bot needs no static IP and no inbound ports.** It connects outward to Discord's gateway over a
  WebSocket. A Raspberry Pi on a domestic line is fine; it only has to stay powered.
- It cannot run on the PHP host, which is why it is a separate machine — and why routing it through
  this API rather than the database directly is the right call. One door, one set of rules.
