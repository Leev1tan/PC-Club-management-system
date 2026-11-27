// API access for the admin UI.
// Same origin by default: the API is served under /api on the host that serves this UI
// (Cms.Server wwwroot, the admin-ui container proxy, or an ingress path split).
// NEXT_PUBLIC_API_URL (build time) points the UI to another API origin, e.g. for `npm run dev`;
// that origin must then be listed in the server setting Cors:AllowedOrigins.
export const API = process.env.NEXT_PUBLIC_API_URL ||
  (typeof window !== 'undefined' ? window.location.origin : 'http://localhost:5081');

export type Role = 'Owner' | 'Manager' | 'Cashier' | 'Tech';
export type Me = { id: string; username: string; displayName: string; role: Role };
export type DemoAccount = { username: string; password: string; role: Role };
export type AuthStatus = { setupRequired: boolean; demoMode: boolean; demoAccounts: DemoAccount[] | null };

/** What each role may do; mirrors the server policies (the server always checks again). */
export function permissions(role: Role) {
  return {
    frontDesk: role === 'Owner' || role === 'Manager' || role === 'Cashier',
    management: role === 'Owner' || role === 'Manager',
    deviceAdmin: role === 'Owner' || role === 'Manager' || role === 'Tech',
    owner: role === 'Owner',
  };
}
export type Permissions = ReturnType<typeof permissions>;

let unauthorizedHandler: (() => void) | null = null;

/** Called when the login cookie is no longer valid (expired, password or role changed). */
export function setUnauthorizedHandler(handler: (() => void) | null) {
  unauthorizedHandler = handler;
}

/**
 * fetch() with the login cookie. State-changing requests carry the X-CCMS-CSRF header: the server
 * refuses cookie requests without it (a cross-site page cannot add the header).
 */
export async function api(path: string, init: RequestInit = {}): Promise<Response> {
  const method = (init.method || 'GET').toUpperCase();
  const headers = new Headers(init.headers);
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers.set('X-CCMS-CSRF', '1');
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const res = await fetch(`${API}${path}`, { ...init, method, headers, credentials: 'include' });
  if (res.status === 401 && unauthorizedHandler && !path.startsWith('/api/auth/')) unauthorizedHandler();
  return res;
}

export const postJson = (path: string, body?: unknown, method = 'POST') =>
  api(path, { method, body: body === undefined ? undefined : JSON.stringify(body) });

export async function errorText(res: Response): Promise<string> {
  if (res.status === 403) return 'Немає прав для цієї дії / Not allowed for your role';
  if (res.status === 429) return 'Забагато спроб, зачекайте / Too many attempts, wait';
  const text = await res.text();
  try {
    const json = JSON.parse(text);
    return json.error ?? json.title ?? text;
  } catch {
    return text || `HTTP ${res.status}`;
  }
}

/** JSON of a successful response, or null (403/404 and network errors). */
export async function getJson<T>(path: string): Promise<T | null> {
  try {
    const res = await api(path);
    return res.ok ? ((await res.json()) as T) : null;
  } catch {
    return null;
  }
}
