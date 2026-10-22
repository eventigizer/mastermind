import * as vscode from 'vscode';
import { Client, Message } from './client';
import { inPickedFolder } from './hooks';

/** PromptRoute's token grammar — the exe checks it again. */
const CODE = /^[A-Z][A-Z0-9]*$/;

/**
 * "Use handoffs in this project" and "Stop using handoffs here": the shell asks, the exe writes (HandoffSkills.cs) —
 * the two skills, the .mastermind/ folders and the project's routing code, asked only when the project has none. The
 * install switches the relay on when it is off, through the mastermind.relay setting — mirrored into config on every
 * connect, so a config write alone would be undone — and says so; the exe writes the same value with the code, so the
 * two cost one restart.
 */
export function wireHandoffs(context: vscode.ExtensionContext, client: Client): void {
  context.subscriptions.push(
    vscode.commands.registerCommand('mastermind.handoffs.use', () => use(client)),
    vscode.commands.registerCommand('mastermind.handoffs.stop', () => stop(client))
  );
}

const notes = (reply: Message): string => (Array.isArray(reply.notes) && reply.notes.length > 0 ? ` ${reply.notes.map(String).join(' ')}` : '');

async function askCode(folder: vscode.WorkspaceFolder): Promise<string | undefined> {
  return vscode.window.showInputBox({
    title: `Mastermind: ${folder.name}'s routing code`,
    prompt: 'The name other projects send handoffs here by — uppercase letters and digits, starting with a letter',
    ignoreFocusOut: true,
    validateInput: (value) => (CODE.test(value) ? undefined : 'Uppercase letters and digits, starting with a letter'),
  });
}

function use(client: Client): Promise<void> {
  return inPickedFolder(async (folder) => {
    const settings = vscode.workspace.getConfiguration('mastermind');
    const relay = settings.get('relay') !== true;
    const root = folder.uri.fsPath;
    let reply: Message | undefined = await client.request({ type: 'handoffs.install', root, relay });
    if (reply.needCode === true) {
      const code = await askCode(folder);
      reply = code ? await client.request({ type: 'handoffs.install', root, code, relay }) : undefined;
    }
    if (reply === undefined) void vscode.window.showInformationMessage('Mastermind: handoffs not turned on — no code given');
    else if (reply.ok !== true) void vscode.window.showWarningMessage(`Mastermind: ${String(reply.error)}`);
    else {
      // The setting follows what the exe wrote: a hand-made `handoffs: null` in config keeps the relay off.
      const switched = reply.relay === true;
      if (switched) await settings.update('relay', true, vscode.ConfigurationTarget.Global);
      const relayText = switched ? ' The relay is switched on.' : relay ? ' The relay stays off — config.json holds handoffs: null, a reset made by hand.' : '';
      void vscode.window.showInformationMessage(`Mastermind: ${folder.name} uses handoffs.${relayText}${notes(reply)}`);
    }
  });
}

function stop(client: Client): Promise<void> {
  return inPickedFolder(async (folder) => {
    const reply = await client.request({ type: 'handoffs.uninstall', root: folder.uri.fsPath });
    if (reply.ok !== true) void vscode.window.showWarningMessage(`Mastermind: ${String(reply.error)}`);
    else void vscode.window.showInformationMessage(`Mastermind: ${folder.name} no longer uses handoffs.${notes(reply)}`);
  });
}
