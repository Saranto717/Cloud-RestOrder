(() => {
  const idleLimitMs = 10 * 60 * 1000;
  const refreshAfterMs = 5 * 60 * 1000;
  let lastActivity = Date.now();
  let lastRefresh = lastActivity;
  let running = false;

  const noteActivity = () => { lastActivity = Date.now(); };
  for (const eventName of ['pointerdown', 'keydown', 'touchstart', 'click', 'scroll']) {
    window.addEventListener(eventName, noteActivity, { passive: true });
  }

  async function tick() {
    if (running) return;
    running = true;
    try {
      const now = Date.now();
      if (now - lastActivity >= idleLimitMs) {
        await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin', cache: 'no-store' }).catch(() => {});
        window.location.replace('/pos.html');
        return;
      }
      if (now - lastRefresh >= refreshAfterMs && now - lastActivity < idleLimitMs) {
        const response = await fetch('/api/auth/refresh', {
          method: 'POST', credentials: 'same-origin', cache: 'no-store',
          headers: { 'Content-Type': 'application/json' }, body: '{}'
        });
        if (response.status === 401 || response.status === 403) {
          window.location.replace('/pos.html');
          return;
        }
        if (response.ok) lastRefresh = now;
      }
    } catch { /* Leave the current view available during a brief connection loss. */ }
    finally { running = false; }
  }

  window.setInterval(tick, 30_000);
})();
