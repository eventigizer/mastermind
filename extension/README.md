# Agent Mastermind

A desk for your Claude Code agents. Mastermind is a small Windows app that watches every Claude Code session on the
machine — which one is working, which one waits on you — and this extension puts it in every VS Code window.

- **The badge** in the status bar counts the sessions that need you, across every window. Hover for which; click for
  Mastermind's panel. Its eye is open when this window's projects are watched, closed when one is not.
- **Mastermind: Watch this project** writes Mastermind's hook entries into the project's `.claude/settings.local.json`,
  after showing you the diff, so its cards carry the live detail. **Mastermind: Stop watching this project** takes
  exactly those entries out, the same way. Nothing is ever written to the global `~/.claude`.
- **Settings → Mastermind** reach the app on their own; a change restarts it — all but VS Code health's, which the
  window keeps. Start at login is per machine.
- **The explorer item** opens a new Claude session with a prompt typed in, unsent, on the files you configure.
- **VS Code health** shows in each window's status bar the memory of its TypeScript, ESLint and Angular servers and
  its Claude panel past a warning share, frees each with a click, and restarts a server in its error band on its own.
- **The walkthrough** starts Mastermind and, if you want the panel's send, allow / deny and archive actions, turns on
  VS Code's debug port.

Mastermind asks before it starts, and starts at login only if you say so on that machine.

**Security.** The app listens on `127.0.0.1` only; it refuses a browser's hook post and a browser's connection to the
window channel, and the only command it can run in VS Code is the session switch. The debug port, when you turn it
on, lets any local process drive VS Code — fine on a machine only you use.
