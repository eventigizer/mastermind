import * as path from 'path';
import * as vscode from 'vscode';
import { Client, Message } from './client';

type Need = { title: string; project: string; status: string; state: 'permission' | 'waiting' | 'finished' };
type Folder = { root: string; state: 'manifest' | 'watched' | 'off' };

const FOLDER_STATE: Record<Folder['state'], string> = {
  manifest: 'watched by the manifest',
  watched: 'watched',
  off: 'not watched — run **Mastermind: Watch this project**',
};

export const esc = (text: unknown): string => String(text ?? '').replace(/[\\`*_{}[\]()#+\-.!|<>]/g, '\\$&');

/**
 * The status-bar badge: the who-needs-you count across every window, an open eye when every folder of this window is
 * watched and a closed one when any is not, the sessions and the folders' states on hover; a click opens Mastermind's
 * panel. The warning colour only while a session waits on a permission — a FINISHED card counts until its next prompt,
 * so a colour on any count would never go out. While no Mastermind answers it reads "Mastermind off", and a click starts it
 * with no question.
 */
export class Badge implements vscode.Disposable {
  private readonly item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  private needs: Need[] = [];
  private folders: Folder[] = [];
  private open = false;

  constructor(client: Client) {
    client.on('needs', (m: Message) => {
      this.needs = Array.isArray(m.items) ? (m.items as Need[]) : [];
      this.render();
    });
    client.on('watched', (m: Message) => {
      this.folders = Array.isArray(m.folders) ? (m.folders as Folder[]) : [];
      this.render();
    });
    client.onState((open) => {
      this.open = open;
      if (!open) {
        this.needs = [];
        this.folders = [];
      }
      this.render();
    });
    this.render();
    this.item.show();
  }

  private render(): void {
    if (!this.open) {
      this.item.text = '$(circle-slash) Mastermind off';
      this.item.tooltip = "Mastermind isn't running — click to start it";
      this.item.command = 'mastermind.start';
      this.item.backgroundColor = undefined;
    } else {
      const eye = this.folders.some((f) => f.state === 'off') ? '$(eye-closed)' : '$(eye)';
      this.item.text = this.needs.length > 0 ? `${eye} ${this.needs.length}` : eye;
      const tip = new vscode.MarkdownString();
      tip.appendMarkdown(this.needs.length > 0 ? `**${this.needs.length} need you**\n\n` : 'Nobody needs you\n\n');
      this.needs.forEach((n) => tip.appendMarkdown(`- ${esc(n.title)} — ${esc(n.project)} · ${esc(n.status)}\n`));
      if (this.folders.length > 0) tip.appendMarkdown('\n');
      this.folders.forEach((f) => tip.appendMarkdown(`- \`${path.basename(f.root)}\`: ${FOLDER_STATE[f.state] ?? f.state}\n`));
      tip.appendMarkdown('\n[Projects…](command:mastermind.projects)');
      tip.isTrusted = { enabledCommands: ['mastermind.projects'] };
      this.item.tooltip = tip;
      this.item.command = 'mastermind.show';
      this.item.backgroundColor = this.needs.some((n) => n.state === 'permission') ? new vscode.ThemeColor('statusBarItem.warningBackground') : undefined;
    }
  }

  dispose(): void {
    this.item.dispose();
  }
}
