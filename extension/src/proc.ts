import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { findPort } from './client';

export const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/** A lock file taken exclusively; one older than staleMs belongs to a window that died holding it and is taken over. */
export function takeLock(file: string, staleMs: number): boolean {
  let taken = false;
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    for (let tries = 0; tries < 2 && !taken; tries++) {
      try {
        fs.closeSync(fs.openSync(file, 'wx'));
        taken = true;
      } catch (error) {
        const held = (error as NodeJS.ErrnoException).code === 'EEXIST';
        if (held && Date.now() - fs.statSync(file).mtimeMs > staleMs) fs.rmSync(file, { force: true });
        else tries = 2;
      }
    }
  } catch {
    taken = false;
  }
  return taken;
}

export function releaseLock(file: string): void {
  try {
    fs.rmSync(file, { force: true });
  } catch {
    // gone already
  }
}

/** Another window holds this lock, and it is younger than staleMs. */
export function lockHeld(file: string, staleMs: number): boolean {
  try {
    return Date.now() - fs.statSync(file).mtimeMs <= staleMs;
  } catch {
    return false;
  }
}

/** Polls until Mastermind's port answers (up) or stops answering (!up), or ms pass; true when it got there. */
export async function waitForPort(up: boolean, ms: number, everyMs = 500): Promise<boolean> {
  const end = Date.now() + ms;
  let there = ((await findPort()) !== undefined) === up;
  while (!there && Date.now() < end) {
    await sleep(everyMs);
    there = ((await findPort()) !== undefined) === up;
  }
  return there;
}

/**
 * Mastermind.exe started detached and unref-ed — a child left in the extension host's job dies with the host (Reload
 * Window, a window close, an extension update), while a detached one leaves every job the host sits in, each allowing
 * silent breakaway — its start failure shown, never an unhandled 'error' event.
 */
export function spawnDetached(exe: string, args: string[]): void {
  const child = spawn(exe, args, { detached: true, stdio: 'ignore' });
  child.on('error', (error) => void vscode.window.showErrorMessage(`Mastermind did not start: ${error.message}`));
  child.unref();
}
