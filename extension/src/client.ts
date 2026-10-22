import * as vscode from 'vscode';
import { configPort } from './config';

export type Message = { type: string; [key: string]: unknown };

const BACKOFF_MS = [1000, 2000, 5000, 10000];
const PING_TIMEOUT_MS = 1500;
const REQUEST_TIMEOUT_MS = 15000;

/** True when Mastermind answers on the port — the exe answers every GET but /hook with "Mastermind". */
async function ping(port: number): Promise<boolean> {
  let alive = false;
  try {
    const response = await fetch(`http://127.0.0.1:${port}/ping`, { signal: AbortSignal.timeout(PING_TIMEOUT_MS) });
    alive = response.ok && (await response.text()) === 'Mastermind';
  } catch {
    alive = false;
  }
  return alive;
}

/**
 * The port Mastermind answers on: mastermind.hookPort, else the config file's hookPort — a port changed while Mastermind was
 * down, or synced from another machine, leaves the exe on the old one until the change reaches it over this
 * connection. Undefined when neither answers.
 */
export async function findPort(): Promise<number | undefined> {
  const mine = vscode.workspace.getConfiguration('mastermind').get<number>('hookPort', 7778);
  let found: number | undefined;
  if (await ping(mine)) found = mine;
  else {
    const theirs = configPort();
    if (theirs !== mine && (await ping(theirs))) found = theirs;
  }
  return found;
}

/**
 * This window's connection to Mastermind: a WebSocket on the exe's /client, reconnected with backoff while the exe is
 * down. On open it sends the hello, then runs each onConnected hook. Messages are JSON objects carrying `type`; a
 * `reply` answers the request whose id it carries, every other type goes to its handler.
 */
export class Client implements vscode.Disposable {
  private socket: WebSocket | undefined;
  private connecting = false;
  private attempt = 0;
  private timer: NodeJS.Timeout | undefined;
  private nextId = 1;
  private disposed = false;
  private readonly waiting = new Map<number, (reply: Message) => void>();
  private readonly handlers = new Map<string, (message: Message) => void>();
  private readonly connectedHooks: (() => void)[] = [];
  private readonly stateEmitter = new vscode.EventEmitter<boolean>();
  /** True on connect, false on a drop or a failed attempt. */
  readonly onState = this.stateEmitter.event;

  constructor(private readonly hello: () => Message) {}

  get isOpen(): boolean {
    return this.socket !== undefined && this.socket.readyState === WebSocket.OPEN;
  }

  on(type: string, handler: (message: Message) => void): void {
    this.handlers.set(type, handler);
  }

  onConnected(hook: () => void): void {
    this.connectedHooks.push(hook);
  }

  send(message: Message): boolean {
    const open = this.isOpen;
    if (open) this.socket?.send(JSON.stringify(message));
    return open;
  }

  /** A request the exe answers with a `reply` carrying the same id; a dropped connection answers ok false. */
  request(message: Message): Promise<Message> {
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timer = setTimeout(() => {
        this.waiting.delete(id);
        reject(new Error('Mastermind did not answer'));
      }, REQUEST_TIMEOUT_MS);
      this.waiting.set(id, (reply) => {
        clearTimeout(timer);
        resolve(reply);
      });
      if (!this.send({ ...message, id })) {
        clearTimeout(timer);
        this.waiting.delete(id);
        reject(new Error("Mastermind isn't running"));
      }
    });
  }

  start(): void {
    void this.connect();
  }

  /** Connect again now with the backoff reset — after a Start, when the exe answers within seconds. */
  kick(): void {
    this.attempt = 0;
    if (this.timer) clearTimeout(this.timer);
    void this.connect();
  }

  private async connect(): Promise<void> {
    if (!this.disposed && !this.connecting && this.socket === undefined) {
      this.connecting = true;
      const port = await findPort();
      this.connecting = false;
      if (!this.disposed && port === undefined) {
        this.stateEmitter.fire(false);
        this.retry();
      } else if (!this.disposed && port !== undefined) this.open(port);
    }
  }

  private open(port: number): void {
    const socket = new WebSocket(`ws://127.0.0.1:${port}/client`);
    this.socket = socket;
    socket.onopen = () => {
      this.attempt = 0;
      socket.send(JSON.stringify(this.hello()));
      this.connectedHooks.forEach((hook) => hook());
      this.stateEmitter.fire(true);
    };
    socket.onmessage = (event) => this.dispatch(event.data);
    socket.onclose = () => {
      if (this.socket === socket) {
        this.socket = undefined;
        this.waiting.forEach((done, id) => done({ type: 'reply', id, ok: false, error: 'Mastermind disconnected' }));
        this.waiting.clear();
        this.stateEmitter.fire(false);
        this.retry();
      }
    };
    socket.onerror = () => undefined; // onclose follows
  }

  private retry(): void {
    if (!this.disposed) {
      const delay = BACKOFF_MS[Math.min(this.attempt, BACKOFF_MS.length - 1)];
      this.attempt += 1;
      this.timer = setTimeout(() => void this.connect(), delay);
    }
  }

  private dispatch(data: unknown): void {
    let message: Message | undefined;
    try {
      const parsed: unknown = JSON.parse(String(data));
      if (parsed !== null && typeof parsed === 'object' && typeof (parsed as Message).type === 'string') message = parsed as Message;
    } catch {
      message = undefined;
    }
    if (message && message.type === 'reply' && typeof message.id === 'number') {
      const done = this.waiting.get(message.id);
      if (done) {
        this.waiting.delete(message.id);
        done(message);
      }
    } else if (message) {
      const handler = this.handlers.get(message.type);
      if (handler) handler(message);
    }
  }

  dispose(): void {
    this.disposed = true;
    if (this.timer) clearTimeout(this.timer);
    const socket = this.socket;
    this.socket = undefined;
    if (socket) socket.close();
    this.stateEmitter.dispose();
  }
}
