/**
 * 页面 → 宿主的回传通道。
 *
 * 底层是 CDP 的 Runtime.addBinding:宿主注册一个全局函数名,页面调用它,
 * 宿主侧收到 Runtime.bindingCalled 事件。这是页面主动向宿主说话的唯一途径。
 */

/** 单条消息的上限。超长的字段(主要是调用栈和请求体)按需截断,避免撑爆事件通道。 */
const MAX_MESSAGE_CHARS = 64 * 1024;

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
        let json = JSON.stringify(message);

        if (json.length > MAX_MESSAGE_CHARS) {
          json = JSON.stringify({
            t: message.t,
            k: message.k,
            seq: message.seq,
            truncated: true,
            note: 'payload 超出上限已丢弃字段',
          });
        }

        (binding as (payload: string) => void)(json);
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
