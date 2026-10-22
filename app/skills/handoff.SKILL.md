---
name: mastermind-handoff
description: Write a handoff to another project — a task, question or finding for that project's agent — and send it through Mastermind's relay.
---

This is Mastermind's handoff writer, for {{CODE}}.

If `.mastermind/writer.md` exists in this project, read it now and follow it too — it holds this project's own
extra steps. If no task came with the request, ask what to send and stop.

A handoff is one Markdown file that another project's agent reads, acts on and deletes. Write it so that agent needs
nothing else: what to do, why, what you already know, and what to send back, if anything.

## Where it can go

This project's code is **{{CODE}}**. The projects you can send to:

{{BOOK}}

Send only to a code in this list. A project that is not listed does not use handoffs — say so and stop.

## Writing it

1. Name the file `{{CODE}}to<DST>__<scope>__<topic>.md`: `<DST>` a code from the list, `<scope>` and `<topic>` short
   lowercase words joined by `_` — for example `{{CODE}}toFE__checkout__price_rounding.md`.
2. Write it in `{{STAGING}}/` first, never straight into the outbox: Mastermind takes whatever lands in the outbox,
   so a file written there in pieces can leave half-written.
3. When it is finished, make `{{OUTBOX}}/` if it is missing, and if a file of that name is already there, pick
   another topic — never overwrite one. Then move it in with a move that refuses to overwrite: `mv -n` in a POSIX
   shell, `Move-Item -LiteralPath` without `-Force` in PowerShell. It is sent only once it is gone from
   `{{STAGING}}/` — check there, never the move's exit status: coreutils releases disagree on whether `mv -n` fails
   when it skips a taken name.
4. Never edit it after the move. A change is a new handoff.

Mastermind carries it to the other project and hands it to an agent there. Never read or write inside another
project yourself.
