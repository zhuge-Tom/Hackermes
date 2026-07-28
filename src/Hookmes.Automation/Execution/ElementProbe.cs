namespace Hookmes.Automation.Execution;

/// <summary>
/// 在页面里定位元素并回报其状态的脚本。
/// <para>
/// 为什么在页面内做定位而不是用 CDP 的 <c>DOM.querySelector</c>:
/// 后者只支持 CSS,而候选链里还有按文本、按可访问性角色的策略;
/// 而且盒模型、可见性、是否被遮挡这些信息一次求值就能全拿到,
/// 比多次 CDP 往返快得多。
/// </para>
/// </summary>
internal static class ElementProbe
{
    /// <summary>
    /// 参数:selector、strategy。返回 JSON:
    /// <c>{found, x, y, w, h, visible, interactable, tag, text, reason}</c>
    /// <para>坐标是视口坐标系的元素中心点,可直接喂给 <c>Input.dispatchMouseEvent</c>。</para>
    /// </summary>
    public const string ResolveFunction = """
        (function (sel, strategy) {
          function byText(text) {
            var nodes = document.querySelectorAll('button, a, [role="button"], input[type="submit"], label, summary');
            for (var i = 0; i < nodes.length; i++) {
              var t = (nodes[i].innerText || nodes[i].value || '').trim();
              if (t === text) return nodes[i];
            }
            return null;
          }

          function byRole(spec) {
            var parts = spec.split('|');
            var role = parts[0];
            var name = parts.length > 1 ? parts[1] : null;
            var nodes = document.querySelectorAll('[role="' + role + '"]');
            if (nodes.length === 0) {
              var implicitMap = { button: 'button', link: 'a', textbox: 'input,textarea', checkbox: 'input[type=checkbox]' };
              if (implicitMap[role]) nodes = document.querySelectorAll(implicitMap[role]);
            }
            if (!name) return nodes[0] || null;
            for (var i = 0; i < nodes.length; i++) {
              var label = nodes[i].getAttribute('aria-label') || (nodes[i].innerText || '').trim();
              if (label === name) return nodes[i];
            }
            return null;
          }

          function resolve() {
            try {
              if (strategy === 'Text') return byText(sel);
              if (strategy === 'Role') return byRole(sel);
              if (strategy === 'XPath') {
                var r = document.evaluate(sel, document, null, 9, null);
                return r.singleNodeValue;
              }
              return document.querySelector(sel);
            } catch (e) {
              return null;
            }
          }

          var el = resolve();
          if (!el) return JSON.stringify({ found: false, reason: 'not-found' });

          var rect = el.getBoundingClientRect();
          var style = window.getComputedStyle(el);

          var visible = rect.width > 0 && rect.height > 0
            && style.visibility !== 'hidden' && style.display !== 'none'
            && parseFloat(style.opacity || '1') > 0.01;

          var disabled = el.disabled === true || el.getAttribute('aria-disabled') === 'true';

          var cx = rect.left + rect.width / 2;
          var cy = rect.top + rect.height / 2;

          // 遮挡检测:中心点上最顶层的元素若不是目标本身或其后代,说明被盖住了。
          var covered = false;
          if (visible && cx >= 0 && cy >= 0 && cx <= window.innerWidth && cy <= window.innerHeight) {
            var top = document.elementFromPoint(cx, cy);
            covered = !!top && top !== el && !el.contains(top) && !top.contains(el);
          }

          return JSON.stringify({
            found: true,
            x: cx, y: cy,
            w: rect.width, h: rect.height,
            top: rect.top, left: rect.left,
            visible: visible,
            interactable: visible && !disabled && !covered,
            disabled: disabled,
            covered: covered,
            inViewport: rect.top >= 0 && rect.left >= 0
              && rect.bottom <= window.innerHeight && rect.right <= window.innerWidth,
            tag: el.tagName,
            text: (el.innerText || el.value || '').slice(0, 120)
          });
        })
        """;

    /// <summary>滚动元素到视口中央。不可见的元素点了也没用。</summary>
    public const string ScrollIntoViewFunction = """
        (function (sel, strategy) {
          var el = strategy === 'XPath'
            ? document.evaluate(sel, document, null, 9, null).singleNodeValue
            : document.querySelector(sel);
          if (!el) return 'not-found';
          el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
          return 'ok';
        })
        """;

    /// <summary>聚焦元素并按需清空,供输入类动作使用。</summary>
    public const string FocusFunction = """
        (function (sel, strategy, clearFirst) {
          var el = strategy === 'XPath'
            ? document.evaluate(sel, document, null, 9, null).singleNodeValue
            : document.querySelector(sel);
          if (!el) return 'not-found';
          el.focus();
          if (clearFirst && ('value' in el)) {
            el.value = '';
            el.dispatchEvent(new Event('input', { bubbles: true }));
          }
          return 'ok';
        })
        """;
}
