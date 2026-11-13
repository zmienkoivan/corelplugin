export function createRemoteOrders(server, key, fetchImpl = fetch) {
  if (!server && !key) return null;
  const origin = new URL(server);
  if ((origin.protocol !== 'https:' && !(origin.protocol === 'http:' &&
      ['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname))) ||
      origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash || !key)
    throw new Error('Specify an HTTPS order server origin and its key.');
  const legacyConnection = { mode: 'remote', server: origin.origin, intakeEnabled: false, stateEnabled: true,
    deadlineEnabled: false, urgencyEnabled: false, customerEnabled: false, pauseEnabled: false, blanksReadyEnabled: false, deletionEnabled: false,
    message: 'Рабочий сервер подключён. Приёмка, сроки и этикетки будут доступны после обновления сервера.' };
  let cachedConnection, checkedAt = 0;
  async function connectionInfo() {
    if (cachedConnection && Date.now() - checkedAt < 10_000) return cachedConnection;
    const response = await fetchImpl(`${origin.origin}/api/orders/connection`, {
      headers: { 'X-Admin-Key': key }, redirect: 'error', signal: AbortSignal.timeout(25_000) });
    if (response.status === 404) cachedConnection = legacyConnection;
    else {
      if (!response.ok) throw new Error('Order server connection rejected.');
      const value = await response.json();
      cachedConnection = { mode: 'remote', server: origin.origin, intakeEnabled: value.intakeEnabled === true,
        stateEnabled: value.stateEnabled === true, deadlineEnabled: value.deadlineEnabled === true,
        urgencyEnabled: value.urgencyEnabled === true, customerEnabled: value.customerEnabled === true, pauseEnabled: value.pauseEnabled === true,
        blanksReadyEnabled: value.blanksReadyEnabled === true, customerTextileEnabled: value.customerTextileEnabled === true, deletionEnabled: value.deletionEnabled === true,
        notesEnabled: value.notesEnabled === true, photoMarkupEnabled: value.photoMarkupEnabled === true, message: 'Рабочий сервер подключён.' };
    }
    checkedAt = Date.now(); return cachedConnection;
  }
  return {
    connection: legacyConnection,
    connectionInfo,
    async network(route, method, body) {
      const allowed = method === 'GET' && /^\/api\/network-printing\/(printers|jobs(?:\/[0-9a-f-]{36})?)$/i.test(route) ||
        method === 'POST' && route === '/api/network-printing/jobs' ||
        method === 'PATCH' && /^\/api\/network-printing\/stations\/[0-9a-f-]{36}$/i.test(route);
      if (!allowed) return Response.json({ error: 'Маршрут сетевой печати недоступен.' }, { status: 404 });
      return fetchImpl(origin.origin + route, { method, redirect: 'error', signal: AbortSignal.timeout(25_000),
        headers: { 'X-Admin-Key': key, ...(body ? { 'Content-Type': 'application/json' } : {}) },
        ...(body ? { body: JSON.stringify(body) } : {}) });
    },
    async setState(id, body) {
      if (!Number.isSafeInteger(id) || id < 1 || !['new', 'done'].includes(body.state) ||
          !Number.isSafeInteger(body.revision) || body.revision < 1)
        return Response.json({ error: 'Неверные параметры готовности заказа.' }, { status: 400 });
      return fetchImpl(`${origin.origin}/api/orders/${id}/state`, { method: 'PATCH',
        headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' },
        body: JSON.stringify({ state: body.state, revision: body.revision }),
        redirect: 'error', signal: AbortSignal.timeout(60_000) });
    },
    async write(route, method, body) {
      const source = await connectionInfo();
      const allowed = (method === 'POST' && route === '/api/orders/intake' && source.intakeEnabled) ||
        (method === 'PUT' && /^\/api\/orders\/\d+\/photos\/\d+\/markup$/.test(route) && source.photoMarkupEnabled) ||
        (method === 'POST' && /^\/api\/orders\/\d+\/notes(?:\/\d+\/(?:activity|state))?$/.test(route) && source.notesEnabled) ||
        (method === 'DELETE' && /^\/api\/orders\/\d+$/.test(route) && source.deletionEnabled) ||
        (method === 'POST' && /^\/api\/orders\/\d+\/assign$/.test(route) && source.intakeEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/deadline$/.test(route) && source.deadlineEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/urgency$/.test(route) && source.urgencyEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/pause$/.test(route) && source.pauseEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/customer-textile$/.test(route) && source.customerTextileEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/blanks-ready$/.test(route) && source.blanksReadyEnabled) ||
        (method === 'PATCH' && /^\/api\/orders\/\d+\/customer$/.test(route) && source.customerEnabled);
      if (!allowed) return Response.json({ error: 'Эта функция ещё недоступна на рабочем сервере.' }, { status: 405 });
      return fetchImpl(origin.origin + route, { method,
        headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' }, body: JSON.stringify(body),
        redirect: 'error', signal: AbortSignal.timeout(60_000) });
    },
    async read(route) {
      if (!/^\/api\/orders(?:$|\/(?:unassigned|imports|\d+(?:\/photos\/\d+(?:\/markup)?|\/label|\/notes(?:\/\d+\/photos\/\d+)?)?|by-number\/\d{5}|by-label\/[A-Za-z0-9_-]{22})$)/.test(route.split('?')[0]))
        return Response.json({ error: 'Этот маршрут недоступен при просмотре рабочего сервера.' }, { status: 404 });
      const response = await fetchImpl(origin.origin + route, {
        headers: { 'X-Admin-Key': key }, redirect: 'error', signal: AbortSignal.timeout(25_000) });
      if (route === '/api/orders/unassigned' && response.status === 404)
        return Response.json([]);
      return response;
    }
  };
}
