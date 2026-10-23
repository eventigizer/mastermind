# Agent Mastermind

A desk for your Claude Code agents. See which agent, in which project, needs you or has just finished; act on it from
one place; and dispatch handoffs from one project's agent to another's. Mastermind never acts on its own — every
prompt, every allow and every handoff is yours to send.

Mastermind is a small Windows app beside VS Code; this extension starts it and connects every VS Code window to it.

![The Mastermind panel: a card per Claude session, each in its state](media/screens/panel.png)

## What it does

It comes in four tiers — take only the ones you want.

1. **Zero setup.** Install and start Mastermind. The status-bar badge counts the sessions that need you across every
   window, hover for which; the panel lists every session as a card, and a click on a card switches that window's
   Claude sidebar to its session.

   ![The status-bar badge and its tooltip](media/screens/badge.png)

   ![A card click switching the sidebar to its session](media/screens/switch.gif)

2. **Watch a project.** **Mastermind: Watch this project** writes Mastermind's hook entries into the project's
   `.claude/settings.local.json`, after showing you the diff, so its cards carry the live detail — what the agent is
   doing, what it asks. **Mastermind: Stop watching this project** takes exactly those entries out, the same way.
   Nothing is ever written to the global `~/.claude`. The badge's eye is open while this window's projects are watched.

3. **The debug port** (optional). With VS Code's debug port on, the panel can send a prompt into a session, answer its
   permission prompt with allow or deny, and archive it; and VS Code health reads each Claude panel's memory. The
   walkthrough turns it on.

   ![A card waiting on a permission, with allow and deny](media/screens/permission.png)

4. **Handoffs.** One project's agent writes the work down; Mastermind carries it to the other project, and you
   dispatch it to an agent there. **Mastermind: Use handoffs in this project** sets a project up; the relay is the
   Handoffs setting.

Also in the box:

- **VS Code health** shows, in each window's status bar, the memory of its TypeScript, ESLint and Angular servers and
  of its Claude panel once past a warning share, frees each with a click, and restarts a server in its error band on
  its own.
- **The explorer item** opens a new Claude session with a prompt typed in, unsent, on the files you configure.
- **Settings → Agent Mastermind** — the general settings, Handoffs and VS Code health. Mastermind asks before it
  starts, and starts at login only if you say so on that machine.

## Requirements

- Windows 10 or 11 on x64; on Arm, Windows 11, which runs the x64 build under emulation.
- Claude Code's VS Code extension.
- Tested in VS Code and Cursor; other VS Code-based editors should work, untested.

## Security

Everything Mastermind can reach:

- **A listener on `127.0.0.1` only**, for Claude Code's hook events and each window's channel. It refuses a
  browser's hook post and a browser's connection to the channel.
- **The session switch** — the one command it runs in VS Code.
- **The clipboard** — a handoff sent while the debug port is off is put on the clipboard, with a notice, for you to
  paste into its session.
- **Auto-allow** — a session you put on auto-allow has its permission prompts answered yes, by the session's own hook
  even while Mastermind is closed; with New Session Auto Allow on, every session starts with it on.
- **The debug port**, when you turn it on, lets any local process drive VS Code — fine on a machine only you use.

## Updates

An update lands when you click Restart Extensions or restart VS Code. Either ends that window's running Claude
sessions; each resumes from its transcript when you open it.

## Uninstall

Uninstalling takes out Mastermind's hook entries, its login shortcut and the bundled app, and leaves your handoff
folders and skills. While another editor still has the extension installed, it leaves everything.
