import * as path from 'path';
import * as vscode from 'vscode';
import { esc } from './badge';
import { Client, Message } from './client';

type Kind = 'typescript' | 'eslint' | 'angular';
type Item = Kind | 'panel';
type Level = 'ok' | 'warning' | 'error';
// limitMb: the heap each can reach, the exe's call — a server's own flag within VS Code's Node cage, else the default
// the exe measured on this PC, the panel's too.
type Server = { kind: Kind; pid: number; mb: number; limitMb: number };
// sharedWith: the other windows whose Claude panels share this reading's V8 isolate — the heap is the isolate's, so
// this window's reload frees only its own share.
type Panel = { state: 'off' | 'none' | 'ok' | 'stale' | 'error'; mb: number; limitMb: number; error: string | null; sharedWith?: string[] };
type Reading = { servers: Server[]; panel: Panel; busy: boolean; why: string | null; error: string | null };
type Busy = { busy: boolean; why: string; error?: string };
type Row = { item: Item; mb: number; ceilingMb: number; share: number; level: Level };

const LABEL: Record<Item, string> = { typescript: 'TypeScript', eslint: 'ESLint', angular: 'Angular', panel: 'Claude panel' };
const RESTART: Record<Kind, string> = { typescript: 'typescript.restartTsServer', eslint: 'eslint.restart', angular: 'angular.restartNgServer' };
const RESTART_WAIT_MS = 2 * 60000;
// The exe sends a reading only when it changes, so a steady one in the error band arrives once — the quiet wait, the
// cooldown and a restart's two-minute limit are judged again on this clock.
const TICK_MS = 5000;
const RELOAD_WEBVIEWS = 'workbench.action.webview.reloadWebviewAction';
const RELOAD_WINDOW = 'workbench.action.reloadWindow';
const LINKED = ['mastermind.health.restart', 'mastermind.health.reloadWebviews', 'mastermind.health.reloadWindow', 'mastermind.health.debugPortStep'];
const RANK: Record<Level, number> = { ok: 0, warning: 1, error: 2 };
// A modal dialog's only button: with isCloseAffordance set it replaces VS Code's own Cancel, so the dialog reads OK alone.
const OK: vscode.MessageItem = { title: 'OK', isCloseAffordance: true };

const settings = (): vscode.WorkspaceConfiguration => vscode.workspace.getConfiguration('mastermind.health');
export const healthEnabled = (): boolean => settings().get<boolean>('enabled', true);
const gb = (mb: number): string => (mb / 1024).toFixed(1);
const link = (command: string, args?: unknown[]): string => `command:${command}${args ? `?${encodeURIComponent(JSON.stringify(args))}` : ''}`;
const message = (e: unknown): string => (e instanceof Error ? e.message : String(e));

function parse(m: Message): Reading | undefined {
  let reading: Reading | undefined;
  if (Array.isArray(m.servers) && m.panel !== null && typeof m.panel === 'object') {
    reading = {
      servers: m.servers as Server[],
      panel: m.panel as Panel,
      busy: m.busy === true,
      why: typeof m.why === 'string' ? m.why : null,
      error: typeof m.error === 'string' ? m.error : null,
    };
  }
  return reading;
}

/**
 * VS Code health's status-bar item (phase 3b): the exe reads this window's language servers and Claude panel; this item
 * judges each against its own ceiling with the window's limits — shares of that ceiling, kept here and never mirrored,
 * so a change restarts nothing — and judges the reading it holds again every 5 s, since the exe sends one only on a
 * change. It shows the worst past its warning share, restarts a server in its error band once no
 * file has been opened, changed or closed for the quiet wait (at most once per cooldown per server), and reports its
 * level to the exe for the tray. The panel's reloads restart every Claude session of the window, so they run on a
 * click only; they and every automatic restart wait on the exe's fresh word that each session there is idle — a
 * restarted server leaves an agent's getDiagnostics empty until it reloads. A click that is refused or fails answers in a modal
 * dialog with OK; an automatic restart answers in a 10 s status-bar message.
 */
export class Health implements vscode.Disposable {
  // Its own id, apart from the badge's: VS Code keys an item's hidden state and its context-menu entry on it.
  private readonly item = vscode.window.createStatusBarItem('mastermind.health', vscode.StatusBarAlignment.Right, 99);
  private readonly disposables: vscode.Disposable[] = [];
  private reading: Reading | undefined;
  private open = false;
  private lastActivity = Date.now();
  private readonly lastAuto = new Map<Kind, number>();
  private readonly restarting = new Map<Kind, { pid: number; mb: number; at: number; automatic: boolean }>();
  private readonly failures = new Map<Kind, { text: string; pid: number }>(); // automatic restarts that failed, on the item
  private checking = false; // an automatic restart's busy check is out — the next tick waits for it
  private reported = 'ok|'; // the exe starts each window at ok, so nothing goes out until the level moves

  constructor(
    private readonly client: Client,
    extensionId: string
  ) {
    this.item.name = 'Mastermind VS Code health';
    client.on('health', (m: Message) => {
      this.reading = parse(m);
      this.tick();
    });
    const timer = setInterval(() => {
      if (this.reading) this.tick();
    }, TICK_MS);
    client.onState((open) => {
      this.open = open;
      if (!open) this.reading = undefined;
      this.reported = 'ok|'; // a reconnect is a fresh client on the exe's side, at ok
      this.render();
    });
    // Only real files count: Claude's diff documents (_claude_fs_*) and output channels change on their own.
    const touch = (document: vscode.TextDocument): void => {
      if (document.uri.scheme === 'file' || document.uri.scheme === 'untitled') this.lastActivity = Date.now();
    };
    this.disposables.push(
      this.item,
      { dispose: () => clearInterval(timer) },
      vscode.workspace.onDidOpenTextDocument(touch),
      vscode.workspace.onDidCloseTextDocument(touch),
      vscode.workspace.onDidChangeTextDocument((event) => touch(event.document)),
      vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration('mastermind.health.enabled')) client.send({ type: 'health.switch', on: healthEnabled() });
        if (event.affectsConfiguration('mastermind.health') || event.affectsConfiguration('mastermind.health.enabled')) this.render();
      }),
      vscode.commands.registerCommand('mastermind.health.free', () => this.free()),
      vscode.commands.registerCommand('mastermind.health.restart', (kind: Kind) => this.restart(kind, false)),
      vscode.commands.registerCommand('mastermind.health.reloadWebviews', () => this.reload(RELOAD_WEBVIEWS)),
      vscode.commands.registerCommand('mastermind.health.reloadWindow', () => this.reload(RELOAD_WINDOW)),
      // openWalkthrough joins category#step itself (VS Code 1.140.0), so step is the bare id.
      vscode.commands.registerCommand('mastermind.health.debugPortStep', () =>
        vscode.commands.executeCommand('workbench.action.openWalkthrough', { category: `${extensionId}#setup`, step: 'debugPort' }, false)
      )
    );
    this.render();
  }

  private row(item: Item, mb: number, ceilingMb: number): Row {
    const warning = settings().get<number>(`${item}.warningPercent`, 60) / 100;
    const error = settings().get<number>(`${item}.errorPercent`, 75) / 100;
    const share = mb / ceilingMb;
    return { item, mb, ceilingMb, share, level: share >= error ? 'error' : share >= warning ? 'warning' : 'ok' };
  }

  private rows(): Row[] {
    const rows: Row[] = [];
    if (this.reading) {
      this.reading.servers.forEach((s) => rows.push(this.row(s.kind, s.mb, s.limitMb)));
      const p = this.reading.panel;
      if (p.state === 'ok' || p.state === 'stale') rows.push(this.row('panel', p.mb, p.limitMb));
    }
    return rows;
  }

  private worst(): Row | undefined {
    return [...this.rows()].sort((a, b) => RANK[b.level] - RANK[a.level] || b.share - a.share)[0];
  }

  /** One judgement: on a reading's arrival, and on the 5 s clock while a reading is held. */
  private tick(): void {
    this.settle();
    this.render();
    this.autoRestart();
  }

  /** A restart ends when its server answers under a new pid — an automatic one then says so for 10 s — and fails when
   *  two minutes pass without one. An automatic restart's failure clears once its server runs under a new pid or
   *  leaves the error band. */
  private settle(): void {
    for (const [kind, r] of this.restarting) {
      const now = this.reading?.servers.find((s) => s.kind === kind);
      if (now !== undefined && now.pid !== r.pid) {
        this.restarting.delete(kind);
        if (r.automatic) vscode.window.setStatusBarMessage(`$(sync) ${LABEL[kind]} restarted automatically at ${gb(r.mb)} GB`, 10000);
      } else if (Date.now() - r.at > RESTART_WAIT_MS) {
        this.restarting.delete(kind);
        this.restartFailed(
          kind,
          r.automatic,
          r.pid,
          now === undefined ? 'no new server came up within two minutes' : 'the same server was still running two minutes later'
        );
      }
    }
    for (const [kind, f] of this.failures) {
      const now = this.reading?.servers.find((s) => s.kind === kind);
      if (now !== undefined && (now.pid !== f.pid || this.rows().find((r) => r.item === kind)?.level !== 'error')) this.failures.delete(kind);
    }
  }

  /** What the item shows besides a reading: the exe's read error, the panel's, or an automatic restart's failure. */
  private problem(): string | undefined {
    return this.reading?.error ?? this.reading?.panel.error ?? [...this.failures.values()][0]?.text;
  }

  private render(): void {
    const worst = this.worst();
    const problem = this.problem();
    const shown = healthEnabled() && this.open && this.reading !== undefined && ((worst !== undefined && worst.level !== 'ok') || problem);
    if (shown) {
      const past = worst !== undefined && worst.level !== 'ok';
      // A panel worst offender in a busy window waits: a reload would restart a working agent, so no click is offered.
      const waiting = past && worst.item === 'panel' && this.reading?.busy === true;
      const restartingWorst = past && worst.item !== 'panel' && this.restarting.has(worst.item);
      this.item.text = !past
        ? '$(warning) Health'
        : restartingWorst
          ? `$(sync~spin) ${LABEL[worst.item]} restarting`
          : `$(pulse) ${LABEL[worst.item]} ${gb(worst.mb)} GB${waiting ? ' — waiting for agents' : ''}`;
      this.item.backgroundColor = new vscode.ThemeColor(worst?.level === 'error' ? 'statusBarItem.errorBackground' : 'statusBarItem.warningBackground');
      this.item.tooltip = this.tooltip(problem);
      this.item.command = waiting ? undefined : 'mastermind.health.free';
      this.item.show();
    } else this.item.hide();
    this.report(shown ? worst : undefined);
  }

  private tooltip(problem: string | undefined): vscode.MarkdownString {
    const tip = new vscode.MarkdownString('', true);
    tip.isTrusted = { enabledCommands: LINKED };
    tip.appendMarkdown('**VS Code health**\n\n');
    this.rows()
      .filter((r) => r.item !== 'panel')
      .forEach((r) => {
        const kind = r.item as Kind;
        const state = this.restarting.has(kind)
          ? 'restarting…'
          : `${gb(r.mb)} of ${gb(r.ceilingMb)} GB — [Restart](${link('mastermind.health.restart', [kind])})`;
        tip.appendMarkdown(`- ${LABEL[kind]}: ${state}\n`);
      });
    tip.appendMarkdown(`- Claude panel: ${this.panelLine()}\n`);
    if (problem) tip.appendMarkdown(`\n$(error) ${esc(problem)}\n`);
    tip.appendMarkdown(
      `\nA server in its error band restarts on its own once no file has been opened, changed or closed for ${settings().get<number>('quietSeconds', 30)} s, at most once per ${settings().get<number>('cooldownMinutes', 10)} min.`
    );
    return tip;
  }

  private panelLine(): string {
    const r = this.reading;
    let line = '';
    if (!r || r.panel.state === 'off') line = `needs the debug port — [Set it up](${link('mastermind.health.debugPortStep')})`;
    else if (r.panel.state === 'none') line = 'no Claude panel open';
    else if (r.panel.state === 'error') line = 'unreadable';
    else {
      const shared = r.panel.sharedWith ?? [];
      line =
        `${gb(r.panel.mb)} of ${gb(r.panel.limitMb)} GB${r.panel.state === 'stale' ? ' (its last reading — the panel is in the background)' : ''}` +
        `${shared.length > 0 ? `, shared with ${esc(shared.join(', '))} — it frees fully only once each of them reloads too` : ''} — `;
      line += r.busy
        ? `reload once every agent in this window has finished — ${esc(r.why ?? 'one is busy')}`
        : `[Reload panels](${link('mastermind.health.reloadWebviews')}) · [Reload window](${link('mastermind.health.reloadWindow')}) — each restarts every Claude session in this window`;
    }
    return line;
  }

  /** The level and the worst item's line go to the exe for the tray, on a change of level, item or tenth of a GB. */
  private report(worst: Row | undefined): void {
    const level: Level = worst ? worst.level : 'ok';
    const name = vscode.workspace.name ?? (vscode.workspace.workspaceFolders?.[0] ? path.basename(vscode.workspace.workspaceFolders[0].uri.fsPath) : 'VS Code');
    const text = worst ? `${LABEL[worst.item]} ${gb(worst.mb)} GB — ${name}` : '';
    const key = `${level}|${text}`;
    if (key !== this.reported && this.client.send({ type: 'health.level', level, item: worst?.item ?? '', text, share: worst?.share ?? 0 })) {
      this.reported = key;
    }
  }

  private autoRestart(): void {
    const quiet = Date.now() - this.lastActivity >= settings().get<number>('quietSeconds', 30) * 1000;
    const cooldownMs = settings().get<number>('cooldownMinutes', 10) * 60000;
    if (healthEnabled() && quiet && this.reading?.busy === false && !this.checking) {
      const due = this.rows()
        .filter((r) => r.item !== 'panel' && r.level === 'error')
        .map((r) => r.item as Kind)
        .filter((kind) => settings().get<boolean>(`${kind}.autoRestart`, true) && Date.now() - (this.lastAuto.get(kind) ?? 0) >= cooldownMs);
      if (due.length > 0) void this.restartWhenIdle(due);
    }
  }

  /** The reading's busy flag is up to 8 s old, so an automatic restart asks again first; a busy answer, or none, waits
   *  for a later tick. */
  private async restartWhenIdle(kinds: Kind[]): Promise<void> {
    this.checking = true;
    const now = await this.busyNow();
    this.checking = false;
    if (!now.busy) kinds.forEach((kind) => void this.restart(kind, true));
  }

  /** The exe's fresh word on this window's Claude sessions — anything but an answer reading busy false holds. */
  private async busyNow(): Promise<Busy> {
    let answer: Busy;
    try {
      const reply = await this.client.request({ type: 'health.busy' });
      answer =
        reply.ok === true && typeof reply.busy === 'boolean'
          ? { busy: reply.busy, why: typeof reply.why === 'string' ? reply.why : '' }
          : { busy: true, why: '', error: typeof reply.error === 'string' ? reply.error : 'no answer' };
    } catch (e) {
      answer = { busy: true, why: '', error: message(e) };
    }
    return answer;
  }

  /** The item's click: the worst offender past its warning share is freed; with none, the dialog says why the item
   *  shows, or that nothing needs freeing. */
  private async free(): Promise<void> {
    const worst = this.worst();
    const problem = this.problem();
    if (worst && worst.level !== 'ok') {
      if (worst.item === 'panel') await this.reload(RELOAD_WEBVIEWS);
      else await this.restart(worst.item, false);
    } else {
      void vscode.window.showInformationMessage(
        problem ? 'Nothing past its warning share' : 'Nothing to free',
        {
          modal: true,
          detail: problem
            ? `The item shows for this: ${problem}\n\nNothing in this window is past its warning share, so there is nothing to free; the item hides once this clears.`
            : 'Every reading in this window is under its warning share.',
        },
        OK
      );
    }
  }

  /** A restart, clicked or automatic; the server's new pid in a later reading ends it (settle). A click on a server
   *  already restarting, or not running, says which. */
  private async restart(kind: Kind, automatic: boolean): Promise<void> {
    const server = this.reading?.servers.find((s) => s.kind === kind);
    if (server && !this.restarting.has(kind)) {
      this.restarting.set(kind, { pid: server.pid, mb: server.mb, at: Date.now(), automatic });
      if (automatic) this.lastAuto.set(kind, Date.now());
      this.failures.delete(kind);
      this.render();
      try {
        await vscode.commands.executeCommand(RESTART[kind]);
      } catch (e) {
        this.restarting.delete(kind);
        this.restartFailed(kind, automatic, server.pid, message(e));
      }
    } else if (!automatic) {
      void vscode.window.showInformationMessage(
        server ? `${LABEL[kind]} is already restarting` : `${LABEL[kind]} is not running`,
        {
          modal: true,
          detail: server
            ? 'Its restart is under way: the item shows the new server once it answers, or the failure after two minutes.'
            : `No ${LABEL[kind]} server runs in this window, so there is nothing to restart.`,
        },
        OK
      );
    }
  }

  private async reload(command: string): Promise<void> {
    const now = await this.busyNow();
    if (now.busy) {
      void vscode.window.showInformationMessage(
        'Not reloaded',
        {
          modal: true,
          detail:
            now.error !== undefined
              ? `Mastermind did not say whether an agent in this window is working (${now.error}), and a reload restarts every Claude session here. Try again in a moment.`
              : `A reload restarts every Claude session in this window, and ${now.why}. Reload once every agent there has finished.`,
        },
        OK
      );
    } else {
      // The exe forgets this window's panel reading, which the reload frees, and reads the panels on its next tick.
      this.client.send({ type: 'health.reloaded' });
      try {
        await vscode.commands.executeCommand(command);
      } catch (e) {
        void vscode.window.showErrorMessage('Reload failed', { modal: true, detail: `${message(e)}\n\nTry the same reload from the Command Palette.` }, OK);
      }
    }
  }

  /** A restart that failed — the command threw, or no new pid in two minutes. An automatic one has no click to answer,
   *  so it stays on the item and in a status-bar message; a clicked one answers in the modal. */
  private restartFailed(kind: Kind, automatic: boolean, pid: number, why: string): void {
    if (automatic) {
      const text = `Automatic restart of ${LABEL[kind]} failed: ${why}`;
      this.failures.set(kind, { text, pid });
      vscode.window.setStatusBarMessage(`$(error) ${text}`, 15000);
    } else {
      void vscode.window.showErrorMessage(
        `${LABEL[kind]} did not restart`,
        { modal: true, detail: `${why}\n\nTry again from the health item, or run the server's own restart command from the Command Palette.` },
        OK
      );
    }
    this.render();
  }

  dispose(): void {
    this.disposables.forEach((d) => d.dispose());
  }
}
