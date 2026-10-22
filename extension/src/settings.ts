import * as vscode from 'vscode';
import { Client } from './client';
import { ConfigFile, fits, readConfig } from './config';

/** Each mirrored setting, the config key the exe keeps it under, and the shape a config value needs to import. */
const MIRRORED: { setting: string; key: string; kind: 'port' | 'port0' | 'flag' }[] = [
  { setting: 'hookPort', key: 'hookPort', kind: 'port' },
  { setting: 'debugPort', key: 'debugPort', kind: 'port0' },
  { setting: 'startAtLogin', key: 'startAtLogin', kind: 'flag' },
  { setting: 'newDispatchAutoAllow', key: 'newDispatchAutoAllow', kind: 'flag' },
  { setting: 'relay', key: 'handoffs.enabled', kind: 'flag' },
];
const IMPORTED = 'mastermind.imported';
const SEND_DEBOUNCE_MS = 1500; // the Settings UI can save a number at each pause in the typing

let importWarned = false;

function fromConfig(config: ConfigFile, key: string): unknown {
  const [head, tail] = key.split('.');
  const value = config[head];
  if (tail === undefined) return value;
  return value !== null && typeof value === 'object' ? (value as ConfigFile)[tail] : undefined;
}

/**
 * The first run on this machine copies the config file's value into VS Code Settings for every mirrored key the user
 * has not set there — otherwise the first send puts VS Code's defaults over the flags the file already holds. Once per
 * VS Code profile, under a globalState mark: without it every activation re-imports, and a key reset to its default while
 * Mastermind was down would come back from the stale config. No config file at all is an import with nothing to copy (a
 * machine nothing has written one on). True once the import has run; until then nothing is sent — a config that does not
 * parse, or a settings write VS Code refuses, warns once and is tried again at the next connect or change. Never
 * throws.
 */
export async function importOnce(context: vscode.ExtensionContext): Promise<boolean> {
  let ready = context.globalState.get<boolean>(IMPORTED) === true;
  if (!ready) {
    try {
      const config = readConfig();
      if (config === 'unparsed') throw new Error("Mastermind's config.json does not parse — fix it, and the next connect imports");
      const settings = vscode.workspace.getConfiguration('mastermind');
      for (const m of MIRRORED) {
        const value = config === null ? undefined : fromConfig(config, m.key);
        if (settings.inspect(m.setting)?.globalValue === undefined && fits(m.kind, value)) {
          await settings.update(m.setting, value, vscode.ConfigurationTarget.Global);
        }
      }
      await context.globalState.update(IMPORTED, true);
      ready = true;
    } catch (error) {
      if (!importWarned) {
        importWarned = true;
        void vscode.window.showWarningMessage(
          `Mastermind: settings are not sent until the first import succeeds — ${error instanceof Error ? error.message : String(error)}`
        );
      }
    }
  }
  return ready;
}

/**
 * The CLAUDE_CONFIG_DIR a sidebar session runs with: the Claude extension lays its claudeCode.environmentVariables
 * entries over process.env, so that setting's entry wins — it never reaches this host's process.env — else this
 * extension host's environment. Undefined when neither sets it.
 */
function claudeConfigDir(): string | undefined {
  const vars: unknown = vscode.workspace.getConfiguration('claudeCode').get('environmentVariables');
  const entry: unknown = Array.isArray(vars) ? vars.find((v) => v?.name === 'CLAUDE_CONFIG_DIR') : undefined;
  const fromSetting = entry !== null && typeof entry === 'object' ? (entry as { value?: unknown }).value : undefined;
  return (typeof fromSetting === 'string' && fromSetting) || process.env.CLAUDE_CONFIG_DIR || undefined;
}

/**
 * What the exe writes into its config: each mirrored setting under its config key, and claudeDir when
 * CLAUDE_CONFIG_DIR is set (claudeConfigDir) — the exe never reads that variable, its own environment differing by
 * launch path; a window without it sends none, so it never argues with one that has it.
 */
function settingsValues(): Record<string, unknown> {
  const settings = vscode.workspace.getConfiguration('mastermind');
  const values: Record<string, unknown> = {};
  for (const m of MIRRORED) values[m.key] = settings.get(m.setting);
  const claudeDir = claudeConfigDir();
  if (claudeDir) values.claudeDir = claudeDir;
  return values;
}

/**
 * The settings go to the exe on every connect — marked, so a run a change just restarted holds a disagreeing window's
 * values instead of restarting back — and on every change of a mirrored one, or of the Claude extension's environment
 * variables, once the typing settles; never before the first-run import has run.
 */
export function wireSettingsSends(context: vscode.ExtensionContext, client: Client): void {
  let timer: NodeJS.Timeout | undefined;
  const send = async (connect: boolean): Promise<void> => {
    if (await importOnce(context)) client.send({ type: 'settings', values: settingsValues(), connect });
  };
  client.onConnected(() => void send(true));
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (MIRRORED.some((m) => event.affectsConfiguration(`mastermind.${m.setting}`)) || event.affectsConfiguration('claudeCode.environmentVariables')) {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => void send(false), SEND_DEBOUNCE_MS);
      }
    }),
    { dispose: () => timer && clearTimeout(timer) }
  );
}
