import { spawn } from 'child_process';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { Client, findPort } from './client';
import { appDir, askLockPath } from './paths';

const STALE_LOCK_MS = 60000;
const RECHECKS = 3;
const RECHECK_MS = 3000; // a restarting Mastermind, or one started at login racing VS Code's own start, binds within seconds
const LOGIN_ASKED = 'mastermind.loginAsked';

/**
 * Start Mastermind.exe from mastermind.exePath — never any other copy: two copies on one machine fight over the
 * single-instance lock and the login shortcut. Detached and unref-ed: a child left in the extension host's job dies
 * with the host (Reload Window, a window close, an extension update), while a detached one leaves every job the host
 * sits in, each allowing silent breakaway. The first Start on this machine asks once whether to start Mastermind at
 * login.
 */
export async function startExe(context: vscode.ExtensionContext, client: Client): Promise<void> {
  const exePath = vscode.workspace.getConfiguration('mastermind').get<string>('exePath', '');
  if (!exePath || !fs.existsSync(exePath)) {
    const pick = await vscode.window.showErrorMessage(
      `Mastermind: no Mastermind.exe at "${exePath}" — set mastermind.exePath for this machine.`,
      'Open the setting'
    );
    if (pick === 'Open the setting') await vscode.commands.executeCommand('workbench.action.openSettings', 'mastermind.exePath');
  } else {
    const child = spawn(exePath, [], { detached: true, stdio: 'ignore' });
    child.on('error', (error) => void vscode.window.showErrorMessage(`Mastermind did not start: ${error.message}`));
    child.unref();
    client.kick();
    await askLoginOnce(context);
  }
}

/** mastermind.startAtLogin is machine-scoped and the mark is this profile's globalState, so each machine is asked once per
 * VS Code profile. */
async function askLoginOnce(context: vscode.ExtensionContext): Promise<void> {
  const settings = vscode.workspace.getConfiguration('mastermind');
  if (!context.globalState.get<boolean>(LOGIN_ASKED)) {
    await context.globalState.update(LOGIN_ASKED, true);
    if (!settings.get<boolean>('startAtLogin', false)) {
      const pick = await vscode.window.showInformationMessage('Start Mastermind when you log in?', 'Yes', 'No');
      if (pick === 'Yes') await settings.update('startAtLogin', true, vscode.ConfigurationTarget.Global);
    }
  }
}

/** Mastermind stays silent over a few seconds of rechecks — a restart or a login start in progress is not "down". */
async function stillDown(): Promise<boolean> {
  let down = (await findPort()) === undefined;
  for (let i = 1; i < RECHECKS && down; i++) {
    await new Promise((resolve) => setTimeout(resolve, RECHECK_MS));
    down = (await findPort()) === undefined;
  }
  return down;
}

/**
 * At activation: a window that finds no Mastermind asks to start it — only the first to take the shared ask lock, since
 * VS Code reopens every window at once; the rest show the badge's "Mastermind off", and the answer covers them all.
 * "Not now" frees the lock, so the next window to open asks again; "Don't ask again" turns mastermind.askToStart off.
 */
export async function askIfDown(context: vscode.ExtensionContext, client: Client): Promise<void> {
  const settings = vscode.workspace.getConfiguration('mastermind');
  if (settings.get<boolean>('askToStart', true) && (await stillDown()) && takeAskLock()) {
    try {
      const pick = await vscode.window.showInformationMessage("Mastermind isn't running — start it?", 'Start', 'Not now', "Don't ask again");
      if (pick === 'Start') await startExe(context, client);
      else if (pick === "Don't ask again") await settings.update('askToStart', false, vscode.ConfigurationTarget.Global);
    } finally {
      releaseAskLock();
    }
  }
}

/** The ask lock, taken exclusively; one older than a minute belongs to a window that crashed mid-question and is
 * taken over. */
function takeAskLock(): boolean {
  let taken = false;
  try {
    fs.mkdirSync(appDir(), { recursive: true });
    for (let tries = 0; tries < 2 && !taken; tries++) {
      try {
        fs.closeSync(fs.openSync(askLockPath(), 'wx'));
        taken = true;
      } catch (error) {
        const held = (error as NodeJS.ErrnoException).code === 'EEXIST';
        if (held && Date.now() - fs.statSync(askLockPath()).mtimeMs > STALE_LOCK_MS) fs.rmSync(askLockPath(), { force: true });
        else tries = 2;
      }
    }
  } catch {
    taken = false;
  }
  return taken;
}

function releaseAskLock(): void {
  try {
    fs.rmSync(askLockPath(), { force: true });
  } catch {
    // gone already
  }
}
