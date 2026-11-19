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

/**
 * Rotate the refresh cookie → new access token. Only ONE refresh runs at a time — in this tab (shared promise) and
 * across tabs (Web Locks). Two refreshes with the same cookie used to look like token theft to the server, which
 * then revoked every session and logged the student out.
 */
let refreshing = null;
export function refresh() {
  refreshing ??= (async () => {
    const run = async () => {
      const r = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin' });
      if (!r.ok) { accessToken = null; return null; }
      return accept(await r.json());
    };
    try { return navigator.locks ? await navigator.locks.request('ustoz-auth-refresh', run) : await run(); }
    finally { refreshing = null; }
  })();
  return refreshing;
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

/**
 * fetch with the access token. On 401 (token expired / rotated meanwhile): refresh ONCE and retry. If that still
 * fails the session is really over → 'ustoz:auth-lost' (the app shows the login page instead of a cryptic error).
 */
export async function authFetch(path, init = {}) {
  const send = (t) => fetch(path, { ...init, headers: { 'X-Time-Zone': timeZone(), ...(init.headers || {}), Authorization: `Bearer ${t}` } });
  let r = await send(await token());
  if (r.status !== 401) return r;
  if (await refresh()) r = await send(accessToken);
  if (r.status === 401) window.dispatchEvent(new CustomEvent('ustoz:auth-lost'));
  return r;
}

export async function api(path, opts = {}) {
  const r = await authFetch(path, { ...opts, headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) } });
  if (r.status === 401) throw new Error('unauthorized');
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return r.status === 204 ? null : r.json();
}
