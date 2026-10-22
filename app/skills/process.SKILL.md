---
name: mastermind-process
description: Work a handoff another project sent here — read the file it names, do what it asks, then delete it.
---

This is Mastermind's handoff processor, for {{CODE}}.

If `.mastermind/processor.md` exists in this project, read it now and follow it too — it holds this project's own
extra steps.

You were given the path of a handoff file under `{{PROCESSING}}/` — for example
`{{PROCESSING}}/FEto{{CODE}}__checkout__price_rounding.md`. If no path came with the request, say so and stop.

1. Read the whole file. Its name says who sent it: the code before `to`.
2. Do what it asks, in this project only — never read or write inside another project.
3. If it asks for an answer, send one back with /mastermind-handoff, addressed to the sender's code.
4. When you are done, delete the file: that is how Mastermind knows it was handled. Leave it only when you stop
   before finishing, and say so.
