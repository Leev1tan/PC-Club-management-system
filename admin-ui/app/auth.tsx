'use client';
import { useState } from 'react';
import { AuthStatus, Me, errorText, postJson } from './api';
import { lang, setLang, t } from './i18n';
import { ClubLogo } from './theme';

export function roleLabel(role: string) {
  return t.roles[role] ?? role;
}

/** UA / EN switch (saves the choice and reloads the page). */
export function LangSwitch() {
  return (
    <span className="lang-switch" role="group" aria-label={t.lang.label}>
      {(['uk', 'en'] as const).map(l => (
        <button key={l} type="button" className={`btn btn-sm ${lang === l ? 'active' : ''}`} aria-pressed={lang === l}
          onClick={() => lang !== l && setLang(l)}>{t.lang[l]}</button>
      ))}
    </span>
  );
}

function AuthCard({ title, sub, children }: { title: string; sub?: string; children: React.ReactNode }) {
  return (
    <div className="auth-screen">
      <div className="modal auth-card">
        <div className="auth-lang"><LangSwitch /></div>
        <div className="sidebar-logo" style={{ marginBottom: 18 }}>
          <div className="sidebar-logo-icon"><ClubLogo alt={t.theme.logoAlt} /></div>
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
      else setError(res.status === 401 ? t.auth.wrong : await errorText(res));
    } catch {
      setError(t.common.unreachable);
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthCard title={t.auth.title}>
      <form onSubmit={e => { e.preventDefault(); login(username, password); }}>
        <div className="form-group">
          <label className="form-label" htmlFor="username">{t.auth.username}</label>
          <input id="username" className="form-input" autoComplete="username" autoFocus
            value={username} onChange={e => setUsername(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="password">{t.auth.password}</label>
          <input id="password" className="form-input" type="password" autoComplete="current-password"
            value={password} onChange={e => setPassword(e.target.value)} />
        </div>
        {error && <div className="auth-error" role="alert">{error}</div>}
        <div className="modal-actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !username || !password}>
            {busy ? '...' : t.auth.signIn}
          </button>
        </div>
      </form>

      {status?.demoMode && status.demoAccounts && status.demoAccounts.length > 0 && (
        <div className="demo-accounts-box">
          <div className="form-label">{t.auth.demoAccounts}</div>
          <div className="demo-accounts">
            {status.demoAccounts.map(a => (
              <button key={a.username} className="btn btn-sm" type="button" disabled={busy}
                onClick={() => { setUsername(a.username); setPassword(a.password); login(a.username, a.password); }}>
                {roleLabel(a.role)}
                <span className="demo-account-name">{a.username} / {a.password}</span>
              </button>
            ))}
          </div>
          <div className="auth-sub">{t.auth.demoOnly}</div>
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
    if (password !== password2) { setError(t.auth.mismatch); return; }
    setBusy(true);
    setError(null);
    try {
      const res = await postJson('/api/auth/setup', { setupCode: code, username, displayName, password });
      if (res.ok) onDone(await res.json());
      else setError(await errorText(res));
    } catch {
      setError(t.common.unreachable);
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthCard title={t.auth.setupTitle}
      sub={t.auth.setupSub}>
      <form onSubmit={e => { e.preventDefault(); submit(); }}>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-code">{t.auth.setupCode}</label>
          <input id="setup-code" className="form-input" placeholder="XXXX-XXXX-XXXX" autoComplete="off" autoFocus
            value={code} onChange={e => setCode(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-user">{t.auth.ownerUsername}</label>
          <input id="setup-user" className="form-input" autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-name">{t.auth.displayName}</label>
          <input id="setup-name" className="form-input" value={displayName} onChange={e => setDisplayName(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-pass">{t.auth.passwordMin}</label>
          <input id="setup-pass" className="form-input" type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label" htmlFor="setup-pass2">{t.auth.passwordRepeat}</label>
          <input id="setup-pass2" className="form-input" type="password" autoComplete="new-password" value={password2} onChange={e => setPassword2(e.target.value)} />
        </div>
        {error && <div className="auth-error" role="alert">{error}</div>}
        <div className="modal-actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !code || !username || !password}>
            {busy ? '...' : t.auth.createOwner}
          </button>
        </div>
      </form>
    </AuthCard>
  );
}
