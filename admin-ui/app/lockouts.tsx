'use client';
import { useCallback, useEffect, useState } from 'react';
import { errorText, getJson, postJson } from './api';
import { clubClock, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Lockouts = {
  accounts: { id: string; username: string | null; lockedUntilUtc: string }[];
  devices: { deviceId: string; hostname: string; lockedUntilUtc: string }[];
};

/**
 * Player login lockouts on the lock screens (Owner, Manager): a name or a PC after too many failed logins.
 * A name that is no account is shown without its text (players type their password into the name field).
 */
export function LoginLockouts({ toast }: { toast: ToastFn }) {
  const [data, setData] = useState<Lockouts | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => setData(await getJson<Lockouts>('/api/player-lockouts')), []);
  useEffect(() => {
    load();
    const iv = setInterval(load, 10000);
    return () => clearInterval(iv);
  }, [load]);

  const clear = async (key: string, body: object) => {
    setBusy(key);
    const res = await postJson('/api/player-lockouts/clear', body);
    setBusy(null);
    toast(res.ok ? t.lockouts.cleared : `${t.common.errorPrefix}: ${await errorText(res)}`, res.ok ? 'success' : 'error');
    load();
  };

  if (!data) return null;
  const rows = [
    ...data.accounts.map(a => ({ key: 'a' + a.id, kind: t.lockouts.account, name: a.username ? '@' + a.username : t.lockouts.unknownName, until: a.lockedUntilUtc, body: { id: a.id } })),
    ...data.devices.map(d => ({ key: 'd' + d.deviceId, kind: t.lockouts.pc, name: d.hostname, until: d.lockedUntilUtc, body: { deviceId: d.deviceId } })),
  ];
  return (
    <div className="section-card">
      <div className="section-card-header"><span className="section-card-title">{t.lockouts.title}</span></div>
      <div className="form-hint" style={{ marginBottom: 10 }}>{t.lockouts.hint}</div>
      {rows.length === 0 ? <div className="form-hint">{t.lockouts.empty}</div> : (
        <div className="table-wrapper">
          <table className="data-table">
            <thead><tr><th>{t.lockouts.account} / {t.lockouts.pc}</th><th>{t.lockouts.until}</th><th></th></tr></thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.key}>
                  <td><span className="badge badge-red">{r.kind}</span> {r.name}</td>
                  <td>{clubClock(r.until)}</td>
                  <td><button className="btn btn-sm" disabled={busy === r.key} onClick={() => clear(r.key, r.body)}>{t.lockouts.clear}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
