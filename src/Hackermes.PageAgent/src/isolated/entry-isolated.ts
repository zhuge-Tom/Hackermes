import { createTransport } from '../shared/transport';
import { installRecordingHook } from './record-hook';

/**
 * 命名隔离世界入口。这里只读 DOM 与捕获用户事件,不替换页面的全局 API。
 * 宿主通过 Page.addScriptToEvaluateOnNewDocument.worldName 创建该世界,
 * 并用 Runtime.addBinding.executionContextName 只向该世界暴露回传通道。
 */

declare const __HACKERMES_BINDING__: string;

(function bootstrap(): void {
  const bindingName = typeof __HACKERMES_BINDING__ === 'string' ? __HACKERMES_BINDING__ : '__hackermes_iso__';
  const flag = '__hackermes_isolated_installed__';
  const global = globalThis as Record<string, unknown>;

  if (global[flag]) {
    return;
  }

  global[flag] = true;
  const transport = createTransport(bindingName);

  try {
    installRecordingHook(transport);
    transport.send({
      t: 'lifecycle',
      k: 'ready',
      world: 'isolated',
      url: location.href,
      ts: Date.now(),
    });
  } catch (error) {
    transport.send({ t: 'lifecycle', k: 'error', world: 'isolated', error: String(error) });
  }
})();
