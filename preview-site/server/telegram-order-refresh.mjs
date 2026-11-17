// Re-read only cards currently open on a workstation. A changing, invisible
// callback suffix makes Telegram return the current post even if the layout is unchanged.
export function createTelegramOrderRefresh({ read, apply, clock = Date.now, interval = 30_000, spacing = 4000 }) {
  const watched = new Map(), checked = new Map();
  let running = false, nextAt = 0, sequence = 0;
  return {
    observe(id) {
      if (!Number.isSafeInteger(id) || id < 1) return;
      watched.set(id, clock());
      if (watched.size > 100) { const oldest = watched.keys().next().value; watched.delete(oldest); checked.delete(oldest); }
    },
    async flush() {
      if (running || clock() < nextAt) return;
      const now = clock();
      for (const [id, touched] of watched) if (now - touched > 60_000) { watched.delete(id); checked.delete(id); }
      const entry = [...watched].filter(([id]) => !checked.has(id) || now - checked.get(id) >= interval)
        .sort(([a], [b]) => (checked.get(a) ?? -Infinity) - (checked.get(b) ?? -Infinity))[0];
      if (!entry) return;
      const [id] = entry;
      running = true; nextAt = now + spacing; checked.set(id, now);
      try {
        const snapshot = await read(id, `${now.toString(36)}${(++sequence).toString(36)}`);
        if (snapshot) await apply(snapshot);
      } catch (error) {
        if (error.retryAfter) nextAt = Math.max(nextAt, clock() + (error.retryAfter + 1) * 1000);
        console.error(`Order ${id} Telegram photo refresh:`, error.message);
      } finally { running = false; }
    }
  };
}
