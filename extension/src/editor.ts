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
