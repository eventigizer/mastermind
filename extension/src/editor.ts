import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

export type EditorIdentity = { processName: string; userDataDir: string; cli: string };

/**
 * The editor this window runs in, for the exe's hello: the image name its windows run under — the extension host runs
 * as the editor's own executable — its settings folder, read off this extension's log folder (<settings>\logs\<run>\
 * window<N>\exthost\<id>, which a profile never moves, unlike the global storage path), and its command-line tool's
 * full path. A part it cannot find goes empty, and the exe falls back to config's.
 */
export function editorIdentity(context: vscode.ExtensionContext): EditorIdentity {
  return { processName: path.parse(process.execPath).name, userDataDir: settingsFolder(context.logUri.fsPath), cli: cliPath(vscode.env.appRoot) };
}

/** After a new session opens: Cursor 3.x puts it in Claude's side bar view without showing a closed secondary side bar,
 * where VS Code reopens it — so the side bar is focused open whenever Claude Code opens sessions there (its
 * preferredLocation `sidebar`; its default, `panel`, opens an editor tab). A failed reveal leaves the session open. */
export async function revealNewSession(): Promise<void> {
  if (vscode.workspace.getConfiguration('claudeCode').get('preferredLocation') === 'sidebar') {
    try {
      await vscode.commands.executeCommand('workbench.action.focusAuxiliaryBar');
    } catch {
      // the session opened; only its reveal failed
    }
  }
}

function settingsFolder(logPath: string): string {
  const parts = logPath.split(path.sep);
  const at = parts.lastIndexOf('logs');
  return at > 0 ? parts.slice(0, at).join(path.sep) : '';
}

/** The nearest bin\*.cmd at or above the app root: VS Code keeps its tool in <install>\bin, above a per-build
 * <install>\<build>\resources\app (read 2026-10-22); a look-alike may keep it nearer. */
function cliPath(appRoot: string): string {
  let found = '';
  const dirs: string[] = [];
  for (let up = appRoot, i = 0; i < 4; up = path.dirname(up), i++) dirs.push(path.join(up, 'bin'));
  for (const dir of dirs) {
    if (!found) {
      try {
        const cmd = fs.readdirSync(dir).find((f) => f.toLowerCase().endsWith('.cmd'));
        if (cmd) found = path.join(dir, cmd);
      } catch {
        // no such folder
      }
    }
  }
  return found;
}
