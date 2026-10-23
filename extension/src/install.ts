import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { Client, findPort } from './client';
import { installedAppDir, updateLockPath } from './paths';
import { releaseLock, sleep, spawnDetached, takeLock, waitForPort } from './proc';

export const UPDATE_STALE_MS = 120000;
const OPEN_WAIT_MS = 10000;
const STOP_WAIT_MS = 15000;
const START_WAIT_MS = 20000;

/** The build this package carries, or undefined when it carries none (a development host). */
const bundledDir = (context: vscode.ExtensionContext): string | undefined => {
  const dir = path.join(context.extensionPath, 'bin', 'app');
  return fs.existsSync(path.join(dir, 'Mastermind.exe')) ? dir : undefined;
};

const versionOf = (dir: string): string => {
  try {
    return fs.readFileSync(path.join(dir, 'version.txt'), 'utf8').trim();
  } catch {
    return '';
  }
};

const newer = (a: string, b: string): boolean => {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) > (pb[i] || 0);
  return false;
};

const warn = (error: unknown): void =>
  void vscode.window.showWarningMessage(`Mastermind: the update did not go in — ${error instanceof Error ? error.message : String(error)}`);

/** mastermind.exePath when set — a build of your own, never replaced — else the installed bundled copy. */
export function effectiveExe(): string {
  const set = vscode.workspace.getConfiguration('mastermind').get<string>('exePath', '');
  return set || path.join(installedAppDir(), 'Mastermind.exe');
}

/** Rename, retried while a just-stopped exe still holds its files. */
async function renameRetried(from: string, to: string): Promise<void> {
  const end = Date.now() + STOP_WAIT_MS;
  for (;;) {
    try {
      fs.renameSync(from, to);
      return;
    } catch (error) {
      if (Date.now() > end) throw error;
      await sleep(250);
    }
  }
}

/** The window's channel open, or false once OPEN_WAIT_MS pass — at activation the client is still finding the port. */
async function channelOpen(client: Client): Promise<boolean> {
  const end = Date.now() + OPEN_WAIT_MS;
  while (!client.isOpen && Date.now() < end) await sleep(100);
  return client.isOpen;
}

/**
 * The bundled build into the app folder when none is installed (`ensureInstalled`, at Start) or when the package's is
 * newer (`updateIfNewer`, at activation). A running Mastermind is asked to quit over this window's channel — waited
 * open first, since activation runs while the client still connects; an update that cannot ask is left for the next
 * activation. The new folder is copied beside the installed one, without blocking the extension host, and the two are
 * swapped by rename once the old app stopped. A Mastermind that ran starts again from the new folder, in the
 * background as a settings change restarts it, and the old folder goes once the new one answers, else the two swap
 * back; one that did not run keeps the old folder until its next start answers (`settleAfterStart`). One window at a
 * time, under update.lock. With mastermind.exePath set, nothing here runs.
 */
async function swapIn(client: Client, bundled: string): Promise<'done' | 'busy' | 'failed'> {
  if (!takeLock(updateLockPath(), UPDATE_STALE_MS)) return 'busy';
  const app = installedAppDir();
  const next = `${app}.new`;
  const old = `${app}.old`;
  let outcome: 'done' | 'busy' | 'failed' = 'failed';
  try {
    const wasRunning = (await findPort()) !== undefined;
    if (wasRunning && !(await channelOpen(client))) outcome = 'busy';
    else {
      await fs.promises.rm(next, { recursive: true, force: true });
      await fs.promises.rm(old, { recursive: true, force: true });
      await fs.promises.cp(bundled, next, { recursive: true });
      if (wasRunning) {
        if (!client.send({ type: 'quit' })) throw new Error("the window's channel closed before Mastermind could be asked to quit");
        if (!(await waitForPort(false, STOP_WAIT_MS, 250))) throw new Error('Mastermind did not quit');
      }
      const hadApp = fs.existsSync(app);
      if (hadApp) await renameRetried(app, old);
      await renameRetried(next, app);
      if (wasRunning) {
        spawnDetached(path.join(app, 'Mastermind.exe'), ['--background']);
        client.kick();
        if (await waitForPort(true, START_WAIT_MS)) await fs.promises.rm(old, { recursive: true, force: true });
        else if (hadApp) {
          await renameRetried(app, next);
          await renameRetried(old, app);
          spawnDetached(path.join(app, 'Mastermind.exe'), ['--background']);
          throw new Error('the new Mastermind did not start — the old one is back');
        }
      }
      outcome = 'done';
    }
  } catch (error) {
    warn(error);
  } finally {
    releaseLock(updateLockPath());
  }
  return outcome;
}

/** At Start, with no exePath set: the bundled build installed when none is. True when an exe is there to start. */
export async function ensureInstalled(context: vscode.ExtensionContext, client: Client): Promise<boolean> {
  const set = vscode.workspace.getConfiguration('mastermind').get<string>('exePath', '');
  const bundled = bundledDir(context);
  if (!set && bundled && !fs.existsSync(effectiveExe())) await swapIn(client, bundled);
  return fs.existsSync(effectiveExe());
}

/** At activation, with no exePath set: a newer bundled build swapped in, and a notice when it went. */
export async function updateIfNewer(context: vscode.ExtensionContext, client: Client): Promise<void> {
  const set = vscode.workspace.getConfiguration('mastermind').get<string>('exePath', '');
  const bundled = bundledDir(context);
  if (!set && bundled && fs.existsSync(effectiveExe()) && newer(versionOf(bundled), versionOf(installedAppDir()))) {
    if ((await swapIn(client, bundled)) === 'done')
      void vscode.window.showInformationMessage(`Mastermind updated to ${versionOf(installedAppDir())}`);
  }
}

/** After a Start: an update made while Mastermind was stopped kept the old folder — gone once the new build answers,
 * else swapped back and started. */
export async function settleAfterStart(client: Client): Promise<void> {
  const app = installedAppDir();
  const old = `${app}.old`;
  if (fs.existsSync(old) && takeLock(updateLockPath(), UPDATE_STALE_MS)) {
    try {
      if (await waitForPort(true, START_WAIT_MS)) await fs.promises.rm(old, { recursive: true, force: true });
      else {
        await renameRetried(app, `${app}.new`);
        await renameRetried(old, app);
        spawnDetached(path.join(app, 'Mastermind.exe'), []);
        client.kick();
        throw new Error('the updated build did not start — the previous one is back');
      }
    } catch (error) {
      warn(error);
    } finally {
      releaseLock(updateLockPath());
    }
  }
}
