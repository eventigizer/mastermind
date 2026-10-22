import * as fs from 'fs';
import { configPath } from './paths';

export type ConfigFile = Record<string, unknown>;

/**
 * Mastermind's config.json as the exe reads it — never written here, the exe being its one writer. Null when there is no
 * file; 'unparsed' when it cannot be read or is not a JSON object. A leading byte-order mark goes first: JSON.parse
 * throws on one, and Windows PowerShell 5.1 writes it.
 */
export function readConfig(): ConfigFile | null | 'unparsed' {
  let result: ConfigFile | null | 'unparsed' = 'unparsed';
  let text: string | undefined;
  try {
    text = fs.readFileSync(configPath(), 'utf8');
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') result = null;
  }
  if (text !== undefined) {
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    try {
      const value: unknown = JSON.parse(text);
      if (value !== null && typeof value === 'object' && !Array.isArray(value)) result = value as ConfigFile;
    } catch {
      result = 'unparsed';
    }
  }
  return result;
}

/** A value of the shape a config key needs — the exe's Config.IsPort for a port, 0 allowed for port0 (off). */
export function fits(kind: 'port' | 'port0' | 'flag', value: unknown): boolean {
  return kind === 'flag'
    ? typeof value === 'boolean'
    : typeof value === 'number' && Number.isInteger(value) && value <= 65535 && (value > 0 || (kind === 'port0' && value === 0));
}

/** The port the exe listens on by its config — 7778, Mastermind's own, unless config's hookPort says otherwise. */
export function configPort(): number {
  const config = readConfig();
  const port = config !== null && config !== 'unparsed' ? config.hookPort : undefined;
  return fits('port', port) ? (port as number) : 7778;
}
