import { captureStack, type Transport } from '../shared/transport';

/**
 * 前端路由 hook。
 *
 * SPA 的"页面跳转"多数不产生真实导航,CDP 的 Page.frameNavigated 不会触发。
 * 没有这个 hook,调试单页应用时时间线上会完全看不到路由变化。
 */
export function installRouteHook(transport: Transport): void {
  const history = globalThis.history;
  if (!history) {
    return;
  }

  const report = (kind: string, url: unknown): void => {
    transport.send({
      t: 'route',
      k: kind,
      url: url == null ? location.href : String(url),
      href: location.href,
      stack: captureStack(),
      ts: Date.now(),
    });
  };

  const originalPush = history.pushState;
  const originalReplace = history.replaceState;

  history.pushState = function pushState(this: History, ...args: Parameters<History['pushState']>) {
    const result = originalPush.apply(this, args);
    report('push', args[2]);
    return result;
  };

  history.replaceState = function replaceState(this: History, ...args: Parameters<History['replaceState']>) {
    const result = originalReplace.apply(this, args);
    report('replace', args[2]);
    return result;
  };

  // 捕获阶段监听,尽量早于页面自己的处理器。
  globalThis.addEventListener('popstate', () => report('pop', location.href), true);
  globalThis.addEventListener('hashchange', () => report('hash', location.href), true);
}
