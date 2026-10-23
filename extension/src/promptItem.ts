import * as vscode from 'vscode';
import { revealNewSession } from './editor';
import { fileFolders } from './paths';

type PromptItem = { include?: string; exclude?: string; template?: string };

const CONTEXT_KEY = 'mastermind.promptFiles';
const REFRESH_DEBOUNCE_MS = 500;

const item = (): PromptItem => vscode.workspace.getConfiguration('mastermind').get<PromptItem>('promptItem') ?? {};

/** The include split at its first wildcard segment: the fixed folders in front become the search's base, so
 * `docs/*.md` reads one folder per workspace folder instead of walking the tree, node_modules included. */
function scoped(folder: vscode.WorkspaceFolder, include: string): vscode.RelativePattern {
  const parts = include.split('/');
  const fixed: string[] = [];
  while (parts.length > 1 && !/[*?[\]{}]/.test(parts[0])) fixed.push(parts.shift() as string);
  return new vscode.RelativePattern(vscode.Uri.joinPath(folder.uri, ...fixed), parts.join('/'));
}

/**
 * The explorer's right-click item: on a file matching mastermind.promptItem's include, minus its exclude, a new Claude
 * session opens in the preferred location with the template typed in, unsent — {path} is the workspace-relative path.
 * A menu's `when` cannot read a configured glob, so the matching files go into a context key the `when` tests with
 * `resourcePath in`, kept current by a file watcher per workspace folder. The exclude is matched from the include's
 * fixed folder, where the search runs. A workspace folder without that fixed folder adds no files — its search starts
 * ripgrep in a missing folder and rejects — and never empties the key for the others.
 */
export function wirePromptItem(context: vscode.ExtensionContext): void {
  let watchers: vscode.FileSystemWatcher[] = [];
  let timer: NodeJS.Timeout | undefined;
  const refresh = async (): Promise<void> => {
    const { include, exclude, template } = item();
    let files: string[] = [];
    if (include && template) {
      for (const folder of fileFolders()) {
        try {
          const found = await vscode.workspace.findFiles(scoped(folder, include), exclude || undefined);
          files = files.concat(found.map((uri) => uri.fsPath));
        } catch {
          // no include folder in this workspace folder — nothing of it to offer
        }
      }
    }
    await vscode.commands.executeCommand('setContext', CONTEXT_KEY, files);
  };
  const soon = (): void => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => void refresh(), REFRESH_DEBOUNCE_MS);
  };
  const rewatch = (): void => {
    watchers.forEach((w) => w.dispose());
    const { include } = item();
    watchers = include ? fileFolders().map((f) => vscode.workspace.createFileSystemWatcher(scoped(f, include), false, true, false)) : [];
    watchers.forEach((w) => {
      w.onDidCreate(soon);
      w.onDidDelete(soon);
    });
  };
  rewatch();
  void refresh();
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('mastermind.promptItem')) {
        rewatch();
        soon();
      }
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      rewatch();
      soon();
    }),
    vscode.commands.registerCommand('mastermind.promptItem', (uri?: vscode.Uri) => open(uri)),
    {
      dispose: () => {
        watchers.forEach((w) => w.dispose());
        if (timer) clearTimeout(timer);
      },
    }
  );
}

function open(uri: vscode.Uri | undefined): void {
  const { template } = item();
  if (uri && uri.scheme === 'file' && template) {
    const prompt = template.split('{path}').join(vscode.workspace.asRelativePath(uri, false));
    // claude-vscode.editor.open is Claude Code's internal command, positional: session id, prompt, three unused,
    // options. An unused position takes undefined, and without programmatic 'honor-preferred-location' the call
    // rewrites the user's claudeCode.preferredLocation.
    vscode.commands
      .executeCommand('claude-vscode.editor.open', undefined, prompt, undefined, undefined, undefined, { programmatic: 'honor-preferred-location' })
      .then(
        () => revealNewSession(),
        (error: unknown) =>
          vscode.window.setStatusBarMessage(`$(error) Mastermind: the session did not open — ${error instanceof Error ? error.message : String(error)}`, 15000)
      );
  }
}
