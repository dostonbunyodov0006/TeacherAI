// Auth + REST helper. Access token lives only in memory; the refresh token is an HttpOnly cookie.
let accessToken = null;
let expiresAt = 0;
let user = null;

/** Browser's IANA time zone: defines the user's calendar day on the server. */
export const timeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';

export const getUser = () => user;

function accept(data) {
  accessToken = data.accessToken;
  expiresAt = Date.parse(data.expiresAt);
  user = data.user;
  return user;
}

export async function login(username, password) {
  const r = await fetch('/api/auth/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
    body: JSON.stringify({ username, password }),
  });
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).detail || 'Kirishda xatolik');
  return accept(await r.json());
}

export async function register(name, username, password) {
  const r = await fetch('/api/auth/register', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
    body: JSON.stringify({ name, username, password }),
  });
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).detail || 'Ro\'yxatdan o\'tishda xatolik');
  return accept(await r.json());
}

export async function refresh() {
  const r = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin' });
  if (!r.ok) { accessToken = null; return null; }
  return accept(await r.json());
}

export async function logout() {
  await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
  accessToken = null; user = null;
}

/** Valid access token, refreshed ~60 s before expiry. Used by SignalR's accessTokenFactory too. */
export async function token() {
  if (!accessToken || Date.now() > expiresAt - 60_000) await refresh();
  return accessToken;
}

export async function api(path, opts = {}) {
  const t = await token();
  const r = await fetch(path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', 'X-Time-Zone': timeZone(), ...(opts.headers || {}), Authorization: `Bearer ${t}` },
  });
  if (r.status === 401) throw new Error('unauthorized');
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return r.status === 204 ? null : r.json();
}
