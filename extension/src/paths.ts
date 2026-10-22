import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

/** The window's folders on this machine's disk — a remote or virtual folder has no path Mastermind can use. */
export const fileFolders = (): readonly vscode.WorkspaceFolder[] => (vscode.workspace.workspaceFolders ?? []).filter((f) => f.uri.scheme === 'file');

/** Mastermind's app folder — %LOCALAPPDATA%\Mastermind, where the exe keeps config.json and its state. */
export function appDir(): string {
  return path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Mastermind');
}

export const configPath = (): string => path.join(appDir(), 'config.json');

/** The lock the first window to find no Mastermind takes before asking to start it. */
export const askLockPath = (): string => path.join(appDir(), 'ask.lock');
