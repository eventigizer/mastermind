import * as fs from 'fs';
import * as vscode from 'vscode';
import { Client, findPort } from './client';
import { effectiveExe, ensureInstalled, settleAfterStart, UPDATE_STALE_MS } from './install';
import { askLockPath, updateLockPath } from './paths';
import { lockHeld, releaseLock, sleep, spawnDetached, takeLock } from './proc';

const STALE_LOCK_MS = 60000;
const RECHECKS = 3;
const RECHECK_MS = 3000; // a restarting Mastermind, or one started at login racing VS Code's own start, binds within seconds
const LOGIN_ASKED = 'mastermind.loginAsked';

/**
 * Start Mastermind.exe — mastermind.exePath when set, else the bundled copy installed in the app folder — never any
 * other copy: two copies on one machine fight over the single-instance lock and the login shortcut. Detached
 * (spawnDetached). Another window's update in progress is waited out first, so a Start never runs the build being
 * swapped. The first Start on this machine asks once whether to start Mastermind at login.
 */
export async function startExe(context: vscode.ExtensionContext, client: Client): Promise<void> {
  for (let i = 0; i < 60 && lockHeld(updateLockPath(), UPDATE_STALE_MS); i++) await sleep(1000);
  const set = vscode.workspace.getConfiguration('mastermind').get<string>('exePath', '');
  const exePath = (await ensureInstalled(context, client)) ? effectiveExe() : set;
  if (!exePath || !fs.existsSync(exePath)) {
    const pick = await vscode.window.showErrorMessage(
      `Mastermind: no Mastermind.exe at "${exePath}" — reinstall the Agent Mastermind extension, or set mastermind.exePath for this machine.`,
      'Open the setting'
    );
    if (pick === 'Open the setting') await vscode.commands.executeCommand('workbench.action.openSettings', 'mastermind.exePath');
  } else {
    spawnDetached(exePath, []);
    client.kick();
    void settleAfterStart(client);
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

/** Mastermind stays silent over a few seconds of rechecks — a restart or a login start in progress is not "down". Counted,
 *  never timed: the same three checks however the clock runs. */
async function stillDown(): Promise<boolean> {
  let down = (await findPort()) === undefined;
  for (let i = 1; i < RECHECKS && down; i++) {
    await sleep(RECHECK_MS);
    down = (await findPort()) === undefined;
  }
  return down;
}

/**
 * At activation: a window that finds no Mastermind asks to start it — only the first to take the shared ask lock, since
 * VS Code reopens every window at once; the rest show the badge's "Mastermind off", and the answer covers them all.
 * Never while another window's update holds the update lock — that update starts Mastermind again itself. "Not now"
 * frees the lock, so the next window to open asks again; "Don't ask again" turns mastermind.askToStart off.
 */
export async function askIfDown(context: vscode.ExtensionContext, client: Client): Promise<void> {
  const settings = vscode.workspace.getConfiguration('mastermind');
  if (settings.get<boolean>('askToStart', true) && (await stillDown()) && !lockHeld(updateLockPath(), UPDATE_STALE_MS)
      && takeLock(askLockPath(), STALE_LOCK_MS)) {
    try {
      const pick = await vscode.window.showInformationMessage("Mastermind isn't running — start it?", 'Start', 'Not now', "Don't ask again");
      if (pick === 'Start') await startExe(context, client);
      else if (pick === "Don't ask again") await settings.update('askToStart', false, vscode.ConfigurationTarget.Global);
    } finally {
      releaseLock(askLockPath());
    }
  }
}
