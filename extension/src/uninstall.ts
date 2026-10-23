// Run by VS Code once Agent Mastermind is fully uninstalled (package.json's vscode:uninstall, at the restart after the
// uninstall), as a plain Node process VS Code kills 5 s in — so it only starts the work. The installed copy, run
// detached with --uninstall, stops every other run of itself, takes Mastermind's hook entries out of every project
// the extension watched, removes the login shortcut and has its own folder deleted once it exits. While another
// editor still has Agent Mastermind installed nothing is touched: the app folder, the watch list and the login
// shortcut are the machine's, and that editor still runs them. No installed copy means Mastermind never ran from this
// extension, so nothing to take out. Handoff folders and skills stay — they hold the user's own work. Nothing here
// may load the vscode module — which paths.ts does, hence the app folder spelled out again.
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const own = path.resolve(__dirname, '..', '..').toLowerCase(); // this editor's extensions folder
const elsewhere = ['.vscode', '.vscode-insiders', '.cursor']
  .map((dir) => path.join(os.homedir(), dir, 'extensions'))
  .some(
    (root) =>
      root.toLowerCase() !== own &&
      fs.existsSync(root) &&
      fs.readdirSync(root).some((name) => name.toLowerCase().startsWith('eventigizer.mastermind-'))
  );
const exe = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Mastermind', 'app', 'Mastermind.exe');

if (!elsewhere && fs.existsSync(exe)) spawn(exe, ['--uninstall'], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
