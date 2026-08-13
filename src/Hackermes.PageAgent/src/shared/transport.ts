/**
 * 页面 → 宿主的回传通道。
 *
 * 底层是 CDP 的 Runtime.addBinding:宿主注册一个全局函数名,页面调用它,
 * 宿主侧收到 Runtime.bindingCalled 事件。这是页面主动向宿主说话的唯一途径。
 */

/**
 * 每个 binding payload 的数据片上限。保守地留出 JSON 转义和 CDP 事件包装空间,
 * 不假定 WebView2 对单次字符串有无界容量。
 */
export const CHUNK_DATA_CHARS = 16 * 1024;

/** 宿主允许重组的单条逻辑消息上限。 */
export const MAX_REASSEMBLED_CHARS = 2 * 1024 * 1024;

/** 与宿主端容量契约一致。 */
export const MAX_CHUNKS = 128;

export interface AgentMessage {
  /** 消息类型:net / storage / route / console / lifecycle */
  t: string;
  /** 子类型,例如 fetch / xhr / websocket */
  k?: string;
  [key: string]: unknown;
}

export interface Transport {
  send(message: AgentMessage): void;
  readonly available: boolean;
}

/**
 * 取得回传通道。binding 可能尚未注册(注入脚本先于 addBinding 生效),
 * 因此每次发送都重新查一次全局对象,并在不可用时静默丢弃 ——
 * 页面不应该因为调试通道没准备好而报错。
 */
export function createTransport(bindingName: string): Transport {
  let seq = 0;
  let messageId = 0;

  return {
    get available(): boolean {
      return typeof (globalThis as Record<string, unknown>)[bindingName] === 'function';
    },

    send(message: AgentMessage): void {
      const binding = (globalThis as Record<string, unknown>)[bindingName];
      if (typeof binding !== 'function') {
        return;
      }

      try {
        message.seq = ++seq;
        const json = JSON.stringify(message);

        if (json.length > MAX_REASSEMBLED_CHARS) {
          const rejected = JSON.stringify({
            t: message.t,
            k: message.k,
            seq: message.seq,
            truncated: true,
            originalChars: json.length,
            note: 'payload 超出 2 MiB 重组上限已丢弃字段',
          });
          (binding as (payload: string) => void)(rejected);
          return;
        }

        if (json.length <= CHUNK_DATA_CHARS) {
          (binding as (payload: string) => void)(json);
          return;
        }

        const total = Math.ceil(json.length / CHUNK_DATA_CHARS);
        if (total > MAX_CHUNKS) {
          return;
        }

        const id = (++messageId).toString(36) + '-' + Date.now().toString(36);
        for (let index = 0; index < total; index++) {
          const data = json.slice(index * CHUNK_DATA_CHARS, (index + 1) * CHUNK_DATA_CHARS);
          (binding as (payload: string) => void)(JSON.stringify({
            __hmChunk: 1,
            id,
            index,
            total,
            data,
          }));
        }
      } catch {
        // 回传失败绝不能影响页面自身逻辑。
      }
    },
  };
}

/** 截断长文本并标注原长度。 */
export function clip(text: string | null | undefined, max: number): string | undefined {
  if (text == null) {
    return undefined;
  }

  return text.length <= max ? text : text.slice(0, max) + `…(共 ${text.length} 字符)`;
}

/**
 * 取当前调用栈,去掉 Agent 自身的帧。
 * 这是 Page Agent 相对 CDP Network 域的核心增量 —— 协议层看不到"谁发起的请求"。
 */
export function captureStack(skipFrames = 2): string | undefined {
  try {
    const stack = new Error().stack;
    if (!stack) {
      return undefined;
    }

    const lines = stack.split('\n').slice(skipFrames + 1);
    return clip(lines.join('\n').trim(), 4000);
  } catch {
    return undefined;
  }
}

/** 生成短标识,用于把请求的开始与结束配对。 */
export function nextId(prefix: string): string {
  return prefix + '-' + Math.random().toString(36).slice(2, 10);
}
