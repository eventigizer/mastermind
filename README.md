# Agent Mastermind

A desk for your Claude Code agents. Mastermind shows which agent, in which project, needs you or has just finished,
and lets you act from one place: jump straight to that session, send it a prompt, allow or deny what it asks. It also
carries handoffs between your projects — one project's agent writes the work down, and you dispatch it to an agent in
the other. It runs as a small Windows app beside VS Code, and a VS Code extension connects each window to it.

## Install

From the Visual Studio Marketplace or Open VSX — the links land here with the first release.

## Build from source

You need the .NET SDK 10 or newer, and Node.js 22 for the extension.

- **The app** — `dotnet build app/Mastermind.csproj`. `app/run.bat` builds and runs it from source; `app/publish.bat`
  builds the self-contained `Mastermind.exe` and copies it to the repository root.
- **The extension** — in `extension/`: `npm ci`, `npm run build`, then `node smoke.js`, which prints PASS.

## Layout

- `app/` — the Mastermind app (C#, WPF): the hook listener, the panel and tray, and the handoff relay.
- `extension/` — the VS Code extension: starts the app, carries its settings in VS Code's Settings, shows the
  status-bar badge, and connects each window to the app.
- `handoff_contract.md` — the contract every part of the handoff system keeps.

## License

MIT — see [LICENSE](LICENSE).

## Sponsor

Mastermind is free. If it saves you time, you can [sponsor it on GitHub](https://github.com/sponsors/eventigizer).
