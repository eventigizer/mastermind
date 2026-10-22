import * as vscode from 'vscode';
import { Client, Message } from './client';
import { fileFolders } from './paths';

const SCHEME = 'mastermind-proposal';

/** The before / after texts of a proposal, served to vscode.diff under the mastermind-proposal scheme. */
class ProposalTexts implements vscode.TextDocumentContentProvider {
  private readonly texts = new Map<string, string>();

  set(uri: vscode.Uri, text: string): void {
    this.texts.set(uri.toString(), text);
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.texts.get(uri.toString()) ?? '';
  }
}

/** The Apply each open proposal waits on, by id — the notification's button and the diff's title-bar button answer
 * the same one, so a proposal applies once, whichever is pressed. */
const waiting = new Map<string, () => void>();

/**
 * "Watch this project" and "Stop watching this project": the exe proposes Mastermind's hook entries written into, or
 * taken out of, the folder's .claude/settings.local.json; the shell shows the diff and applies it only on Apply — the
 * exe writes, and only while the file still reads as the diff's left side. A manifest's projects are refused there.
 * Apply rides a plain notification, never a modal one: a modal blocks the whole window, and the diff behind it could
 * not be scrolled before the answer (user decision). It rides the diff's title bar too: VS Code hides an information
 * notification after about fifteen seconds, buttons or not, so a long read loses it to the bell, while the title-bar
 * button stays as long as the diff is open.
 */
export function wireHooks(context: vscode.ExtensionContext, client: Client): void {
  const texts = new ProposalTexts();
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(SCHEME, texts),
    vscode.commands.registerCommand('mastermind.watch', () => run(client, texts, false)),
    vscode.commands.registerCommand('mastermind.unwatch', () => run(client, texts, true)),
    vscode.commands.registerCommand('mastermind.applyProposal', (uri?: vscode.Uri) => applyFromTitle(uri))
  );
}

/** The diff's title-bar Apply — the proposal id is the first segment of the diff's path. */
function applyFromTitle(uri: vscode.Uri | undefined): void {
  const target = uri ?? vscode.window.activeTextEditor?.document.uri;
  answer(target && target.scheme === SCHEME ? target.path.split('/')[1] : undefined);
}

/** Either button's Apply: the first one applies; a later one — the notification outlives a title-bar Apply, and no API
 * closes it — says so instead of doing nothing. */
function answer(id: string | undefined): void {
  const apply = id ? waiting.get(id) : undefined;
  if (apply) apply();
  else void vscode.window.showWarningMessage('Mastermind: this proposal is answered already — run the command again');
}

async function pickFolder(): Promise<vscode.WorkspaceFolder | undefined> {
  const folders = fileFolders();
  return folders.length > 1 ? vscode.window.showWorkspaceFolderPick() : folders[0];
}

/** A folder command's frame: the window's one folder, or the one picked among several — none warns — and a failure of
 * the work shown as a warning. */
export async function inPickedFolder(work: (folder: vscode.WorkspaceFolder) => Promise<void>): Promise<void> {
  const folder = await pickFolder();
  if (!folder) void vscode.window.showWarningMessage('Mastermind: open a folder first');
  else {
    try {
      await work(folder);
    } catch (error) {
      void vscode.window.showWarningMessage(`Mastermind: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
}

function run(client: Client, texts: ProposalTexts, remove: boolean): Promise<void> {
  return inPickedFolder(async (folder) => {
    const proposal = await client.request({ type: 'hooks.propose', root: folder.uri.fsPath, remove });
    if (proposal.ok !== true) void vscode.window.showWarningMessage(`Mastermind: ${String(proposal.error)}`);
    else await confirm(client, texts, folder, proposal, remove);
  });
}

async function confirm(client: Client, texts: ProposalTexts, folder: vscode.WorkspaceFolder, proposal: Message, remove: boolean): Promise<void> {
  const id = String(proposal.proposalId);
  const before = vscode.Uri.from({ scheme: SCHEME, path: `/${id}/before/settings.local.json` });
  const after = vscode.Uri.from({ scheme: SCHEME, path: `/${id}/after/settings.local.json` });
  texts.set(before, String(proposal.before));
  texts.set(after, String(proposal.after));
  await vscode.commands.executeCommand('vscode.diff', before, after, `${folder.name} · .claude/settings.local.json — ${remove ? 'stop watching' : 'watch'}`);
  const warnings = Array.isArray(proposal.warnings) ? proposal.warnings.map(String) : [];
  const question = remove ? `Take Mastermind's hook entries out of ${folder.name}?` : `Write Mastermind's hook entries into ${folder.name}?`;
  const applied = new Promise<void>((resolve) => waiting.set(id, resolve));
  // A notification carries no detail line, so a warning rides the question's own text.
  const notification =
    warnings.length > 0
      ? vscode.window.showWarningMessage(`${question} ${warnings.join(' ')}`, 'Apply')
      : vscode.window.showInformationMessage(question, 'Apply');
  void notification.then((pick) => {
    if (pick === 'Apply') answer(id);
  });
  await applied;
  waiting.delete(id);
  const result = await client.request({ type: 'hooks.apply', proposalId: id });
  if (result.ok === true) {
    void vscode.window.showInformationMessage(remove ? `Mastermind: ${folder.name} is no longer watched` : `Mastermind: ${folder.name} is watched`);
  } else void vscode.window.showWarningMessage(`Mastermind: ${String(result.error)}`);
}
