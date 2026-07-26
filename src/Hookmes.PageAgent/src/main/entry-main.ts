import { createTransport } from '../shared/transport';
import { installFetchHook, installWebSocketHook, installXhrHook } from './net-hook';
import { installCookieHook, installStorageHook } from './storage-hook';
import { installRouteHook } from './route-hook';

/**
 * 主世界入口。
 *
 * 为什么这部分必须在主世界:hook 的目标是页面实际使用的 fetch / XMLHttpRequest /
 * localStorage 等对象。隔离世界有独立的全局对象,在其中包装这些 API 对页面代码毫无影响。
 *
 * 代价是页面能检测到、能绕过、也能反过来篡改本脚本。应对办法是把主世界部分做到最小:
 * 只做 hook 与上报,不含任何业务逻辑。真正的分析放在宿主侧。
 *
 * 绑定名由宿主在注入时替换 __HOOKMES_BINDING__ 占位符,每次会话随机化,
 * 减少被特征识别的可能。
 */

declare const __HOOKMES_BINDING__: string;

(function bootstrap(): void {
  const bindingName = typeof __HOOKMES_BINDING__ === 'string' ? __HOOKMES_BINDING__ : '__hookmes__';
  const flag = '__hookmes_main_installed__';
  const global = globalThis as Record<string, unknown>;

  // 同一文档可能被重复注入(例如宿主重连),幂等保护。
  if (global[flag]) {
    return;
  }

  global[flag] = true;

  const transport = createTransport(bindingName);

  try {
    installFetchHook(transport);
    installXhrHook(transport);
    installWebSocketHook(transport);
    installStorageHook(transport);
    installCookieHook(transport);
    installRouteHook(transport);

    transport.send({
      t: 'lifecycle',
      k: 'ready',
      world: 'main',
      url: location.href,
      ts: Date.now(),
    });
  } catch (error) {
    // 任何一步失败都不能让页面脚本受影响 —— 宁可少一些观测能力。
    transport.send({ t: 'lifecycle', k: 'error', world: 'main', error: String(error) });
  }
})();
