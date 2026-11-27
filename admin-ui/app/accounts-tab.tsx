'use client';
import { useCallback, useEffect, useState } from 'react';
import { api, errorText, getJson, postJson } from './api';
import { clubDateTime, fmt, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Lease = { id: number; deviceId: string; hostname: string; sessionId: string; username: string | null; game: string | null; startedUtc: string; delivered: boolean };
type Account = {
  id: string; platform: string; loginMask: string; hasSecret: boolean; isActive: boolean; removed: boolean;
  createdUtc: string; secretUpdatedUtc: string | null; lease: Lease | null; leases: number;
};

/** Platforms of the account pool (the same ids as the server, Cms.Shared.AccountPlatforms). */
export const accountPlatforms = ['steam', 'epic', 'battlenet', 'riot', 'ea', 'ubisoft', 'gog', 'other'] as const;
export const platformName = (p: string | null | undefined) => p ? (t.accounts.platforms as Record<string, string>)[p] ?? p : '—';
const failed = async (res: Response) => `${t.common.errorPrefix}: ${await errorText(res)}`;

/**
 * Club game-account pool (spec area 8). The front desk sees which PC uses which account (the login is masked, the
 * secret is never shown) and frees a stuck one; Owner and Manager add accounts, set a new secret, switch them off and
 * remove them. The server never returns a login or a secret.
 */
export function AccountsTab({ toast, management }: { toast: ToastFn; management: boolean }) {
  const [list, setList] = useState<Account[] | null>(null);
  const [adding, setAdding] = useState(false);
  const [rotating, setRotating] = useState<Account | null>(null);
  const [showRemoved, setShowRemoved] = useState(false);
  const load = useCallback(async () => setList(await getJson<Account[]>('/api/game-accounts')), []);
  useEffect(() => {
    load();
    const iv = setInterval(load, 10000);
    return () => clearInterval(iv);
  }, [load]);

  const act = async (a: Account, what: 'release' | 'on' | 'off' | 'remove') => {
    if (what === 'remove' && !window.confirm(fmt(t.accounts.confirmRemove, { login: a.loginMask }))) return;
    if (what === 'release' && !window.confirm(fmt(t.accounts.confirmRelease, { login: a.loginMask, pc: a.lease?.hostname ?? '' }))) return;
    const res = what === 'release' ? await postJson(`/api/game-accounts/${a.id}/release`, {})
      : what === 'remove' ? await api(`/api/game-accounts/${a.id}`, { method: 'DELETE' })
      : await postJson(`/api/game-accounts/${a.id}/active`, { isActive: what === 'on' }, 'PUT');
    toast(res.ok ? (t.accounts.done as Record<string, string>)[what] : await failed(res), res.ok ? 'success' : 'error');
    load();
  };

  const shown = (list ?? []).filter(a => showRemoved || !a.removed);
  const inUse = shown.filter(a => a.lease).length;

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.accounts.title}</span>
        <span className="resv-toolbar">
          <label className="check-row" style={{ margin: 0 }}><input type="checkbox" checked={showRemoved} onChange={e => setShowRemoved(e.target.checked)} /> {t.accounts.showRemoved}</label>
          {management && <button className="btn btn-primary btn-sm" onClick={() => setAdding(true)}>{t.accounts.add}</button>}
        </span>
      </div>
      <p className="auth-sub">{t.accounts.hint}</p>
      <div className="section-card account-security">
        <div className="section-card-header"><span className="section-card-title">{t.accounts.poolTitle}</span></div>
        <div className="form-hint">{t.accounts.security}</div>
      </div>

      {list && shown.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon">🔑</div>
          <div className="empty-state-title">{t.accounts.empty}</div>
        </div>
      ) : (
        <div className="table-wrapper">
          <div className="form-hint" style={{ marginBottom: 6 }}>{fmt(t.accounts.inUse, { n: inUse, total: shown.filter(a => !a.removed).length })}</div>
          <table className="data-table">
            <thead><tr>
              <th>{t.accounts.col.platform}</th><th>{t.accounts.col.login}</th><th>{t.accounts.col.secret}</th><th>{t.accounts.col.state}</th>
              <th>{t.accounts.col.pc}</th><th>{t.accounts.col.leases}</th><th></th>
            </tr></thead>
            <tbody>
              {shown.map(a => (
                <tr key={a.id} className={a.removed || !a.isActive ? 'row-muted' : ''}>
                  <td><span className="badge badge-blue">{platformName(a.platform)}</span></td>
                  <td className="mono-cell">{a.loginMask}</td>
                  <td>{a.hasSecret ? <span className="badge badge-green">{t.accounts.stored}</span> : <span className="badge badge-amber">{t.accounts.noSecret}</span>}
                    {a.secretUpdatedUtc && <div className="user-chip">{fmt(t.accounts.since, { when: clubDateTime(a.secretUpdatedUtc) })}</div>}</td>
                  <td>{a.removed ? <span className="badge badge-red">{t.accounts.removed}</span>
                    : !a.isActive ? <span className="badge badge-red">{t.accounts.off}</span>
                    : a.lease ? <span className="badge badge-purple">{t.accounts.used}</span> : <span className="badge badge-green">{t.accounts.free}</span>}</td>
                  <td>
                    {a.lease ? (
                      <>
                        <strong>{a.lease.hostname}</strong>
                        <div className="user-chip">
                          {a.lease.username ? <span className="data">@{a.lease.username}</span> : t.guest}
                          {a.lease.game && <> · <span className="data">{a.lease.game}</span></>}
                        </div>
                        <div className="user-chip">
                          {fmt(t.accounts.leasedAt, { when: clubDateTime(a.lease.startedUtc) })}
                          {' · '}{a.lease.delivered ? t.accounts.delivered : t.accounts.notDelivered}
                        </div>
                      </>
                    ) : '—'}
                  </td>
                  <td>{a.leases}</td>
                  <td className="account-actions">
                    <div>
                      {a.lease && <button className="btn btn-sm" onClick={() => act(a, 'release')}>{t.accounts.release}</button>}
                      {management && !a.removed && <button className="btn btn-sm" onClick={() => setRotating(a)}>{a.hasSecret ? t.accounts.rotate : t.accounts.setSecret}</button>}
                      {management && !a.removed && <button className="btn btn-sm" onClick={() => act(a, a.isActive ? 'off' : 'on')}>{a.isActive ? t.accounts.turnOff : t.accounts.turnOn}</button>}
                      {management && !a.removed && <button className="btn btn-danger btn-sm" onClick={() => act(a, 'remove')}>{t.delete}</button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {adding && <AddDialog toast={toast} onClose={() => setAdding(false)} onDone={() => { setAdding(false); toast(t.accounts.added, 'success'); load(); }} />}
      {rotating && <SecretDialog account={rotating} toast={toast} onClose={() => setRotating(null)} onDone={() => { setRotating(null); toast(t.accounts.rotated, 'success'); load(); }} />}
    </>
  );
}

function AddDialog({ toast, onClose, onDone }: { toast: ToastFn; onClose: () => void; onDone: () => void }) {
  const [platform, setPlatform] = useState<string>('steam');
  const [login, setLogin] = useState('');
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    const res = await postJson('/api/game-accounts', { platform, login: login.trim(), secret: secret || null });
    setBusy(false);
    setSecret('');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    onDone();
  };
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{t.accounts.addTitle}</div>
        <div className="form-group">
          <label className="form-label">{t.accounts.col.platform}</label>
          <select className="form-select" value={platform} onChange={e => setPlatform(e.target.value)}>
            {accountPlatforms.map(p => <option key={p} value={p}>{platformName(p)}</option>)}
          </select>
        </div>
        <div className="form-group">
          <label className="form-label">{t.accounts.login}</label>
          <input className="form-input" maxLength={100} autoComplete="off" spellCheck={false} value={login} onChange={e => setLogin(e.target.value)} />
        </div>
        <div className="form-group">
          <label className="form-label">{t.accounts.secret}</label>
          <input className="form-input" type="password" maxLength={200} autoComplete="new-password" value={secret} onChange={e => setSecret(e.target.value)} />
          <div className="form-hint">{t.accounts.secretHint}</div>
        </div>
        <div className="form-hint">{t.accounts.twoFactor}</div>
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={!login.trim() || busy} onClick={save}>{t.add}</button>
        </div>
      </div>
    </div>
  );
}

function SecretDialog({ account, toast, onClose, onDone }: { account: Account; toast: ToastFn; onClose: () => void; onDone: () => void }) {
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    const res = await postJson(`/api/game-accounts/${account.id}/secret`, { secret }, 'PUT');
    setBusy(false);
    setSecret('');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    onDone();
  };
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.accounts.rotateTitle, { login: account.loginMask })}</div>
        <div className="form-group">
          <label className="form-label">{t.accounts.newSecret}</label>
          <input className="form-input" type="password" maxLength={200} autoComplete="new-password" value={secret} onChange={e => setSecret(e.target.value)} />
          <div className="form-hint">{t.accounts.secretHint}</div>
        </div>
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={!secret || busy} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}
