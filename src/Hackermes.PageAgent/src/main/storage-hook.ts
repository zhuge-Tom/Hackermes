import { clip, type Transport } from '../shared/transport';

/**
 * 存储写入 hook。
 *
 * CDP 的 DOMStorage 域能拿到存储内容,但拿不到"谁在什么时候写的"。
 * 调试登录态丢失、缓存被意外清空这类问题时,写入时机才是线索。
 */

function hookStorage(transport: Transport, storage: Storage, area: 'local' | 'session'): void {
  if (!storage) {
    return;
  }

  const proto = Object.getPrototypeOf(storage) as Storage;
  const originalSet = proto.setItem;
  const originalRemove = proto.removeItem;
  const originalClear = proto.clear;

  proto.setItem = function setItem(this: Storage, key: string, value: string) {
    // 只在操作的是被 hook 的那个 area 时上报,否则 local 与 session 会互相串。
    if (this === storage) {
      transport.send({
        t: 'storage',
        k: area,
        op: 'set',
        key,
        valuePreview: clip(String(value), 512),
        ts: Date.now(),
      });
    }

    return originalSet.call(this, key, value);
  };

  proto.removeItem = function removeItem(this: Storage, key: string) {
    if (this === storage) {
      transport.send({ t: 'storage', k: area, op: 'remove', key, ts: Date.now() });
    }

    return originalRemove.call(this, key);
  };

  proto.clear = function clear(this: Storage) {
    if (this === storage) {
      transport.send({ t: 'storage', k: area, op: 'clear', ts: Date.now() });
    }

    return originalClear.call(this);
  };
}

export function installStorageHook(transport: Transport): void {
  try {
    hookStorage(transport, globalThis.localStorage, 'local');
  } catch {
    // 某些沙箱环境下访问 localStorage 会抛(third-party cookie 被禁)。
  }

  try {
    hookStorage(transport, globalThis.sessionStorage, 'session');
  } catch {
    // 同上。
  }
}

export function installCookieHook(transport: Transport): void {
  try {
    const descriptor =
      Object.getOwnPropertyDescriptor(Document.prototype, 'cookie') ??
      Object.getOwnPropertyDescriptor(globalThis.document, 'cookie');

    if (!descriptor?.get || !descriptor.set) {
      return;
    }

    const originalGet = descriptor.get;
    const originalSet = descriptor.set;

    Object.defineProperty(Document.prototype, 'cookie', {
      configurable: true,
      enumerable: descriptor.enumerable,
      get(this: Document) {
        return originalGet.call(this);
      },
      set(this: Document, value: string) {
        transport.send({
          t: 'storage',
          k: 'cookie',
          op: 'set',
          valuePreview: clip(String(value), 512),
          ts: Date.now(),
        });

        return originalSet.call(this, value);
      },
    });
  } catch {
    // cookie 属性在部分环境不可重定义,放弃 hook 即可。
  }
}
