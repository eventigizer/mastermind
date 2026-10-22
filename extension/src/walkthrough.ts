import * as vscode from 'vscode';

const POLL_MS = 15000;
const READY_KEY = 'mastermind.debugPortReady';

/**
 * The walkthrough's debug-port step ticks once mastermind.debugPort answers as VS Code by the exe's own rule (CdpClient):
 * a vscode-webview:// target, or a page target at a vscode-file:// URL (each window's workbench) in /json/list —
 * /json/version's Browser reads Chrome for VS Code too, and a backgrounded sidebar's webview can drop out of CDP while
 * the workbench page does not. The context key alone misses a session whose walkthrough service starts after the key
 * is set — the service files an already-true key's event without ticking the step, and the key's later changes no-op —
 * so the step is also marked complete directly, each poll until a call goes through: welcome.markStepComplete throws
 * until the service has registered this extension's walkthrough.
 */
export function wireWalkthrough(context: vscode.ExtensionContext): void {
  const stepId = `${context.extension.id}#setup#debugPort`;
  let ready = false;
  let marked = false;
  let timer: NodeJS.Timeout | undefined;
  const check = async (): Promise<void> => {
    const port = vscode.workspace.getConfiguration('mastermind').get<number>('debugPort', 0);
    if (!ready && port > 0) {
      try {
        const response = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(1500) });
        const targets: unknown = await response.json();
        ready =
          Array.isArray(targets) &&
          targets.some(
            (t) => typeof t?.url === 'string' && (t.url.startsWith('vscode-webview://') || (t.type === 'page' && t.url.startsWith('vscode-file://')))
          );
        if (ready) await vscode.commands.executeCommand('setContext', READY_KEY, true);
      } catch {
        ready = false;
      }
    }
    if (ready && !marked) {
      try {
        await vscode.commands.executeCommand('welcome.markStepComplete', stepId);
        marked = true;
        clearInterval(timer); // nothing is left to tick
      } catch {
        // the walkthrough isn't registered yet — the next poll marks it
      }
    }
  };
  void check();
  timer = setInterval(() => void check(), POLL_MS);
  context.subscriptions.push({ dispose: () => clearInterval(timer) });
}
