# Handoffs

A handoff is work that one project's agent writes for another project's agent — a task, a question or a finding —
in one Markdown file. Mastermind carries it from the sending project to the receiving one, you hand it to an agent
there, and that agent does the work and deletes the file. This is the contract every part of the loop keeps: the
handoff skills, Mastermind's relay, and anything else you build to write or work handoffs.

## Turning handoffs on

Run **Mastermind: Use handoffs in this project** in a VS Code window on the project. It writes the handoff skills
into the project's `.claude/skills/` — `/mastermind-handoff`, which writes a handoff, and `/mastermind-process`,
which works one — makes the folders below, gives the project its routing code, asking for one when it has none,
and switches the relay on when it is off. Mastermind restarts to take a new code in.

- The skill folders are Mastermind's: each start rewrites them, so a hand edit there is lost. A project's own extra
  steps go in an add-on file instead — `.mastermind/writer.md` for the writer, `.mastermind/processor.md` for the
  processor — which each skill reads and follows when it is there.
- A session already open in the project sees the skills at once when the project had a `.claude/skills/` folder
  when the session started; otherwise only after `/reload-skills` or a restart, and the command's note says which.
  A session that does not see `/mastermind-process` yet cannot run a dispatch.
- Turn handoffs on at one level of a repository only: a session loads the skills of its own folder and of every
  folder above it up to the repository's root, and those of a folder below once it works on files there. Two
  skills of one name both load, and a bare `/mastermind-handoff` can run the other project's copy — which writes
  under that project's code.
- The code, the install and the skills belong to this machine. On another machine, run the command there too, with
  the same code — until then Mastermind there does not see the project's handoffs, and a different code leaves every
  handoff addressed to the old one undeliverable.

**Mastermind: Stop using handoffs here** takes the code and the skills out, sends `staging/` to the recycle bin
where Windows can, drafts included, removes `.claude/skills/` and `.claude/` when nothing else is left in them, and restarts Mastermind
when it took a code out.
Handoffs still waiting stay on disk and it names them — but Mastermind lists a project's handoffs only while its
config holds an entry for the project, which the undo removes when the code was all it held, and handoffs other
projects send to that code read `UNKNOWN DEST`. Your own files in `.mastermind/`, the add-ons included, stay, and so
does the relay, which serves every project.

## Routing codes

Every project that uses handoffs has a routing code: an uppercase letter, then uppercase letters or digits — `FE`,
`API`, `APP2`. The install keeps it in Mastermind's config, `%LOCALAPPDATA%\Mastermind\config.json`, as the `token`
of the project's entry in `projects`. A code names exactly one project: the install refuses one another project
holds, and a code two projects are given by hand routes nowhere until one gives it up. The writer skill carries
every other project's code and folder name, refreshed at each Mastermind start, so an agent addresses a handoff
without looking outside its own project.

## The filename is the address

```
{SRC}to{DST}__{scope}__{topic}.md
```

- `{SRC}` is the sender's code and `{DST}` the receiver's; the `to` between them is lowercase, so neither code can
  swallow it.
- `__` ends the address. The rest is free — by convention a scope and a topic, lowercase words joined by `_`:
  `FEtoAPI__checkout__price_rounding.md`.
- Only `{DST}` routes. A handoff whose `{DST}` no project claims is never delivered: it stays where it is, marked
  `UNKNOWN DEST`. `{SRC}` is where an answer goes.
- Only a file whose name fits the grammar is a handoff: Mastermind lists and moves no other file, and no folder,
  whatever its name.
- The name never changes once the file is finished: the dispatch text names it, and the topic is what tells two
  handoffs on one route apart.

## The folders

Each project's handoff folders sit under `.mastermind/` at its root.

| Folder | Holds | Moved on by |
|---|---|---|
| `staging/` | handoffs being written | the writer, into the outbox when finished |
| `outbox/` | finished handoffs on their way out | Mastermind, into the receiver's inbox |
| `inbox/` | delivered handoffs waiting for an agent | you, dispatching each to an agent |
| `processing/` | handoffs an agent is working | that agent, which deletes each when done |

- `outbox/`, `inbox/` and `processing/` hold pending work and are committed with the project. A delivery deletes a
  file in one repository and adds it in another, so commit both sides together; a branch without the file hides it
  until you switch back. A handoff becomes part of the receiving repository's history — write it as you would a
  commit there.
- `staging/` is throwaway. `.mastermind/.gitignore` keeps it out of git, nothing watches it, and each Mastermind
  start deletes whatever sits at its top level last written more than three days ago — a file copied or moved in
  keeps its old time — in each project on a fixed drive, to the recycle bin where Windows can.
- Config's `handoffs.outbox`, `handoffs.inbox` and `handoffs.processing` move those folders in every project, each
  relative to the project's root. `staging/` never moves: the start-time clean deletes what sits there.

## Writing a handoff

1. Write it in `staging/` under its final name — never straight into the outbox, which Mastermind takes from as
   soon as a file holds still, so a file written there in pieces can leave half-written.
2. Make `outbox/` if it is missing, then move the file in with a move that refuses to overwrite — `mv -n` in a
   POSIX shell, `Move-Item -LiteralPath` without `-Force` in PowerShell. A name already in the outbox means the
   topic is taken: pick another, never overwrite.
3. It is sent only once it is gone from `staging/` — check there, never the move's exit status: coreutils
   releases disagree on whether `mv -n` fails when it skips a taken name.
4. Never edit it after the move. A change, or an answer, is a new handoff.

## Delivery

While the relay is on — **Mastermind: Relay** in VS Code's settings — Mastermind scans the outbox, inbox and
processing folders of every project in its config, and lists each handoff on its **INCOMING PROMPTS** card.

- A file in an outbox is delivered once its size holds from one scan to the next: Mastermind moves it into the
  receiving project's inbox under the same name, never overwriting. A name already waiting there is refused, and so
  is a receiving project whose folder is gone — the handoff stays in the outbox, and Mastermind says so.
- The card's auto-move box, on at each start, delivers each handoff once per Mastermind run. To retry a refused
  one, untick auto-move and use the row's `move ▸`, or restart Mastermind.
- Delivery is a rename, which needs both projects on one volume. Across volumes the file is copied and then
  deleted: the receiving inbox can show a partial copy for a moment, and a file something holds open — a sync
  client, an editor — stays in the outbox once copied and can be delivered again on a later run.

## Dispatch

A delivered handoff waits in the inbox until you hand it to an agent from its row on the card: `new` starts a fresh
session in the receiving project, `send ▾` hands it to one of its idle sessions. Dispatch moves the file into
`processing/` and gives the session the dispatch text, config's `handoffs.dispatchText`:

```
/mastermind-process {path}
```

`{path}` becomes the file's path from the project root, with forward slashes —
`.mastermind/processing/FEtoAPI__checkout__price_rounding.md`. The text works alone or after words of your own:
Claude Code runs a skill named at the start of a message, and takes one named later as leave to run it.

- With the debug port on — VS Code started with a `remote-debugging-port`, and **Mastermind: Debug Port** in VS
  Code's settings naming it — Mastermind sends the text itself; `send ▾` adds it after any draft already in that
  session's message box, and the draft goes with it. A dispatch that reaches no session goes back to the inbox.
- With it off — the default — `new` opens the session in the one VS Code window whose first folder is the
  receiving project, with the text typed in, and `send ▾` switches to the session and puts the text on the
  clipboard: paste it, then press Enter. No such window, or a window that refuses, puts the file back in the inbox
  with a warning. A handoff handed over and not yet sent reads `TYPED` until a message naming it shows in one of
  the project's running sessions, which then holds it.
- With **Mastermind: New Dispatch Auto Allow** on, a `new` session starts with auto-allow on, and in a project
  Mastermind watches — **Mastermind: Watch this project** — the agent working the handoff runs every tool without
  asking you. Auto-allow answers through the hooks Watch adds, so elsewhere the agent asks as usual.

## Working a handoff

The receiving agent reads the file at `{path}`, does what it asks in its own project, and deletes it — the delete
is how Mastermind knows it was handled. An answer goes back as a new handoff, addressed to the sender's code.

No agent reads or writes inside another project. Mastermind alone moves handoffs between them.

## The card

| Row | Where | Means | You can |
|---|---|---|---|
| `TO SEND` | outbox | finished, not delivered yet | `move ▸`, while auto-move is off |
| `WAITING` | inbox | delivered; its project is open in VS Code | `new`, `send ▾` |
| `PARKED` | inbox | delivered; its project is not open in VS Code | wait — `new` and `send ▾` turn on with the window |
| `TYPED` | processing | handed over with the debug port off, not sent yet | `back to inbox` |
| `PROCESSING` | processing | an agent has it | — |
| `ORPHAN` | processing | the session it was handed to ended, or Mastermind has no record of one | `back to inbox` when there is no record |
| `UNKNOWN DEST` | any | its `{DST}` names no project | — |

`delete` shows on every row no agent holds — all but `TYPED` and `PROCESSING` — and sends the handoff to the
recycle bin where Windows can; on a network or removable drive, or past the bin's size, it is gone for good. A
`new` whose session Mastermind never identified stays `PROCESSING`; take its file out of `processing/` by hand once
you know nobody is working it.

## Changing the config

Mastermind reads its config once per run, so an edit to a code or a `handoffs` key takes a restart. Turn the relay
on and off with **Mastermind: Relay** in VS Code's settings: each window copies it into config's
`handoffs.enabled` when it connects, so a hand edit of that key is undone.
