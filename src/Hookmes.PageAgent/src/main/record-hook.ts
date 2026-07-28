import type { Transport } from '../shared/transport';

type Candidate = { value: string; strategy: 'TestId' | 'Id' | 'Text' | 'Css'; score: number };

function escapeCss(value: string): string {
  return typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(value) : value.replace(/[^a-zA-Z0-9_-]/g, '\\$&');
}

function unique(selector: string): boolean {
  try { return document.querySelectorAll(selector).length === 1; } catch { return false; }
}

function candidatesFor(element: Element): Candidate[] {
  const result: Candidate[] = [];
  for (const name of ['data-testid', 'data-test', 'data-cy']) {
    const value = element.getAttribute(name);
    if (value) result.push({ value: `[${name}="${escapeCss(value)}"]`, strategy: 'TestId', score: 100 });
  }

  if (element.id && unique('#' + escapeCss(element.id)))
    result.push({ value: '#' + escapeCss(element.id), strategy: 'Id', score: 80 });

  const text = (element.textContent || '').trim().replace(/\s+/g, ' ');
  if (text && text.length <= 80 && (element.matches('button,a,[role="button"]')))
    result.push({ value: text, strategy: 'Text', score: 45 });

  const parts: string[] = [];
  let current: Element | null = element;
  while (current && current !== document.documentElement && parts.length < 6) {
    let part = current.tagName.toLowerCase();
    const parent: Element | null = current.parentElement;
    if (parent) {
      const peers = Array.from(parent.children).filter(child => child.tagName === current!.tagName);
      if (peers.length > 1) part += `:nth-of-type(${peers.indexOf(current) + 1})`;
    }
    parts.unshift(part);
    const selector = parts.join(' > ');
    if (unique(selector)) break;
    current = parent;
  }
  if (parts.length) result.push({ value: parts.join(' > '), strategy: 'Css', score: 30 });
  return result.sort((a, b) => b.score - a.score);
}

function targetOf(event: Event): Element | null {
  return event.target instanceof Element ? event.target : null;
}

export function installRecordingHook(transport: Transport): void {
  document.addEventListener('click', event => {
    const element = targetOf(event);
    if (!element) return;
    transport.send({ t: 'action', k: 'click', candidates: candidatesFor(element), ts: Date.now() });
  }, true);

  document.addEventListener('input', event => {
    const element = targetOf(event) as HTMLInputElement | HTMLTextAreaElement | null;
    if (!element || element instanceof HTMLSelectElement || !('value' in element)) return;
    transport.send({
      t: 'action', k: 'type',
      candidates: candidatesFor(element), value: element.value, ts: Date.now(),
    });
  }, true);

  document.addEventListener('change', event => {
    const element = targetOf(event);
    if (!(element instanceof HTMLSelectElement)) return;
    transport.send({
      t: 'action', k: 'select', candidates: candidatesFor(element), value: element.value, ts: Date.now(),
    });
  }, true);

  document.addEventListener('keydown', event => {
    if (event.key !== 'Enter' && event.key !== 'Escape' && event.key !== 'Tab') return;
    transport.send({ t: 'action', k: 'press', key: event.key, ts: Date.now() });
  }, true);
}
