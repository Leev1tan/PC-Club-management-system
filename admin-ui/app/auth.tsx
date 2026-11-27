'use client';
import { useState } from 'react';
import { AuthStatus, Me, errorText, postJson } from './api';

const roleNames: Record<string, string> = {
  Owner: 'Власник / Owner',
  Manager: 'Менеджер / Manager',
  Cashier: 'Касир / Cashier',
  Tech: 'Технік / Tech',
};

export function roleLabel(role: string) {
  return roleNames[role] ?? role;
}

function AuthCard({ title, sub, children }: { title: string; sub?: string; children: React.ReactNode }) {
  return (
    <div className="auth-screen">
      <div className="modal auth-card">
        <div className="sidebar-logo" style={{ marginBottom: 18 }}>
          <div className="sidebar-logo-icon">🎮</div>
          <div>
            <div className="sidebar-logo-text">PC Club</div>
            <div className="sidebar-logo-sub">Management</div>
          </div>
        </div>
        <div className="modal-title">{title}</div>
        {sub && <div className="auth-sub">{sub}</div>}
        {children}
      </div>
    </div>
  );
}

/** Staff login. In demo mode the public demo accounts are shown as buttons (demo databases only). */
export function LoginScreen({ status, onLogin }: { status: AuthStatus | null; onLogin: (me: Me) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const login = async (u: string, p: string) => {
    setBusy(true);
    setError(null);
    try {
      const res = await postJson('/api/auth/login', { username: u, password: p });
      if (res.ok) onLogin(await res.json());
      else setError(res.status === 401 ? 'Невірний логін або пароль / Wrong user name or password' : await errorText(res));
    } catch {
      setError('Сервер недоступний / Server not reachable');
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthCard title="Вхід персоналу / Staff sign-in">
      <form onSubmit={e => { e.preventDefault(); login(username, password); }}>
        <div className="form-group">
          <label className="form-label" htmlFor="username">Логін / User name</label>
          <input id="username" className="form-input" autoComplete="username" autoFocus
            value={username} onChange={e => setUsername(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="password">Пароль / Password</label>
          <input id="password" className="form-input" type="password" autoComplete="current-password"
            value={password} onChange={e => setPassword(e.target.value)} />
        </div>
        {error && <div className="auth-error" role="alert">{error}</div>}
        <div className="modal-actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !username || !password}>
            {busy ? '...' : 'Увійти / Sign in'}
          </button>
        </div>
      </form>

      {status?.demoMode && status.demoAccounts && status.demoAccounts.length > 0 && (
        <div className="demo-accounts-box">
          <div className="form-label">LIVE DEMO — публічні демо-акаунти / public demo accounts</div>
          <div className="demo-accounts">
            {status.demoAccounts.map(a => (
              <button key={a.username} className="btn btn-sm" type="button" disabled={busy}
                onClick={() => { setUsername(a.username); setPassword(a.password); login(a.username, a.password); }}>
                {roleLabel(a.role)}
                <span className="demo-account-name">{a.username} / {a.password}</span>
              </button>
            ))}
          </div>
          <div className="auth-sub">Ці акаунти існують лише в демо-режимі. These accounts exist only in demo mode.</div>
        </div>
      )}
    </AuthCard>
  );
}

/**
 * First start (no staff account yet): create the Owner. Needs the one-time setup code from the
 * server data directory (setup-code.txt), so only someone with access to the server can do it.
 */
export function SetupScreen({ onDone }: { onDone: (me: Me) => void }) {
  const [code, setCode] = useState('');
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [password2, setPassword2] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (password !== password2) { setError('Паролі не збігаються / Passwords do not match'); return; }
    setBusy(true);
    setError(null);
    try {
      const res = await postJson('/api/auth/setup', { setupCode: code, username, displayName, password });
      if (res.ok) onDone(await res.json());
      else setError(await errorText(res));
    } catch {
      setError('Сервер недоступний / Server not reachable');
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthCard title="Перший запуск / First start"
      sub="Створіть акаунт власника. Код налаштування — у файлі setup-code.txt у каталозі даних сервера (Windows: C:\ProgramData\PCClub, Docker: docker compose exec server cat /app/data/setup-code.txt). Create the owner account with the one-time setup code from the server.">
      <form onSubmit={e => { e.preventDefault(); submit(); }}>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-code">Код налаштування / Setup code</label>
          <input id="setup-code" className="form-input" placeholder="XXXX-XXXX-XXXX" autoComplete="off" autoFocus
            value={code} onChange={e => setCode(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-user">Логін власника / Owner user name</label>
          <input id="setup-user" className="form-input" autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-name">Ім&apos;я / Display name</label>
          <input id="setup-name" className="form-input" value={displayName} onChange={e => setDisplayName(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-pass">Пароль (мін. 8) / Password (min. 8)</label>
          <input id="setup-pass" className="form-input" type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-pass2">Повторіть пароль / Repeat password</label>
          <input id="setup-pass2" className="form-input" type="password" autoComplete="new-password" value={password2} onChange={e => setPassword2(e.target.value)} />
        </div>
        {error && <div className="auth-error" role="alert">{error}</div>}
        <div className="modal-actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !code || !username || !password}>
            {busy ? '...' : 'Створити власника / Create owner'}
          </button>
        </div>
      </form>
    </AuthCard>
  );
}
