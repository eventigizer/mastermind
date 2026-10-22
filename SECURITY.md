# Security

Report a vulnerability privately: this repository's **Security** tab → **Report a vulnerability**. Please do not open a
public issue for one.

What counts: Mastermind listens on `127.0.0.1` only — the hook listener and the window channel on one port — and it can
answer a Claude Code permission prompt for a session you set to auto-allow. A way for another program, a web page or
another user of the machine to reach either, or to turn auto-allow on, is a vulnerability. VS Code's debug port, when
you turn it on, lets any local process drive VS Code by design; that is documented, not a vulnerability.

Auto-allow answers for the Claude process a session's toggle was turned on for — while Mastermind is closed too,
through its own `Mastermind.exe --hook` entry in a watched project's settings. Turn it off on the session's card, or
stop watching the project.

Reports are answered as soon as possible; this is a project maintained in spare time.
