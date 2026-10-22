# The debug port

Sending a prompt, allow / deny and archive click inside the Claude sidebar, and VS Code health reads each Claude
panel's memory — both through VS Code's debug port. Without it, the badge, the session switch, every card and the
language servers' memory still work.

In `argv.json` (the button opens it):

```jsonc
"remote-debugging-port": "9222"
```

Then set **Mastermind: Debug Port** to `9222` and quit every VS Code window — a reload is not enough — and reopen. This
step ticks itself once the port answers as VS Code.

Any local process can drive VS Code through this port. That is fine on a machine only you use.
