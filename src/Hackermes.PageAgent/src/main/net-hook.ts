import { captureStack, clip, nextId, type Transport } from '../shared/transport';

/**
 * 网络 API hook。
 *
 * 为什么在有 CDP Network 域的情况下还要 hook:协议层能看到请求本身,
 * 但看不到**是哪段代码发起的**。调试"这个请求为什么发了两次"时,调用栈才是关键信息。
 *
 * 透明性要求(违反任何一条都算 bug,不是可接受的副作用):
 *   - 保留原函数引用并正确转发 this 与全部参数
 *   - 异常原样抛出,不吞不包装
 *   - toString() 伪装成原生函数
 *   - 保持属性描述符与原型链
 */

/** 让包装函数在 toString() 时看起来仍是原生实现。 */
function disguise(wrapper: (...args: never[]) => unknown, original: (...args: never[]) => unknown): void {
  try {
    Object.defineProperty(wrapper, 'name', { value: original.name, configurable: true });
    Object.defineProperty(wrapper, 'length', { value: original.length, configurable: true });

    const nativeSource = 'function ' + original.name + '() { [native code] }';
    Object.defineProperty(wrapper, 'toString', {
      value: function toString() {
        return nativeSource;
      },
      writable: true,
      configurable: true,
    });
  } catch {
    // 伪装失败不影响 hook 功能本身。
  }
}

export function installFetchHook(transport: Transport): void {
  const original = globalThis.fetch;
  if (typeof original !== 'function') {
    return;
  }

  const wrapper = function fetch(this: unknown, ...args: Parameters<typeof original>) {
    const id = nextId('f');
    const started = Date.now();

    let url = '';
    let method = 'GET';

    try {
      const [input, init] = args;
      url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      method = init?.method ?? (input instanceof Request ? input.method : 'GET');
    } catch {
      // 参数形态千奇百怪,取不到就留空,不能因此让请求失败。
    }

    transport.send({
      t: 'net',
      k: 'fetch',
      phase: 'start',
      id,
      url,
      method: method.toUpperCase(),
      stack: captureStack(),
      ts: started,
    });

    let result: Promise<Response>;

    try {
      result = original.apply(this as never, args);
    } catch (error) {
      transport.send({
        t: 'net',
        k: 'fetch',
        phase: 'error',
        id,
        url,
        error: String(error),
        durationMs: Date.now() - started,
      });
      throw error;
    }

    return result.then(
      (response) => {
        transport.send({
          t: 'net',
          k: 'fetch',
          phase: 'end',
          id,
          url,
          status: response.status,
          ok: response.ok,
          durationMs: Date.now() - started,
        });
        return response;
      },
      (error) => {
        transport.send({
          t: 'net',
          k: 'fetch',
          phase: 'error',
          id,
          url,
          error: String(error),
          durationMs: Date.now() - started,
        });
        throw error;
      },
    );
  };

  disguise(wrapper as never, original as never);
  globalThis.fetch = wrapper as typeof original;
}

interface HookedXhr extends XMLHttpRequest {
  __hackermes?: { id: string; method: string; url: string; started: number; stack?: string };
}

export function installXhrHook(transport: Transport): void {
  const proto = XMLHttpRequest?.prototype;
  if (!proto) {
    return;
  }

  const originalOpen = proto.open;
  const originalSend = proto.send;

  const openWrapper = function open(this: HookedXhr, ...args: Parameters<XMLHttpRequest['open']>) {
    try {
      this.__hackermes = {
        id: nextId('x'),
        method: String(args[0] ?? 'GET').toUpperCase(),
        url: String(args[1] ?? ''),
        started: 0,
        stack: captureStack(),
      };
    } catch {
      // 记录失败不影响 open 本身。
    }

    return originalOpen.apply(this, args);
  };

  const sendWrapper = function send(this: HookedXhr, ...args: Parameters<XMLHttpRequest['send']>) {
    const meta = this.__hackermes;

    if (meta) {
      meta.started = Date.now();

      transport.send({
        t: 'net',
        k: 'xhr',
        phase: 'start',
        id: meta.id,
        url: meta.url,
        method: meta.method,
        stack: meta.stack,
        ts: meta.started,
      });

      // 用 addEventListener 而非覆盖 onloadend,避免和页面自己的处理器冲突。
      this.addEventListener('loadend', () => {
        transport.send({
          t: 'net',
          k: 'xhr',
          phase: 'end',
          id: meta.id,
          url: meta.url,
          status: this.status,
          ok: this.status >= 200 && this.status < 400,
          durationMs: Date.now() - meta.started,
        });
      });
    }

    return originalSend.apply(this, args);
  };

  disguise(openWrapper as never, originalOpen as never);
  disguise(sendWrapper as never, originalSend as never);

  proto.open = openWrapper as typeof originalOpen;
  proto.send = sendWrapper as typeof originalSend;
}

export function installWebSocketHook(transport: Transport): void {
  const OriginalWebSocket = globalThis.WebSocket;
  if (typeof OriginalWebSocket !== 'function') {
    return;
  }

  const wrapper = function WebSocket(this: unknown, url: string | URL, protocols?: string | string[]) {
    const id = nextId('ws');
    const href = typeof url === 'string' ? url : url.href;

    transport.send({
      t: 'net',
      k: 'websocket',
      phase: 'open',
      id,
      url: href,
      stack: captureStack(),
      ts: Date.now(),
    });

    const socket = new OriginalWebSocket(url, protocols);

    socket.addEventListener('message', (event: MessageEvent) => {
      transport.send({
        t: 'net',
        k: 'websocket',
        phase: 'message',
        id,
        url: href,
        direction: 'in',
        preview: clip(typeof event.data === 'string' ? event.data : '[binary]', 2000),
      });
    });

    socket.addEventListener('close', (event: CloseEvent) => {
      transport.send({ t: 'net', k: 'websocket', phase: 'close', id, url: href, code: event.code });
    });

    socket.addEventListener('error', () => {
      transport.send({ t: 'net', k: 'websocket', phase: 'error', id, url: href });
    });

    const originalSocketSend = socket.send.bind(socket);
    socket.send = function send(data: Parameters<WebSocket['send']>[0]) {
      transport.send({
        t: 'net',
        k: 'websocket',
        phase: 'message',
        id,
        url: href,
        direction: 'out',
        preview: clip(typeof data === 'string' ? data : '[binary]', 2000),
      });
      return originalSocketSend(data);
    };

    return socket;
  } as unknown as typeof WebSocket;

  // 保住原型链与静态常量,页面里的 instanceof 与 WebSocket.OPEN 仍需可用。
  wrapper.prototype = OriginalWebSocket.prototype;
  Object.setPrototypeOf(wrapper, OriginalWebSocket);
  disguise(wrapper as never, OriginalWebSocket as never);

  globalThis.WebSocket = wrapper;
}
