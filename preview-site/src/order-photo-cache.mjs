// Shared between the sidebar, gallery and fullscreen viewer. Only photo versions
// identify content: changing an order's status must not invalidate its pictures.
export const photoIdentity = ({ orderId, index, version, serverKey, full }) =>
  JSON.stringify([serverKey, orderId, index, version ?? null, Boolean(full)]);

export function createPhotoCache({ maxBytes = 96 * 1024 * 1024, maxEntries = 160,
  fetchPhoto = (...args) => fetch(...args),
  createUrl = blob => URL.createObjectURL(blob), revokeUrl = url => URL.revokeObjectURL(url) } = {}) {
  const entries = new Map();
  let bytes = 0;

  function remove(entry) {
    if (entry.removed) return;
    entry.removed = true;
    entries.delete(entry.key);
    entry.controller.abort();
    if (entry.url) { bytes -= entry.bytes; revokeUrl(entry.url); }
  }
  function trim() {
    for (const entry of entries.values()) {
      if (bytes <= maxBytes && entries.size <= maxEntries) break;
      // Mounted images keep their URL until the last viewer releases it.
      if (!entry.users && entry.url) remove(entry);
    }
  }

  return {
    peek(photo) {
      const entry = entries.get(photoIdentity(photo));
      return entry && !entry.obsolete ? entry.url : null;
    },
    acquire(photo) {
      const key = photoIdentity(photo);
      const slot = JSON.stringify([photo.serverKey, photo.orderId, photo.index, Boolean(photo.full)]);
      for (const old of entries.values()) {
        if (old.slot === slot && old.key !== key) {
          old.obsolete = true;
          if (!old.users) remove(old);
        }
      }
      let entry = entries.get(key);
      if (!entry) {
        entry = { key, slot, serverKey: photo.serverKey, users: 0, bytes: 0, url: null,
          obsolete: false, removed: false, controller: new AbortController() };
        entries.set(key, entry);
        entry.ready = (async () => {
          const response = await fetchPhoto(`/api/orders/${photo.orderId}/photos/${photo.index}${photo.full ? '' : '?thumbnail=1'}`,
            { headers: { 'X-Admin-Key': photo.serverKey }, signal: entry.controller.signal });
          if (!response.ok) throw new Error('Photo unavailable');
          const blob = await response.blob();
          if (entry.removed) throw new DOMException('Photo released', 'AbortError');
          entry.url = createUrl(blob); entry.bytes = blob.size; bytes += blob.size;
          trim();
          return entry.url;
        })().catch(error => { remove(entry); throw error; });
      }
      entry.users++;
      // Map order doubles as LRU order; reusing an image moves it to the end.
      entries.delete(key); entries.set(key, entry);
      let released = false;
      return { url: entry.url, ready: entry.ready, release() {
        if (released) return;
        released = true; entry.users--;
        if (!entry.users && entry.obsolete) remove(entry);
        trim();
      } };
    },
    clear(serverKey) {
      for (const entry of entries.values()) {
        if (serverKey !== undefined && entry.serverKey !== serverKey) continue;
        entry.obsolete = true;
        if (!entry.users) remove(entry);
      }
    }
  };
}

export const orderPhotoCache = createPhotoCache();
