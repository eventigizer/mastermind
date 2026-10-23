// Smoke test for the Mastermind shell — run after every build; pack.ps1 runs it before it packages:
//   cd extension && npm run build && node smoke.js
// Loads out/extension.js against a stub vscode module, a fake exe (fetch + WebSocket) and a fake spawn — no Mastermind,
// no VS Code, no process — one window per state. Fixtures go under the repo's tmp folder, LOCALAPPDATA pointed there;
// .vscodeignore keeps this file out of the package.
'use strict';
const assert = require('assert');
const Module = require('module');
const path = require('path');
const fs = require('fs');
const childProcess = require('child_process');

const REPO = path.resolve(__dirname, '..');
const FIXTURES = path.join(REPO, 'tmp', 'smoke-mastermind-vscode');
const OUT = path.join(__dirname, 'out');
const PACKAGE = require('./package.json');
const PROPS = Object.assign({}, ...PACKAGE.contributes.configuration.map((s) => s.properties));
process.env.LOCALAPPDATA = path.join(FIXTURES, 'LocalAppData');
delete process.env.CLAUDE_CONFIG_DIR; // each check sets it when it needs it
const APP_DIR = path.join(process.env.LOCALAPPDATA, 'Mastermind');
const CONFIG = path.join(APP_DIR, 'config.json');
const LOCK = path.join(APP_DIR, 'ask.lock');
const EXE = path.join(FIXTURES, 'Mastermind.exe');
// The package a window's extension runs from: one carrying no build (a development host), and one carrying bin/app.
const NO_BUNDLE = path.join(FIXTURES, 'ext-dev');
const BUNDLE = path.join(FIXTURES, 'ext');
const INSTALLED = path.join(APP_DIR, 'app');
// A fake editor install and settings folder: the hello's editor identity is read off them.
const APP_ROOT = path.join(FIXTURES, 'Editor', '0123456789', 'resources', 'app'); // VS Code's per-build folder
const EDITOR_CLI = path.join(FIXTURES, 'Editor', 'bin', 'code.cmd');
const USER_DATA = path.join(FIXTURES, 'EditorData');
fs.mkdirSync(APP_ROOT, { recursive: true });
fs.mkdirSync(path.dirname(EDITOR_CLI), { recursive: true });
fs.writeFileSync(EDITOR_CLI, '');
fs.mkdirSync(APP_DIR, { recursive: true });
fs.writeFileSync(EXE, ''); // it exists; spawn is faked

// Timers from 1 s up to the 15 s request timeout (backoff, debounces, rechecks) fire after 20 ms; an interval runs only
// when a check drives it by its delay (`tick`).
const realSetTimeout = global.setTimeout;
global.setTimeout = (fn, ms, ...rest) => realSetTimeout(fn, ms >= 1000 && ms < 15000 ? 20 : ms, ...rest);
const intervals = new Set();
global.setInterval = (fn, ms) => {
  const timer = { fn, ms, unref() {} };
  intervals.add(timer);
  return timer;
};
global.clearInterval = (timer) => intervals.delete(timer);
const tick = (ms) => [...intervals].filter((t) => t.ms === ms).forEach((t) => t.fn());
let offset = 0;
const realNow = Date.now;
Date.now = () => realNow() + offset;
const wait = (ms) => new Promise((resolve) => realSetTimeout(resolve, ms));

// The exe, faked: the ports that answer, every socket opened and what each received. With its lifecycle on, a quit
// stops it and a spawned Mastermind.exe answers; quits records whether each arrived over an open channel.
const exe = { ports: new Set(), sockets: [], cdpTargets: [], lifecycle: false, quits: [] };
Object.defineProperty(globalThis, 'fetch', {
  configurable: true,
  writable: true,
  value: async (url) => {
    const u = new URL(url);
    if (!exe.ports.has(Number(u.port))) throw new TypeError('fetch failed');
    if (u.pathname === '/ping') return { ok: true, text: async () => 'Mastermind' };
    return { ok: true, json: async () => exe.cdpTargets, text: async () => '' };
  },
});
class FakeSocket {
  constructor(url) {
    this.url = url;
    this.port = Number(new URL(url).port);
    this.readyState = 0;
    this.sent = [];
    exe.sockets.push(this);
    realSetTimeout(() => {
      if (exe.ports.has(this.port)) {
        this.readyState = 1;
        if (this.onopen) this.onopen();
      } else {
        this.readyState = 3;
        if (this.onclose) this.onclose({ code: 1006 });
      }
    }, 5);
  }
  send(text) {
    const m = JSON.parse(text);
    this.sent.push(m);
    if (m.type === 'quit' && exe.lifecycle) {
      exe.quits.push({ open: this.readyState === 1 });
      exe.ports.delete(this.port);
      realSetTimeout(() => this.close(), 5);
    }
  }
  close() {
    if (this.readyState !== 3) {
      this.readyState = 3;
      if (this.onclose) this.onclose({ code: 1000 });
    }
  }
  receive(message) {
    if (this.onmessage) this.onmessage({ data: JSON.stringify(message) });
  }
  last(type) {
    return [...this.sent].reverse().find((m) => m.type === type);
  }
}
FakeSocket.OPEN = 1;
Object.defineProperty(globalThis, 'WebSocket', { configurable: true, writable: true, value: FakeSocket });

const spawned = [];
childProcess.spawn = (file, args, options) => {
  const child = {
    file,
    args,
    options,
    unrefed: false,
    on: () => child,
    unref: () => {
      child.unrefed = true;
    },
  };
  spawned.push(child);
  if (exe.lifecycle && path.basename(file) === 'Mastermind.exe') exe.ports.add(7778);
  return child;
};

// Settings every window shares, and this machine's globalState.
let settings = {};
let globalState = new Map();

function makeVscode(win) {
  const disposable = { dispose() {} };
  class EventEmitter {
    constructor() {
      this.fns = [];
      this.event = (fn) => {
        this.fns.push(fn);
        return disposable;
      };
    }
    fire(value) {
      this.fns.forEach((fn) => fn(value));
    }
    dispose() {}
  }
  win.onConfig = new EventEmitter();
  win.onFolders = new EventEmitter();
  const key = (section, name) => `${section}.${name}`;
  return {
    EventEmitter,
    StatusBarAlignment: { Left: 1, Right: 2 },
    ConfigurationTarget: { Global: 1 },
    ExtensionMode: { Production: 1, Development: 2, Test: 3 },
    env: { appRoot: APP_ROOT },
    ViewColumn: { Active: -1 },
    ThemeColor: class {
      constructor(id) {
        this.id = id;
      }
    },
    MarkdownString: class {
      constructor(value = '') {
        this.value = value;
      }
      appendMarkdown(text) {
        this.value += text;
        return this;
      }
    },
    RelativePattern: class {
      constructor(baseUri, pattern) {
        this.baseUri = baseUri;
        this.pattern = pattern;
      }
    },
    Uri: {
      from: (parts) => ({ ...parts, toString: () => `${parts.scheme}:${parts.path}` }),
      joinPath: (base, ...parts) => ({ scheme: 'file', fsPath: path.join(base.fsPath, ...parts) }),
    },
    window: {
      createStatusBarItem(...args) {
        const id = typeof args[0] === 'string' ? args.shift() : undefined;
        const [alignment, priority] = args;
        const item = {
          id,
          alignment,
          priority,
          text: '',
          tooltip: '',
          visible: false,
          show() {
            this.visible = true;
          },
          hide() {
            this.visible = false;
          },
          dispose() {},
        };
        win.items.push(item);
        return item;
      },
      showInformationMessage: (text, ...rest) => win.answer(text, rest),
      showWarningMessage: (text, ...rest) => win.answer(text, rest),
      showErrorMessage: (text, ...rest) => win.answer(text, rest),
      showWorkspaceFolderPick: async () => win.folders[0],
      setStatusBarMessage: (text) => {
        win.statusMessages.push(text);
        return disposable;
      },
      createWebviewPanel: (viewType, title) => {
        const handlers = [];
        win.panel = {
          viewType,
          title,
          webview: {
            html: '',
            onDidReceiveMessage: (fn) => {
              handlers.push(fn);
              return disposable;
            },
          },
          receive: (m) => handlers.forEach((fn) => fn(m)),
          onDidDispose: () => disposable,
          reveal() {},
          dispose() {},
        };
        return win.panel;
      },
    },
    workspace: {
      get workspaceFolders() {
        return win.folders;
      },
      onDidChangeConfiguration: win.onConfig.event,
      onDidChangeWorkspaceFolders: win.onFolders.event,
      onDidOpenTextDocument: () => disposable,
      onDidCloseTextDocument: () => disposable,
      onDidChangeTextDocument: () => disposable,
      getConfiguration: (section) => ({
        get: (name, fallback) => {
          const k = key(section, name);
          if (k in settings) return settings[k];
          return PROPS[k] && 'default' in PROPS[k] ? PROPS[k].default : fallback;
        },
        inspect: (name) => ({ globalValue: settings[key(section, name)] }),
        update: async (name, value) => {
          settings[key(section, name)] = value;
          win.onConfig.fire({ affectsConfiguration: (k) => k === key(section, name) });
        },
      }),
      findFiles: async (include, exclude) => {
        win.findFiles = [include, exclude];
        return win.files.map((fsPath) => ({ scheme: 'file', fsPath }));
      },
      createFileSystemWatcher: () => ({ onDidCreate: () => disposable, onDidDelete: () => disposable, dispose() {} }),
      asRelativePath: (uri) => path.relative(win.folders[0].uri.fsPath, uri.fsPath).split(path.sep).join('/'),
      registerTextDocumentContentProvider: (scheme, provider) => {
        win.provider = provider;
        return disposable;
      },
    },
    commands: {
      registerCommand: (id, fn) => {
        win.commands[id] = fn;
        return disposable;
      },
      executeCommand: async (id, ...args) => {
        win.executed.push([id, ...args]);
        if (id === 'setContext') win.keys[args[0]] = args[1];
        return undefined;
      },
    },
  };
}

let current;
const realLoad = Module._load;
Module._load = function load(request, ...rest) {
  return request === 'vscode' ? current.vscode : realLoad.call(this, request, ...rest);
};

function openWindow(options = {}) {
  const win = {
    items: [],
    commands: {},
    executed: [],
    keys: {},
    asked: [],
    statusMessages: [],
    answers: options.answers || {},
    folders: (options.folders || []).map((fsPath, index) => ({ uri: { scheme: 'file', fsPath }, name: path.basename(fsPath), index })),
    files: options.files || [],
  };
  win.answer = async (text) => {
    win.asked.push(text);
    const match = Object.keys(win.answers).find((k) => text.includes(k));
    return match ? win.answers[match] : undefined;
  };
  win.vscode = makeVscode(win);
  current = win;
  // Each window's extension host loads its own copy of the extension.
  Object.keys(require.cache)
    .filter((k) => k.startsWith(OUT))
    .forEach((k) => delete require.cache[k]);
  const extension = require(path.join(OUT, 'extension.js'));
  const context = {
    subscriptions: [],
    extensionMode: 1,
    extensionPath: options.extensionPath || NO_BUNDLE,
    logUri: { scheme: 'file', fsPath: path.join(USER_DATA, 'logs', '20261021T000000', 'window1', 'exthost', `${PACKAGE.publisher}.${PACKAGE.name}`) },
    extension: { id: `${PACKAGE.publisher}.${PACKAGE.name}`, packageJSON: PACKAGE },
    globalState: {
      get: (k) => globalState.get(k),
      update: async (k, v) => {
        globalState.set(k, v);
      },
    },
  };
  win.activation = Promise.resolve().then(() => extension.activate(context));
  win.close = () => context.subscriptions.forEach((d) => d.dispose());
  return win;
}

const badge = (win) => win.items.find((i) => i.priority === 100);
const health = (win) => win.items.find((i) => i.id === 'mastermind.health');

function reset({ config, lock, ports } = {}) {
  settings = {};
  globalState = new Map();
  exe.ports = new Set(ports || []);
  exe.cdpTargets = [];
  exe.lifecycle = false;
  exe.quits = [];
  spawned.length = 0;
  fs.rmSync(LOCK, { force: true });
  fs.rmSync(CONFIG, { force: true });
  if (config !== undefined) fs.writeFileSync(CONFIG, config);
  if (lock) {
    fs.writeFileSync(LOCK, '');
    if (lock === 'stale') {
      const old = new Date(Date.now() - 120000);
      fs.utimesSync(LOCK, old, old);
    }
  }
}

const checks = [];
function check(name, fn) {
  try {
    fn();
    checks.push(`ok   ${name}`);
  } catch (error) {
    checks.push(`FAIL ${name}: ${error.message}`);
  }
}

(async () => {
  // A — the extension loads in a window with no Mastermind running.
  reset();
  const a = openWindow();
  let error;
  try {
    await a.activation;
  } catch (e) {
    error = e;
  }
  await wait(50);
  check('the extension activates in a window with no Mastermind', () => assert.strictEqual(error, undefined));
  a.close();

  const proj = path.join(FIXTURES, 'proj');

  // B — no Mastermind, the lock free: one question after the rechecks; Start spawns the exe detached, then the login
  // question, once.
  reset({ config: JSON.stringify({ hookPort: 7778 }) });
  settings['mastermind.exePath'] = EXE;
  const b = openWindow({ folders: [proj], answers: { 'start it?': 'Start', 'log in?': 'Yes' } });
  await b.activation;
  await wait(150);
  check('no Mastermind: the badge reads Mastermind off, its click starts it', () => {
    assert.strictEqual(badge(b).text, '$(circle-slash) Mastermind off');
    assert.strictEqual(badge(b).command, 'mastermind.start');
  });
  check('no Mastermind: the window asks, and Start spawns Mastermind.exe detached and unref-ed', () => {
    assert.strictEqual(b.asked.filter((t) => t.includes('start it?')).length, 1);
    assert.strictEqual(spawned.length, 1);
    assert.strictEqual(spawned[0].file, EXE);
    assert.deepStrictEqual(spawned[0].options, { detached: true, stdio: 'ignore' });
    assert.ok(spawned[0].unrefed);
  });
  check('the first Start asks once about login; Yes sets startAtLogin', () => {
    assert.strictEqual(b.asked.filter((t) => t.includes('log in?')).length, 1);
    assert.strictEqual(settings['mastermind.startAtLogin'], true);
  });
  check('the ask lock is released once answered', () => assert.ok(!fs.existsSync(LOCK)));
  b.close();

  // C — a lock another window holds: no question. A lock older than a minute is taken over.
  reset({ lock: 'fresh' });
  const c = openWindow({ answers: { 'start it?': 'Start' } });
  await c.activation;
  await wait(150);
  check('a held ask lock: no question, Mastermind off', () => {
    assert.strictEqual(c.asked.length, 0);
    assert.strictEqual(badge(c).text, '$(circle-slash) Mastermind off');
  });
  c.close();
  reset({ lock: 'stale' });
  const c2 = openWindow({ answers: { 'start it?': "Don't ask again" } });
  await c2.activation;
  await wait(150);
  check('a stale lock is taken over and the window asks', () => assert.ok(c2.asked.some((t) => t.includes('start it?'))));
  check("Don't ask again turns askToStart off", () => assert.strictEqual(settings['mastermind.askToStart'], false));
  c2.close();

  // D — the import: config values copied into unset settings, once per profile, a byte-order mark dropped.
  const seeded = {
    hookPort: 7778,
    debugPort: 9222,
    startAtLogin: true,
    newSessionAutoAllow: true,
    handoffs: { enabled: true, outbox: 'tmp' },
    whatsappEfficiencyFix: true,
  };
  reset({ config: String.fromCharCode(0xfeff) + JSON.stringify(seeded), lock: 'fresh' });
  settings['mastermind.debugPort'] = 9333;
  const d = openWindow();
  await d.activation;
  check('the first run imports each unset mirrored key; a set one stands; the BOM is dropped', () => {
    assert.strictEqual(settings['mastermind.hookPort'], 7778);
    assert.strictEqual(settings['mastermind.debugPort'], 9333);
    assert.strictEqual(settings['mastermind.startAtLogin'], true);
    assert.strictEqual(settings['mastermind.newSessionAutoAllow'], true);
    assert.strictEqual(settings['mastermind.relay'], true);
    assert.ok(!Object.keys(settings).some((k) => k.includes('whatsapp')));
    assert.strictEqual(globalState.get('mastermind.imported'), true);
  });
  d.close();
  delete settings['mastermind.startAtLogin'];
  const d2 = openWindow();
  await d2.activation;
  check('the import runs once per profile: a key reset afterwards stays reset', () => assert.ok(!('mastermind.startAtLogin' in settings)));
  d2.close();
  reset({ config: '{ "hookPort": 7778, // a comment\n }', lock: 'fresh' });
  const d3 = openWindow();
  await d3.activation;
  check('a config that does not parse imports nothing and leaves the mark off', () => {
    assert.deepStrictEqual(Object.keys(settings), []);
    assert.strictEqual(globalState.get('mastermind.imported'), undefined);
  });
  d3.close();

  // D4 — while the import has not run, nothing is sent; once the config parses, the next connect imports, then sends.
  reset({ config: '{ "debugPort": 9222, // a comment\n }', ports: [7778] });
  const d4 = openWindow();
  await d4.activation;
  await wait(100);
  const d4s = exe.sockets[exe.sockets.length - 1];
  check('an import that cannot run holds the settings: the hello goes, no settings, one warning', () => {
    assert.strictEqual(d4s.sent[0].type, 'hello');
    assert.ok(!d4s.sent.some((m) => m.type === 'settings'));
    assert.strictEqual(d4.asked.filter((t) => t.includes('not sent until')).length, 1);
  });
  fs.writeFileSync(CONFIG, JSON.stringify({ debugPort: 9222, startAtLogin: true }));
  d4s.close();
  await wait(150);
  check('the config fixed, the next connect imports first, then sends what it imported', () => {
    const s = exe.sockets[exe.sockets.length - 1].last('settings');
    assert.ok(s);
    assert.strictEqual(s.values.debugPort, 9222);
    assert.strictEqual(s.values.startAtLogin, true);
    assert.strictEqual(globalState.get('mastermind.imported'), true);
  });
  d4.close();

  // E — Mastermind up: the hello, then the settings marked as a connect's; the badge reads the count and the eye.
  reset({ config: JSON.stringify({ hookPort: 7778 }), ports: [7778] });
  const e = openWindow({ folders: [proj] });
  await e.activation;
  await wait(100);
  const es = exe.sockets[exe.sockets.length - 1];
  check("connected: the hello names this host, its folders, the version and the editor; the connect's settings follow", () => {
    assert.strictEqual(es.port, 7778);
    assert.deepStrictEqual(es.sent[0], {
      type: 'hello',
      hostPid: process.pid,
      folders: [proj],
      version: PACKAGE.version,
      env: es.sent[0].env,
      health: true,
      editor: { processName: path.parse(process.execPath).name, userDataDir: USER_DATA, cli: EDITOR_CLI },
    });
    assert.strictEqual(es.sent[1].type, 'settings');
    assert.strictEqual(es.sent[1].connect, true);
    assert.deepStrictEqual(Object.keys(es.sent[1].values).sort(), ['debugPort', 'handoffs.enabled', 'hookPort', 'newSessionAutoAllow', 'startAtLogin']);
    assert.strictEqual(es.sent[1].values.debugPort, 0);
  });
  const finished = { title: 'b', project: 'FE', status: 'FINISHED', state: 'finished' };
  es.receive({ type: 'needs', count: 2, items: [{ title: 'a', project: 'BE', status: 'NEEDS PERMISSION', state: 'permission' }, finished] });
  es.receive({ type: 'watched', folders: [{ root: proj, state: 'watched' }] });
  check('the badge: an open eye and the count, coloured while one waits on a permission; a click opens the panel', () => {
    assert.strictEqual(badge(e).text, '$(eye) 2');
    assert.strictEqual(badge(e).command, 'mastermind.show');
    assert.ok(badge(e).tooltip.value.includes('2 need you'));
    assert.strictEqual(badge(e).backgroundColor.id, 'statusBarItem.warningBackground');
  });
  es.receive({ type: 'watched', folders: [{ root: proj, state: 'off' }] });
  es.receive({ type: 'needs', count: 0, items: [] });
  check('a folder not watched closes the eye; a zero count shows the eye alone', () => {
    assert.strictEqual(badge(e).text, '$(eye-closed)');
    assert.ok(badge(e).tooltip.value.includes('not watched'));
  });
  es.receive({ type: 'needs', count: 1, items: [finished] });
  check('a count with nobody on a permission leaves the badge uncoloured', () => {
    assert.strictEqual(badge(e).text, '$(eye-closed) 1');
    assert.strictEqual(badge(e).backgroundColor, undefined);
  });
  // The projects page: the command asks the exe for the rows and renders them; a changed code asks first,
  // then the save sends the rows back.
  const projectsOpen = e.commands['mastermind.projects']();
  await wait(20);
  const get = es.sent[es.sent.length - 1];
  es.receive({
    type: 'reply',
    id: get.id,
    ok: true,
    projects: [
      { root: proj, name: path.basename(proj), token: 'FE', label: '', hide: false, manifest: false },
      { root: path.join(FIXTURES, 'wired'), name: 'wired', token: 'BE', label: '', hide: false, manifest: true },
    ],
  });
  await projectsOpen;
  check('projects: the command asks the exe for the rows and renders them, the manifest one locked', () => {
    assert.strictEqual(get.type, 'projects.get');
    assert.ok(e.panel && e.panel.webview.html.includes('"manifest":true') && e.panel.webview.html.includes('"token":"FE"'));
  });
  e.answers['Save?'] = 'Save';
  e.panel.receive({ type: 'save', rows: [{ root: proj, name: path.basename(proj), token: 'FE2', label: '', hide: false, manifest: false }] });
  await wait(20);
  const saved = es.sent[es.sent.length - 1];
  check('projects: a changed code asks first, then the save sends the rows', () => {
    assert.ok(e.asked.some((t) => t.includes('UNKNOWN DEST') && t.includes('FE → FE2')));
    assert.strictEqual(saved.type, 'projects.save');
    assert.strictEqual(saved.projects[0].token, 'FE2');
  });
  es.receive({ type: 'reply', id: saved.id, ok: true, restart: true });
  await wait(20);

  // F — a mirrored setting changed twice in a row: one send once the typing settles, with the last value, unmarked;
  // claudeDir rides it only while CLAUDE_CONFIG_DIR is set.
  process.env.CLAUDE_CONFIG_DIR = path.join(FIXTURES, 'claude-home');
  await e.vscode.workspace.getConfiguration('mastermind').update('debugPort', 9222);
  await e.vscode.workspace.getConfiguration('mastermind').update('debugPort', 9223);
  await wait(80);
  delete process.env.CLAUDE_CONFIG_DIR;
  check('a setting change sends once the typing settles, with the last value', () => {
    const sends = es.sent.filter((m) => m.type === 'settings');
    assert.strictEqual(sends.length, 2);
    assert.strictEqual(sends[1].values.debugPort, 9223);
    assert.strictEqual(sends[1].connect, false);
  });
  check('claudeDir goes out only while CLAUDE_CONFIG_DIR is set', () => {
    const sends = es.sent.filter((m) => m.type === 'settings');
    assert.ok(!('claudeDir' in sends[0].values));
    assert.strictEqual(sends[1].values.claudeDir, path.join(FIXTURES, 'claude-home'));
  });

  // G — a call: the undefined markers rebuilt; a command off the list refused, never run.
  const marker = { $undefined: true };
  es.receive({
    type: 'call',
    id: 7,
    command: 'claude-vscode.editor.open',
    args: ['sid-1', marker, marker, marker, marker, { programmatic: 'honor-preferred-location' }],
  });
  es.receive({ type: 'call', id: 8, command: 'workbench.action.terminal.sendSequence', args: [{ text: 'x' }] });
  await wait(30);
  check('a call runs editor.open with its unused positions undefined and answers ok', () => {
    assert.deepStrictEqual(
      e.executed.find(([id]) => id === 'claude-vscode.editor.open'),
      ['claude-vscode.editor.open', 'sid-1', undefined, undefined, undefined, undefined, { programmatic: 'honor-preferred-location' }]
    );
    assert.deepStrictEqual(
      es.sent.find((m) => m.type === 'result' && m.id === 7),
      { type: 'result', id: 7, ok: true }
    );
  });
  check('a call switching to a session never reveals the side bar', () =>
    assert.ok(!e.executed.some(([id]) => id === 'workbench.action.focusAuxiliaryBar'))
  );
  settings['claudeCode.preferredLocation'] = 'sidebar';
  es.receive({ type: 'call', id: 9, command: 'claude-vscode.editor.open', args: [marker, 'hi', marker, marker, marker, { programmatic: 'honor-preferred-location' }] });
  await wait(30);
  check('a new-session call reveals the side bar after editor.open when Claude Code opens sessions there', () => {
    const at = (cmd) => e.executed.findIndex(([id, sid]) => id === cmd && (cmd !== 'claude-vscode.editor.open' || sid === undefined));
    assert.ok(at('claude-vscode.editor.open') >= 0 && at('workbench.action.focusAuxiliaryBar') > at('claude-vscode.editor.open'));
    assert.deepStrictEqual(
      es.sent.find((m) => m.type === 'result' && m.id === 9),
      { type: 'result', id: 9, ok: true }
    );
  });
  delete settings['claudeCode.preferredLocation'];
  check('a call naming any other command is refused and never run', () => {
    assert.ok(!e.executed.some(([id]) => id === 'workbench.action.terminal.sendSequence'));
    assert.strictEqual(es.sent.find((m) => m.type === 'result' && m.id === 8).ok, false);
  });

  // H — watch: the proposal's diff before the question, then Apply sends the apply.
  e.answers["Write Mastermind's hook entries"] = 'Apply';
  const watching = e.commands['mastermind.watch']();
  await wait(30);
  const propose = es.last('hooks.propose');
  es.receive({ type: 'reply', id: propose.id, ok: true, proposalId: 'p1', root: proj, before: '{}\n', after: '{\n  "hooks": {}\n}\n', warnings: [] });
  await wait(30);
  const apply = es.last('hooks.apply');
  if (apply) es.receive({ type: 'reply', id: apply.id, ok: true });
  await watching;
  check('watch: the exe proposes for this folder and the diff opens on both texts', () => {
    assert.deepStrictEqual({ root: propose.root, remove: propose.remove }, { root: proj, remove: false });
    const diff = e.executed.find(([id]) => id === 'vscode.diff');
    assert.ok(diff);
    assert.strictEqual(e.provider.provideTextDocumentContent(diff[1]), '{}\n');
    assert.strictEqual(e.provider.provideTextDocumentContent(diff[2]), '{\n  "hooks": {}\n}\n');
  });
  check('watch: Apply sends the apply for that proposal', () => assert.strictEqual(apply && apply.proposalId, 'p1'));

  // H2 — stop watching with the notification left unanswered: the diff's title-bar Apply applies the proposal.
  const unwatching = e.commands['mastermind.unwatch']();
  await wait(30);
  const propose2 = es.last('hooks.propose');
  es.receive({ type: 'reply', id: propose2.id, ok: true, proposalId: 'p2', root: proj, before: '{\n  "hooks": {}\n}\n', after: '{}\n', warnings: [] });
  await wait(30);
  const diff2 = e.executed.filter(([id]) => id === 'vscode.diff').pop();
  e.commands['mastermind.applyProposal'](diff2[2]);
  await wait(30);
  const apply2 = es.last('hooks.apply');
  if (apply2 && apply2.proposalId === 'p2') {
    es.receive({ type: 'reply', id: apply2.id, ok: true });
    await unwatching;
  }
  check("stop watching: the diff's title-bar Apply applies what the notification left open", () => {
    assert.strictEqual(propose2.remove, true);
    assert.strictEqual(apply2 && apply2.proposalId, 'p2');
  });
  check('the title-bar Apply shows on a proposal diff only', () =>
    assert.deepStrictEqual(PACKAGE.contributes.menus['editor/title'], [
      { command: 'mastermind.applyProposal', when: 'resourceScheme == mastermind-proposal', group: 'navigation' },
    ])
  );
  e.close();

  // I — the port fallback: mastermind.hookPort dead, the config's port alive.
  reset({ config: JSON.stringify({ hookPort: 7778 }), ports: [7778] });
  globalState.set('mastermind.imported', true);
  settings['mastermind.hookPort'] = 7790;
  const i = openWindow();
  await i.activation;
  await wait(100);
  check("mastermind.hookPort dead: the shell connects on the config's port and sends the new one", () => {
    const socket = exe.sockets[exe.sockets.length - 1];
    assert.strictEqual(socket.port, 7778);
    assert.strictEqual(socket.last('settings').values.hookPort, 7790);
  });
  i.close();

  // J — the explorer item: only the include's fixed folder is searched; the command types the template in.
  reset({ lock: 'fresh' });
  globalState.set('mastermind.imported', true);
  settings['mastermind.promptItem'] = { include: 'docs/*.md', exclude: '**/archive.md', template: '/review {path}' };
  const draft = path.join(proj, 'docs', 'x.md');
  const j = openWindow({ folders: [proj], files: [draft] });
  await j.activation;
  await wait(30);
  check('the prompt item: the fixed folder alone is searched, and the matches go into mastermind.promptFiles', () => {
    const [include, exclude] = j.findFiles;
    assert.strictEqual(include.baseUri.fsPath, path.join(proj, 'docs'));
    assert.strictEqual(include.pattern, '*.md');
    assert.strictEqual(exclude, '**/archive.md');
    assert.deepStrictEqual(j.keys['mastermind.promptFiles'], [draft]);
  });
  j.commands['mastermind.promptItem']({ scheme: 'file', fsPath: draft });
  check('the prompt item: a new session in the preferred location, the template typed in', () =>
    assert.deepStrictEqual(
      j.executed.find(([id]) => id === 'claude-vscode.editor.open'),
      ['claude-vscode.editor.open', undefined, '/review docs/x.md', undefined, undefined, undefined, { programmatic: 'honor-preferred-location' }]
    )
  );
  await wait(30);
  check("the prompt item leaves the side bar alone under Claude Code's default, an editor tab", () =>
    assert.ok(!j.executed.some(([id]) => id === 'workbench.action.focusAuxiliaryBar'))
  );
  settings['claudeCode.preferredLocation'] = 'sidebar';
  j.commands['mastermind.promptItem']({ scheme: 'file', fsPath: draft });
  await wait(30);
  check('the prompt item reveals the side bar when Claude Code opens sessions there', () =>
    assert.ok(j.executed.some(([id]) => id === 'workbench.action.focusAuxiliaryBar'))
  );
  check('the explorer menu shows the item only on a file in mastermind.promptFiles', () =>
    assert.strictEqual(PACKAGE.contributes.menus['explorer/context'][0].when, 'resourceScheme == file && resourcePath in mastermind.promptFiles')
  );
  j.close();

  // K — the walkthrough's debug-port step ticks once the port answers as VS Code.
  reset({ lock: 'fresh', ports: [9222] });
  globalState.set('mastermind.imported', true);
  settings['mastermind.debugPort'] = 9222;
  exe.cdpTargets = [{ type: 'page', url: 'vscode-file://vscode-app/c:/x/workbench.html' }];
  const k = openWindow();
  await k.activation;
  await wait(30);
  check('the walkthrough: a vscode-file page on the debug port sets mastermind.debugPortReady', () =>
    assert.strictEqual(k.keys['mastermind.debugPortReady'], true)
  );
  check('the walkthrough: the debug-port step is also marked complete by its id', () =>
    assert.deepStrictEqual(
      k.executed.find(([id]) => id === 'welcome.markStepComplete'),
      ['welcome.markStepComplete', 'eventigizer.mastermind#setup#debugPort']
    )
  );
  k.close();

  // L — VS Code health: hidden under its warning share; the worst offender past it; the automatic restart inside the
  // quiet wait and the cooldown, on the item's own 5 s clock; the panel's reload refused while busy; the levels'
  // margin; the switch.
  reset({ ports: [7778] });
  globalState.set('mastermind.imported', true);
  offset = 0;
  const l = openWindow({ folders: [proj] });
  await l.activation;
  await wait(100);
  const ls = exe.sockets[exe.sockets.length - 1];
  const off = { state: 'off', mb: -1, limitMb: 4192, error: null };
  const ts = (pid, mb) => [{ kind: 'typescript', pid, mb, limitMb: 3072 }];
  const tsRestarts = () => l.executed.filter(([id]) => id === 'typescript.restartTsServer').length;
  // The exe's fresh answer to the shell's last health.busy request.
  const answerBusy = async (busy, why = null) => {
    ls.receive({ type: 'reply', id: ls.last('health.busy').id, ok: true, busy, why });
    await wait(20);
  };
  ls.receive({ type: 'health', servers: ts(10, 1000), panel: off, busy: false, error: null });
  check('health: every reading under its warning share hides the item', () => assert.strictEqual(health(l).visible, false));
  ls.receive({ type: 'health', servers: ts(10, 2400), panel: off, busy: false, error: null });
  check('health: past its error share the item names the worst offender, red, and reports error', () => {
    assert.strictEqual(health(l).text, '$(pulse) TypeScript 2.3 GB');
    assert.strictEqual(health(l).backgroundColor.id, 'statusBarItem.errorBackground');
    assert.strictEqual(ls.last('health.level').level, 'error');
  });
  check('health: without the debug port the panel row offers the walkthrough step', () => {
    assert.ok(health(l).tooltip.value.includes('needs the debug port'));
    assert.ok(health(l).tooltip.value.includes('command:mastermind.health.debugPortStep'));
  });
  check('health: no automatic restart inside the quiet wait', () => assert.strictEqual(tsRestarts(), 0));
  offset += 31000;
  tick(5000);
  await wait(20);
  check('health: an automatic restart asks the exe first and waits for its answer', () => {
    assert.ok(ls.last('health.busy'));
    assert.strictEqual(tsRestarts(), 0);
  });
  await answerBusy(false);
  check('health: quiet past the wait in the error band and idle, its clock restarts it once and shows it restarting', () => {
    assert.strictEqual(tsRestarts(), 1);
    assert.ok(health(l).text.startsWith('$(sync~spin)'));
    assert.ok(!l.statusMessages.some((t) => t.includes('restarted automatically')));
  });
  ls.receive({ type: 'health', servers: ts(11, 2400), panel: off, busy: false, error: null });
  check('health: the new pid ends the restart, and only then the status bar says it restarted automatically', () => {
    assert.ok(health(l).text.startsWith('$(pulse)'));
    assert.ok(l.statusMessages.some((t) => t.includes('TypeScript restarted automatically at 2.3 GB')));
  });
  offset += 31000;
  tick(5000);
  await wait(20);
  check('health: a second automatic restart waits out the cooldown', () => assert.strictEqual(tsRestarts(), 1));
  offset += 10 * 60000;
  tick(5000);
  await wait(20);
  await answerBusy(true, 'a session is working');
  check('health: a busy answer holds the automatic restart', () => assert.strictEqual(tsRestarts(), 1));
  tick(5000);
  await wait(20);
  await answerBusy(false);
  check('health: past the cooldown and idle it restarts again', () => assert.strictEqual(tsRestarts(), 2));
  const panel = { state: 'ok', mb: 3500, limitMb: 4192, error: null };
  ls.receive({ type: 'health', servers: [], panel, busy: true, why: 'a session is working', error: null });
  check('health: in a busy window the panel item keeps its label and its click, and the tooltip says why a reload waits', () => {
    assert.strictEqual(health(l).text, '$(pulse) Claude panel 3.4 GB');
    assert.strictEqual(health(l).command, 'mastermind.health.free');
    assert.ok(health(l).tooltip.value.includes('reload once every agent'));
    assert.ok(health(l).tooltip.value.includes('a session is working'));
  });
  const refused = l.commands['mastermind.health.free']();
  await wait(20);
  await answerBusy(true, 'a session is working');
  await refused;
  check('health: the command run anyway asks the exe, never reloads a busy window, and answers in a dialog', () => {
    assert.ok(!l.executed.some(([id]) => id === 'workbench.action.webview.reloadWebviewAction'));
    assert.ok(l.asked.includes('Not reloaded'));
  });
  ls.receive({ type: 'health', servers: [], panel: { ...panel, sharedWith: ['webapp'] }, busy: false, why: null, error: null });
  check('health: a panel sharing its isolate names the other windows, since its reload frees only its share', () =>
    assert.ok(health(l).tooltip.value.includes('shared with webapp — it frees fully only once each of them reloads too'))
  );
  const freeing = l.commands['mastermind.health.free']();
  await wait(20);
  await answerBusy(false);
  await freeing;
  check('health: an idle window tells the exe, then reloads its Claude panels on the click', () => {
    assert.ok(ls.last('health.reloaded'));
    assert.ok(l.executed.some(([id]) => id === 'workbench.action.webview.reloadWebviewAction'));
  });
  // A level falls only 5 points of its ceiling below the share it rose at — the panel above sat in its error band.
  const panelAt = (mb) => ls.receive({ type: 'health', servers: [], panel: { ...panel, mb }, busy: false, why: null, error: null });
  panelAt(3000);
  check('health: a reading just under the error share holds the error band', () =>
    assert.strictEqual(health(l).backgroundColor.id, 'statusBarItem.errorBackground'));
  panelAt(2400);
  check('health: past the margin it falls to the warning band, which a reading just under its share holds shown', () => {
    assert.strictEqual(health(l).visible, true);
    assert.strictEqual(health(l).backgroundColor.id, 'statusBarItem.warningBackground');
  });
  panelAt(2200);
  check('health: past the margin under the warning share the item hides', () => assert.strictEqual(health(l).visible, false));
  panelAt(2450);
  check('health: rising again, it stays hidden under the warning share', () => assert.strictEqual(health(l).visible, false));
  panelAt(2520);
  check('health: and shows at the warning share', () => assert.strictEqual(health(l).visible, true));
  await l.vscode.workspace.getConfiguration('mastermind.health').update('enabled', false);
  check('health: the switch off goes to the exe and hides the item', () => {
    assert.deepStrictEqual(ls.last('health.switch'), { type: 'health.switch', on: false });
    assert.strictEqual(health(l).visible, false);
  });
  offset = 0;
  l.close();

  // M — the bundled copy: a package carrying bin/app installs it into the app folder at Start, swaps a newer one in at
  // activation, and leaves the app folder alone under exePath. The fake exe's lifecycle is on throughout.
  const APP_EXE = path.join(INSTALLED, 'Mastermind.exe');
  const build = (dir, version) => {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'Mastermind.exe'), '');
    fs.writeFileSync(path.join(dir, 'version.txt'), version);
  };
  const versionIn = (dir) => fs.readFileSync(path.join(dir, 'version.txt'), 'utf8').trim();
  const bundled = (bundle, installed) => {
    reset({ config: JSON.stringify({ hookPort: 7778 }) });
    for (const dir of [BUNDLE, INSTALLED, `${INSTALLED}.new`, `${INSTALLED}.old`]) fs.rmSync(dir, { recursive: true, force: true });
    fs.rmSync(path.join(APP_DIR, 'update.lock'), { force: true });
    build(path.join(BUNDLE, 'bin', 'app'), bundle);
    if (installed) build(INSTALLED, installed);
    exe.lifecycle = true;
  };
  // The install's waits run on the wall clock, so a check waits on the outcome, never a fixed delay.
  const settle = async (test, ms = 5000) => {
    const end = realNow() + ms;
    while (!test() && realNow() < end) await wait(20);
  };

  bundled('1.0.1', null);
  const m1 = openWindow({ extensionPath: BUNDLE, folders: [proj], answers: { 'start it?': 'Start', 'log in?': 'No' } });
  await m1.activation;
  await settle(() => spawned.length > 0);
  check('bundled, nothing installed: Start copies bin/app into the app folder and spawns its Mastermind.exe', () => {
    assert.strictEqual(versionIn(INSTALLED), '1.0.1');
    assert.strictEqual(spawned.length, 1);
    assert.strictEqual(spawned[0].file, APP_EXE);
    assert.deepStrictEqual(spawned[0].args, []);
  });
  m1.close();

  bundled('1.0.1', '1.0.0');
  exe.ports.add(7778);
  const m2 = openWindow({ extensionPath: BUNDLE, folders: [proj] });
  await m2.activation;
  await settle(() => !fs.existsSync(`${INSTALLED}.old`) && m2.asked.some((t) => t.includes('updated to')));
  check('bundled newer, Mastermind running: the quit goes over the open channel, the swap lands, the new build starts in the background', () => {
    assert.deepStrictEqual(exe.quits, [{ open: true }]);
    assert.strictEqual(versionIn(INSTALLED), '1.0.1');
    assert.strictEqual(spawned.length, 1);
    assert.strictEqual(spawned[0].file, APP_EXE);
    assert.deepStrictEqual(spawned[0].args, ['--background']);
    assert.ok(!fs.existsSync(`${INSTALLED}.old`));
    assert.ok(m2.asked.includes('Mastermind updated to 1.0.1'));
  });
  m2.close();

  bundled('1.0.1', '1.0.0');
  settings['mastermind.exePath'] = EXE;
  const m3 = openWindow({ extensionPath: BUNDLE, folders: [proj], answers: { 'start it?': 'Start', 'log in?': 'No' } });
  await m3.activation;
  await settle(() => spawned.length > 0);
  await wait(100); // an update running anyway would have swapped by now
  check('exePath set: neither the install nor the update touches the app folder, and Start spawns the set path', () => {
    assert.strictEqual(spawned.length, 1);
    assert.strictEqual(spawned[0].file, EXE);
    assert.strictEqual(versionIn(INSTALLED), '1.0.0');
    assert.ok(!fs.existsSync(`${INSTALLED}.new`) && !fs.existsSync(`${INSTALLED}.old`));
    assert.ok(!m3.asked.some((t) => t.includes('updated to')));
  });
  m3.close();
  bundled('1.0.1', null);
  const missing = path.join(FIXTURES, 'missing', 'Mastermind.exe');
  settings['mastermind.exePath'] = missing;
  const m3b = openWindow({ extensionPath: BUNDLE, folders: [proj], answers: { 'start it?': 'Start', 'log in?': 'No' } });
  await m3b.activation;
  await settle(() => m3b.asked.some((t) => t.includes('no Mastermind.exe at')));
  check("exePath set to a missing file: Start's error names that path, and nothing is installed or spawned", () => {
    assert.ok(m3b.asked.some((t) => t.includes(`no Mastermind.exe at "${missing}"`)));
    assert.strictEqual(spawned.length, 0);
    assert.ok(!fs.existsSync(INSTALLED));
  });
  m3b.close();

  bundled('1.0.1', '1.0.0');
  const m4 = openWindow({ extensionPath: BUNDLE, folders: [proj], answers: { 'start it?': 'Not now', 'log in?': 'No' } });
  await m4.activation;
  await settle(() => m4.asked.some((t) => t.includes('updated to')));
  check('bundled newer, nothing running: the update swaps with no spawn and keeps app.old', () => {
    assert.strictEqual(versionIn(INSTALLED), '1.0.1');
    assert.strictEqual(spawned.length, 0);
    assert.strictEqual(versionIn(`${INSTALLED}.old`), '1.0.0');
  });
  await m4.commands['mastermind.start']();
  await settle(() => !fs.existsSync(`${INSTALLED}.old`));
  check("the next Start spawns the app folder's Mastermind.exe, and once it answers app.old is gone", () => {
    assert.strictEqual(spawned.length, 1);
    assert.strictEqual(spawned[0].file, APP_EXE);
    assert.ok(!fs.existsSync(`${INSTALLED}.old`));
  });
  m4.close();
  exe.lifecycle = false;

  checks.forEach((line) => console.log(line));
  const failed = checks.filter((line) => line.startsWith('FAIL')).length;
  console.log(failed === 0 ? `PASS ${checks.length}/${checks.length}` : `FAILED ${failed} of ${checks.length}`);
  process.exit(failed === 0 ? 0 : 1);
})();
