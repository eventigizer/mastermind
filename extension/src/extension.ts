import * as vscode from 'vscode';
import { Badge } from './badge';
import { Client, Message } from './client';
import { askIfDown, startExe } from './exe';
import { wireHandoffs } from './handoffs';
import { Health, healthEnabled } from './health';
import { wireHooks } from './hooks';
import { editorIdentity } from './editor';
import { updateIfNewer } from './install';
import { fileFolders } from './paths';
import { wireProjects } from './projects';
import { wirePromptItem } from './promptItem';
import { importOnce, wireSettingsSends } from './settings';
import { wireWalkthrough } from './walkthrough';

/** The only command the exe may run here — a `call` naming any other is refused, so a process that answers on the
 * port in Mastermind's place can drive nothing else; its one other reach is a `paste`'s clipboard text. */
const CALLABLE = new Set(['claude-vscode.editor.open']);
const PROXY_VARS = ['HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'no_proxy'];

const folders = (): string[] => fileFolders().map((f) => f.uri.fsPath);

/** The proxy variables of this extension host's environment — the exe warns when a watched project's HTTP entry
 * would go to a proxy. */
function proxyEnv(): Record<string, string> {
  const env: Record<string, string> = {};
  for (const name of PROXY_VARS) {
    const value = process.env[name];
    if (value) env[name] = value;
  }
  return env;
}

/** A call's argument with its {"$undefined": true} marker back to undefined — JSON has none, and an unused position of
 * claude-vscode.editor.open must be undefined, never null. */
const rebuild = (arg: unknown): unknown => (arg !== null && typeof arg === 'object' && (arg as { $undefined?: unknown }).$undefined === true ? undefined : arg);

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  // The import runs before anything can send: a send first would put VS Code's defaults over the config file's flags.
  await importOnce(context);
  const client = new Client(() => ({
    type: 'hello',
    hostPid: process.pid,
    folders: folders(),
    version: String(context.extension.packageJSON.version),
    env: proxyEnv(),
    health: healthEnabled(),
    editor: editorIdentity(context),
  }));
  context.subscriptions.push(client, new Badge(client), new Health(client, context.extension.id));
  wireSettingsSends(context, client);
  client.on('call', (message: Message) => void runCall(client, message));
  client.on('paste', (message: Message) => void runPaste(client, message));
  wireHooks(context, client);
  wireHandoffs(context, client);
  wireProjects(context, client);
  wirePromptItem(context);
  wireWalkthrough(context);
  const showOrStart = (): unknown => (client.isOpen ? client.send({ type: 'show' }) : startExe(context, client));
  context.subscriptions.push(
    vscode.commands.registerCommand('mastermind.start', showOrStart),
    vscode.commands.registerCommand('mastermind.show', showOrStart),
    vscode.workspace.onDidChangeWorkspaceFolders(() => client.send({ type: 'folders', folders: folders() }))
  );
  if (context.extensionMode === vscode.ExtensionMode.Development) wireCheckCall(context);
  client.start();
  void updateIfNewer(context, client);
  void askIfDown(context, client);
}

/** A call message run through CALLABLE: undefined once the command ran, else why it did not. */
async function call(message: Message): Promise<string | undefined> {
  const command = String(message.command);
  const args = Array.isArray(message.args) ? message.args.map(rebuild) : [];
  let error: string | undefined;
  if (!CALLABLE.has(command)) error = `${command} is not a command Mastermind may run`;
  else {
    try {
      await vscode.commands.executeCommand(command, ...args);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }
  return error;
}

async function runCall(client: Client, message: Message): Promise<void> {
  const error = await call(message);
  client.send(error === undefined ? { type: 'result', id: message.id, ok: true } : { type: 'result', id: message.id, ok: false, error });
}

/** A port-off `send`: the sidebar switches to the session with nothing typed — a prefill would replace whatever the
 * person has typed there — and the dispatch text goes to the clipboard with a notice to
 * paste it. The switch runs through CALLABLE like any call; the text reaches only the clipboard, no more than
 * editor.open's own prefill already lets the exe type. A paste naming no session opens nothing. */
async function runPaste(client: Client, message: Message): Promise<void> {
  const none = { $undefined: true };
  const sessionId = typeof message.sessionId === 'string' && message.sessionId ? message.sessionId : undefined;
  let error = sessionId
    ? await call({
        type: 'call',
        command: 'claude-vscode.editor.open',
        args: [sessionId, none, none, none, none, { programmatic: 'honor-preferred-location' }],
      })
    : 'a paste names no session';
  if (error === undefined) {
    try {
      await vscode.env.clipboard.writeText(String(message.text));
      void vscode.window.showInformationMessage(`Mastermind: a handoff for ${String(message.title)} is on the clipboard — paste it there and press Enter`);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }
  client.send(error === undefined ? { type: 'result', id: message.id, ok: true } : { type: 'result', id: message.id, ok: false, error });
}

/** An Extension Development Host only: the platform check drives a call with no exe in the loop — the window's first
 * folder holds `.mastermind/call.json`, a call message, which runs through the exe's own path and answers in
 * `.mastermind/result.json`. A daily install never registers it, so no workspace file there can run a command. */
function wireCheckCall(context: vscode.ExtensionContext): void {
  void vscode.commands.executeCommand('setContext', 'mastermind.dev', true);
  context.subscriptions.push(
    vscode.commands.registerCommand('mastermind.dev.call', async () => {
      const folder = fileFolders()[0];
      if (folder) {
        const dir = vscode.Uri.joinPath(folder.uri, '.mastermind');
        let error: string | undefined;
        try {
          const text = Buffer.from(await vscode.workspace.fs.readFile(vscode.Uri.joinPath(dir, 'call.json'))).toString('utf8');
          error = await call(JSON.parse(text) as Message);
        } catch (e) {
          error = e instanceof Error ? e.message : String(e);
        }
        const result = error === undefined ? { ok: true } : { ok: false, error };
        await vscode.workspace.fs.writeFile(vscode.Uri.joinPath(dir, 'result.json'), Buffer.from(JSON.stringify(result)));
      }
    })
  );
}

export function deactivate(): void {}
