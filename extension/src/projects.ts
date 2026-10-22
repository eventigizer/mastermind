import * as crypto from 'crypto';
import * as vscode from 'vscode';
import { Client, Message } from './client';

type Row = { root: string; name: string; token: string; label: string; colour: string; hide: boolean; manifest: boolean };

let panel: vscode.WebviewPanel | undefined;
let loaded: Row[] = [];

/**
 * "Mastermind: Projects": one editor tab listing every project Mastermind knows — a row each with its name on
 * the chip, routing code, chip colour and hide, dragged to reorder, and a ✕ that takes it off the list (else a
 * row stays in config for good). A manifest's project shows its place and code locked. The exe reads and writes (ProjectsPage.cs); Save asks first when a code changed, since a handoff already
 * addressed to the old code reads UNKNOWN DEST, and Mastermind restarts to take the list in.
 */
export function wireProjects(context: vscode.ExtensionContext, client: Client): void {
  context.subscriptions.push(vscode.commands.registerCommand('mastermind.projects', () => open(client)));
}

const failed = (error: unknown): void => void vscode.window.showWarningMessage(`Mastermind: ${error instanceof Error ? error.message : String(error)}`);

async function open(client: Client): Promise<void> {
  let reply: Message | undefined;
  try {
    reply = await client.request({ type: 'projects.get' });
  } catch (error) {
    failed(error);
  }
  if (reply !== undefined) {
    loaded = Array.isArray(reply.projects) ? (reply.projects as Row[]) : [];
    if (panel) panel.reveal();
    else {
      // Kept alive while hidden: a hidden webview is rebuilt from its html on return, losing unsaved edits.
      panel = vscode.window.createWebviewPanel('mastermind.projects', 'Mastermind: Projects', vscode.ViewColumn.Active, {
        enableScripts: true,
        retainContextWhenHidden: true,
      });
      panel.onDidDispose(() => (panel = undefined));
      panel.webview.onDidReceiveMessage((m: { type: string; rows: Row[] }) => {
        if (m.type === 'save') void save(client, m.rows);
      });
    }
    panel.webview.html = page(loaded);
  }
}

async function save(client: Client, rows: Row[]): Promise<void> {
  const was = new Map(loaded.map((r) => [r.root, r.token]));
  const moved = rows.filter((r) => !r.manifest && (was.get(r.root) ?? '') !== '' && was.get(r.root) !== r.token);
  let go = true;
  if (moved.length > 0) {
    const list = moved.map((r) => `${r.name}: ${was.get(r.root)} → ${r.token || 'none'}`).join(', ');
    const pick = await vscode.window.showWarningMessage(
      `Handoffs already addressed to an old code will read UNKNOWN DEST — ${list}. Save?`,
      { modal: true },
      'Save'
    );
    go = pick === 'Save';
  }
  if (go) {
    try {
      const reply = await client.request({ type: 'projects.save', projects: rows });
      if (reply.ok !== true) void vscode.window.showWarningMessage(`Mastermind: ${String(reply.error)}`);
      else {
        void vscode.window.showInformationMessage(
          reply.restart === true ? 'Mastermind: projects saved — Mastermind restarts to use them' : 'Mastermind: nothing changed'
        );
        panel?.dispose();
      }
    } catch (error) {
      failed(error);
    }
  }
}

function page(rows: Row[]): string {
  const nonce = crypto.randomBytes(16).toString('base64');
  const data = JSON.stringify(rows).replace(/</g, '\\u003c');
  return `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 12px 20px; }
  table { border-collapse: collapse; width: 100%; max-width: 960px; }
  th { text-align: left; font-weight: 600; color: var(--vscode-descriptionForeground); padding: 4px 8px; }
  td { padding: 4px 8px; border-top: 1px solid var(--vscode-panel-border); }
  tr[draggable="true"] td.grip { cursor: grab; }
  tr.locked td.grip { color: var(--vscode-disabledForeground); }
  input[type=text] { width: 100%; box-sizing: border-box; background: var(--vscode-input-background); color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent); padding: 3px 6px; }
  input:disabled { opacity: .6; }
  .hint { color: var(--vscode-descriptionForeground); margin: 4px 0 14px; }
  button { background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: none; padding: 6px 14px;
    margin-top: 14px; cursor: pointer; }
  button.remove { background: none; color: var(--vscode-descriptionForeground); margin: 0; padding: 0 4px; }
</style></head>
<body>
<h2>Projects</h2>
<p class="hint">Drag a row to reorder. A project the manifest sets keeps its place and code. An empty colour takes the palette's. &#x2715; takes a project off the list — a folder still open comes back unconfigured.</p>
<table><thead><tr><th></th><th>Project</th><th>Name on the chip</th><th>Code</th><th>Colour</th><th>Hide</th><th></th></tr></thead>
<tbody id="rows"></tbody></table>
<button id="save">Save</button>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
const rows = ${data};
const body = document.getElementById('rows');
let dragged = -1;
function render() {
  body.innerHTML = '';
  rows.forEach((r, i) => {
    const tr = document.createElement('tr');
    tr.className = r.manifest ? 'locked' : '';
    tr.draggable = !r.manifest;
    tr.innerHTML = '<td class="grip"></td><td></td><td><input type="text" data-k="label"></td><td><input type="text" data-k="token"></td>' +
      '<td><input type="color" data-k="pick"> <input type="text" data-k="colour" size="8"></td><td><input type="checkbox" data-k="hide"></td>' +
      '<td><button type="button" class="remove" title="Take this project off the list">&#x2715;</button></td>';
    tr.children[0].textContent = r.manifest ? '\\u{1F512}' : '\\u2807';
    tr.children[1].textContent = r.name;
    tr.children[1].title = r.root + (r.manifest ? ' — set by the manifest' : '');
    const q = (k) => tr.querySelector('[data-k="' + k + '"]');
    q('label').value = r.label;
    q('label').placeholder = r.token || r.name;
    q('token').value = r.token;
    q('token').disabled = r.manifest;
    q('colour').value = r.colour;
    q('colour').placeholder = 'palette';
    q('pick').value = /^#[0-9a-fA-F]{6}$/.test(r.colour) ? r.colour : '#808080';
    q('hide').checked = r.hide;
    q('label').oninput = (e) => { r.label = e.target.value; };
    q('token').oninput = (e) => { e.target.value = e.target.value.toUpperCase(); r.token = e.target.value; };
    q('colour').oninput = (e) => { r.colour = e.target.value; };
    q('pick').oninput = (e) => { r.colour = e.target.value; q('colour').value = r.colour; };
    q('hide').onchange = (e) => { r.hide = e.target.checked; };
    const remove = tr.querySelector('.remove');
    if (r.manifest) remove.remove();
    else remove.onclick = () => { rows.splice(i, 1); render(); };
    tr.ondragstart = () => { dragged = i; };
    tr.ondragover = (e) => { if (!r.manifest && dragged >= 0) e.preventDefault(); };
    tr.ondrop = (e) => {
      e.preventDefault();
      if (dragged >= 0 && dragged !== i) { const [m] = rows.splice(dragged, 1); rows.splice(i, 0, m); }
      dragged = -1;
      render();
    };
    body.appendChild(tr);
  });
}
document.getElementById('save').onclick = () => vscode.postMessage({ type: 'save', rows });
render();
</script></body></html>`;
}
