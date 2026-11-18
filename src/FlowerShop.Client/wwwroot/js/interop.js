window.fsStore = {
  get: k => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k, v) => { try { localStorage.setItem(k, v); } catch { } },
  remove: k => { try { localStorage.removeItem(k); } catch { } }
};
