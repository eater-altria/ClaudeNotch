import Foundation

/// 注入 claude.ai/settings/usage 页面、抓取 DOM 文本的 JS。
/// 逻辑移植自 mnapoli/claude-usage-bar，最后返回 JSON 字符串。
enum Extractor {
    static let script = #"""
    (function () {
      const data = {
        sessionPercent: null, sessionResetTime: null,
        weeklyAllModelsPercent: null, weeklyAllModelsReset: null,
        weeklySonnetPercent: null, weeklySonnetReset: null,
        extraSpent: null, extraLimit: null, extraBalance: null,
        extraPercent: null, extraReset: null,
        isLoggedIn: true, error: null
      };

      function findSectionByText(text) {
        const all = document.querySelectorAll('*');
        for (const el of all) {
          if (el.childNodes.length === 1 && el.textContent && el.textContent.trim() === text) {
            let p = el.parentElement;
            for (let i = 0; i < 5 && p; i++) {
              if (p.textContent && p.textContent.includes('%')) return p;
              p = p.parentElement;
            }
            return el.parentElement ? el.parentElement.parentElement : null;
          }
        }
        return null;
      }
      function pct(c) {
        if (!c) return null;
        const m = (c.textContent || '').match(/(\d+)%\s*used/);
        return m ? parseInt(m[1], 10) : null;
      }
      function reset(c) {
        if (!c) return null;
        const m = (c.textContent || '').match(/Resets?\s+(?:in\s+)?([^\n]+)/i);
        if (!m) return null;
        let t = m[1].trim();
        t = t.replace(/\s*(\d+%|used|Learn more).*$/i, '').trim();
        return t;
      }

      const s = findSectionByText('Current session');
      if (s) { data.sessionPercent = pct(s); data.sessionResetTime = reset(s); }

      const a = findSectionByText('All models');
      if (a) { data.weeklyAllModelsPercent = pct(a); data.weeklyAllModelsReset = reset(a); }

      const so = findSectionByText('Sonnet only');
      if (so) { data.weeklySonnetPercent = pct(so); data.weeklySonnetReset = reset(so); }

      const ex = findSectionByText('Extra usage');
      if (ex) {
        const t = ex.textContent || '';
        data.extraPercent = pct(ex);
        data.extraReset = reset(ex);
        const sp = t.match(/[€$£]([\d.]+)\s*spent/);
        data.extraSpent = sp ? parseFloat(sp[1]) : null;
        const lm = t.match(/[€$£](\d+).*Monthly spending limit/);
        data.extraLimit = lm ? parseFloat(lm[1]) : null;
        const bm = t.match(/[€$£]([\d.]+).*Current balance/);
        data.extraBalance = bm ? parseFloat(bm[1]) : null;
      }

      return JSON.stringify(data);
    })();
    """#
}
