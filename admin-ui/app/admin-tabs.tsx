'use client';
import { useCallback, useEffect, useState } from 'react';
import { Me, api, errorText, getJson, postJson } from './api';
import { roleLabel } from './auth';
import { currency, fmt, locale, t } from './i18n';
import { AuditVerify } from './pos';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;

const fmtDateTime = (iso: string) => {
  const d = new Date(iso);
  return `${d.toLocaleDateString(locale)} ${d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
};
const failed = async (res: Response) => `${t.common.errorPrefix}: ${await errorText(res)}`;

// ===== Audit log (Owner, Manager; read-only) =====
type AuditEntry = {
  id: number; createdUtc: string; actorUsername: string | null; actorRole: string | null; action: string;
  targetType: string | null; targetId: string | null; summary: string | null; detailsJson: string | null; ip: string | null;
  hash?: string | null; prevHash?: string | null;
};

const auditFilters = (): { prefix: string; label: string }[] => [
  { prefix: '', label: t.common.all },
  { prefix: 'device.unlock_without_session', label: t.auditPage.unlockUnpaid },
  { prefix: 'device.emergency_unlock', label: t.audit.emergency },
  { prefix: 'session.free_time', label: t.audit.free },
  { prefix: 'shift.', label: t.audit.shifts },
  { prefix: 'sale.', label: t.audit.bar },
  { prefix: 'stock.', label: '📦 ' + t.nav.stock },
  { prefix: 'product.', label: '🛒 ' + t.stock.name },
  { prefix: 'player.debt', label: t.audit.debt },
  { prefix: 'player.', label: t.auditPage.balances },
  { prefix: 'session.', label: t.auditPage.sessions },
  { prefix: 'tariff.', label: t.auditPage.tariffs },
  { prefix: 'package.', label: t.audit.packages },
  { prefix: 'billing.', label: t.audit.billing },
  { prefix: 'auth.', label: t.auditPage.logins },
  { prefix: 'staff.', label: t.auditPage.staff },
  { prefix: 'device.', label: t.auditPage.pcs },
  { prefix: 'settings.', label: t.auditPage.settings },
];

const alertActions = new Set(['device.unlock_without_session', 'auth.login_failed', 'player.balance_adjusted', 'player.bonus_adjusted',
  'player.refund', 'settings.staff_pin_removed', 'device.emergency_unlock', 'session.free_time', 'stock.corrected', 'sale.refunded',
  'shift.cash_payout', 'player.debt_written_off', 'product.price_changed', 'settings.currency_changed']);

/** Owner: recomputes the hash chain of the whole audit log on the server. */
function VerifyChain() {
  const [result, setResult] = useState<AuditVerify | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async () => {
    setBusy(true);
    setResult(await getJson<AuditVerify>('/api/audit/verify'));
    setBusy(false);
  };
  return (
    <div className="section-card chain-box">
      <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
        <button className="btn btn-primary btn-sm" disabled={busy} onClick={run}>{busy ? '...' : t.audit.verify}</button>
        <span className="auth-sub" style={{ margin: 0 }}>{t.audit.verifyHint}</span>
      </div>
      {result && (
        <div className={result.ok ? 'chain-ok' : 'auth-error'} role="status">
          {result.ok
            ? fmt(t.audit.verifyOk, { rows: result.rows, hash: (result.headHash ?? '').slice(0, 16), anchors: result.anchorsChecked })
            : fmt(t.audit.verifyBroken, { id: result.firstBrokenId ?? '?', problem: t.audit.problem[result.problem ?? ''] ?? result.problem ?? '' })}
        </div>
      )}
    </div>
  );
}

export function AuditTab({ owner = false }: { owner?: boolean }) {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [filter, setFilter] = useState('');
  const [actor, setActor] = useState('');
  const [loading, setLoading] = useState(false);
  const [more, setMore] = useState(false);

  const load = useCallback(async (beforeId?: number) => {
    setLoading(true);
    const params = new URLSearchParams({ take: '100' });
    if (filter) params.set('action', filter);
    if (actor.trim()) params.set('actor', actor.trim());
    if (beforeId) params.set('beforeId', String(beforeId));
    const page = await getJson<AuditEntry[]>(`/api/audit?${params}`) ?? [];
    setEntries(prev => (beforeId ? [...prev, ...page] : page));
    setMore(page.length === 100);
    setLoading(false);
  }, [filter, actor]);

  useEffect(() => { load(); }, [load]);

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.auditPage.title}</span>
        <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>{t.auditPage.readOnly}</span>
      </div>
      {owner && <VerifyChain />}
      <div className="filter-bar">
        {auditFilters().map(f => (
          <button key={f.prefix} className={`filter-chip ${filter === f.prefix ? 'active' : ''}`} onClick={() => setFilter(f.prefix)}>{f.label}</button>
        ))}
        <input className="form-input" style={{ width: 180, padding: '4px 10px' }} placeholder={t.auditPage.actor}
          value={actor} onChange={e => setActor(e.target.value)} />
        <button className="btn btn-sm" onClick={() => load()} title={t.refresh}>↻</button>
      </div>
      <div className="table-wrapper">
        <table className="data-table">
          <thead><tr>
            <th>{t.auditPage.col.time}</th><th>{t.auditPage.col.who}</th><th>{t.auditPage.col.action}</th>
            <th>{t.auditPage.col.description}</th><th>{t.auditPage.col.ip}</th>
          </tr></thead>
          <tbody>
            {entries.length === 0 ? (
              <tr><td colSpan={5} style={{ textAlign: 'center', padding: 40, color: 'var(--text-muted)' }}>{loading ? '...' : t.auditPage.empty}</td></tr>
            ) : entries.map(e => (
              <tr key={e.id}>
                <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(e.createdUtc)}</td>
                <td>{e.actorUsername ? <>{e.actorUsername}<br /><span className="user-chip">{roleLabel(e.actorRole ?? '')}</span></> : <span className="user-chip">{t.none}</span>}</td>
                <td><span className={`badge ${alertActions.has(e.action) ? 'badge-red' : 'badge-blue'}`}>{e.action}</span></td>
                <td>
                  {e.summary}
                  {e.detailsJson && <div className="audit-details">{e.detailsJson}</div>}
                  {e.hash && <div className="audit-hash" title={`${t.audit.hash}: ${e.hash}\nprev: ${e.prevHash ?? ''}`}>#{e.id} · {e.hash.slice(0, 12)}…</div>}
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{e.ip || t.none}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {more && (
        <div style={{ textAlign: 'center', marginTop: 12 }}>
          <button className="btn" disabled={loading} onClick={() => load(entries[entries.length - 1]?.id)}>{t.more}</button>
        </div>
      )}
    </>
  );
}

// ===== Staff accounts (Owner) =====
type Staff = { id: string; username: string; displayName: string; role: string; isActive: boolean; createdUtc: string; lastLoginUtc: string | null };
const roles = ['Owner', 'Manager', 'Cashier', 'Tech'];

export function StaffTab({ me, toast }: { me: Me; toast: ToastFn }) {
  const [staff, setStaff] = useState<Staff[]>([]);
  const [form, setForm] = useState({ username: '', displayName: '', password: '', role: 'Cashier' });

  const load = useCallback(async () => setStaff(await getJson<Staff[]>('/api/staff') ?? []), []);
  useEffect(() => { load(); }, [load]);

  const create = async () => {
    const res = await postJson('/api/staff', form);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    toast(fmt(t.staff.created, { username: form.username }), 'success');
    setForm({ username: '', displayName: '', password: '', role: 'Cashier' });
    load();
  };

  const update = async (s: Staff, change: Partial<Pick<Staff, 'role' | 'isActive'>>) => {
    const res = await postJson(`/api/staff/${s.id}`, change, 'PUT');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    toast(t.staff.saved, 'success');
    load();
  };

  const resetPassword = async (s: Staff) => {
    const password = window.prompt(fmt(t.staff.resetPrompt, { username: s.username }));
    if (!password) return;
    const res = await postJson(`/api/staff/${s.id}/password`, { password });
    toast(res.ok ? t.staff.passwordChanged : await failed(res), res.ok ? 'success' : 'error');
  };

  return (
    <>
      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">{t.staff.newTitle}</span></div>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); create(); }}>
          <input className="form-input" placeholder={t.staff.username} value={form.username} onChange={e => setForm({ ...form, username: e.target.value })} />
          <input className="form-input" placeholder={t.staff.displayName} value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder={t.staff.password} value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} />
          <select className="form-select" value={form.role} onChange={e => setForm({ ...form, role: e.target.value })}>
            {roles.map(r => <option key={r} value={r}>{roleLabel(r)}</option>)}
          </select>
          <button className="btn btn-primary" type="submit" disabled={!form.username || form.password.length < 8}>{t.common.create}</button>
        </form>
      </div>
      <div className="table-wrapper">
        <table className="data-table">
          <thead><tr>
            <th>{t.staff.col.login}</th><th>{t.staff.col.name}</th><th>{t.staff.col.role}</th><th>{t.staff.col.status}</th>
            <th>{t.staff.col.lastLogin}</th><th>{t.staff.col.actions}</th>
          </tr></thead>
          <tbody>
            {staff.map(s => (
              <tr key={s.id}>
                <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.username}{s.id === me.id && t.staff.you}</td>
                <td>{s.displayName}</td>
                <td>
                  <select className="form-select" style={{ width: 170, padding: '4px 8px', fontSize: 12 }} value={s.role}
                    onChange={e => update(s, { role: e.target.value })}>
                    {roles.map(r => <option key={r} value={r}>{roleLabel(r)}</option>)}
                  </select>
                </td>
                <td><span className={`badge ${s.isActive ? 'badge-green' : 'badge-red'}`}>{s.isActive ? t.staff.active : t.staff.disabled}</span></td>
                <td>{s.lastLoginUtc ? fmtDateTime(s.lastLoginUtc) : t.none}</td>
                <td>
                  <div style={{ display: 'flex', gap: 4 }}>
                    <button className="btn btn-sm" onClick={() => update(s, { isActive: !s.isActive })}>{s.isActive ? t.staff.disable : t.staff.enable}</button>
                    <button className="btn btn-sm" onClick={() => resetPassword(s)}>{t.staff.passwordButton}</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

// ===== Club settings (Owner) =====
type CurrencyView = { code: string; symbol: string; nameUk: string; nameEn: string };
type Settings = {
  staffPinConfigured: boolean; staffPinUpdatedUtc: string | null; enrollmentTokenConfigured: boolean; allowPendingRegistration: boolean;
  currency?: CurrencyView | null; currencies?: CurrencyView[];
  language?: string; languages?: string[];
};

/** Language of the lock screens on the club PCs (uk, en); the admin UI language is a browser choice. */
function LockScreenLanguage({ settings, toast, onSaved }: { settings: Settings | null; toast: ToastFn; onSaved: () => void }) {
  const [code, setCode] = useState(settings?.language ?? 'uk');
  useEffect(() => { if (settings?.language) setCode(settings.language); }, [settings?.language]);
  const list = settings?.languages ?? [];
  const name = (c: string) => t.settings.languageNames[c] ?? c;

  const save = async () => {
    const res = await postJson('/api/settings/language', { code }, 'PUT');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    const saved: { code: string } = await res.json();
    toast(fmt(t.settings.languageSaved, { name: name(saved.code) }), 'success');
    onSaved();
  };

  return (
    <div className="section-card">
      <div className="section-card-header"><span className="section-card-title">{t.settings.languageTitle}</span></div>
      <p className="auth-sub">{t.settings.languageHint}</p>
      <form className="staff-form" onSubmit={e => { e.preventDefault(); save(); }}>
        <label className="form-label" htmlFor="club-language" style={{ alignSelf: 'center', margin: 0 }}>{t.settings.languageLabel}</label>
        <select id="club-language" className="form-select" value={code} onChange={e => setCode(e.target.value)}>
          {list.map(c => <option key={c} value={c}>{name(c)}</option>)}
        </select>
        <button className="btn btn-primary" type="submit" disabled={!list.length || code === settings?.language}>{t.settings.languageSave}</button>
      </form>
    </div>
  );
}

/** Club currency (display only): UAH, EUR, USD, PLN. The amounts in the database are never converted. */
function CurrencySettings({ settings, toast, onCurrency }: { settings: Settings | null; toast: ToastFn; onCurrency: (code: string) => void }) {
  const [code, setCode] = useState(settings?.currency?.code ?? currency());
  useEffect(() => { if (settings?.currency) setCode(settings.currency.code); }, [settings?.currency]);
  const list = settings?.currencies ?? [];
  const name = (c: CurrencyView) => `${t.settings.currencyNames[c.code] ?? c.code} (${c.symbol}, ${c.code})`;

  const save = async () => {
    const res = await postJson('/api/settings/currency', { code }, 'PUT');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    const saved: CurrencyView = await res.json();
    toast(fmt(t.settings.currencySaved, { name: name(saved) }), 'success');
    onCurrency(saved.code);
  };

  return (
    <div className="section-card">
      <div className="section-card-header"><span className="section-card-title">{t.settings.currencyTitle}</span></div>
      <p className="auth-sub">{t.settings.currencyHint}</p>
      <form className="staff-form" onSubmit={e => { e.preventDefault(); save(); }}>
        <label className="form-label" htmlFor="club-currency" style={{ alignSelf: 'center', margin: 0 }}>{t.settings.currencyLabel}</label>
        <select id="club-currency" className="form-select" value={code} onChange={e => setCode(e.target.value)}>
          {list.map(c => <option key={c.code} value={c.code}>{name(c)}</option>)}
        </select>
        <button className="btn btn-primary" type="submit" disabled={!list.length || code === (settings?.currency?.code ?? currency())}>{t.settings.currencySave}</button>
      </form>
    </div>
  );
}

export function SettingsTab({ toast, onCurrency }: { toast: ToastFn; onCurrency: (code: string) => void }) {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [pin, setPin] = useState('');
  const [pin2, setPin2] = useState('');
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');

  const load = useCallback(async () => setSettings(await getJson<Settings>('/api/settings')), []);
  useEffect(() => { load(); }, [load]);

  const savePin = async () => {
    if (pin !== pin2) { toast(t.settings.pinMismatch, 'error'); return; }
    const res = await postJson('/api/settings/staff-pin', { pin }, 'PUT');
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    toast(t.settings.pinSaved, 'success');
    setPin(''); setPin2('');
    load();
  };

  const removePin = async () => {
    if (!window.confirm(t.settings.removeConfirm)) return;
    const res = await api('/api/settings/staff-pin', { method: 'DELETE' });
    toast(res.ok ? t.settings.pinRemoved : await failed(res), res.ok ? 'info' : 'error');
    load();
  };

  const changePassword = async () => {
    const res = await postJson('/api/auth/password', { currentPassword: current, newPassword: next });
    toast(res.ok ? t.settings.passwordChanged : await failed(res), res.ok ? 'success' : 'error');
    if (res.ok) { setCurrent(''); setNext(''); }
  };

  return (
    <>
      <CurrencySettings settings={settings} toast={toast} onCurrency={code => { onCurrency(code); load(); }} />
      <LockScreenLanguage settings={settings} toast={toast} onSaved={load} />

      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">{t.settings.pinTitle}</span></div>
        <p className="auth-sub">
          {t.settings.pinHint}{' '}
          {t.settings.pinState}: {settings
            ? (settings.staffPinConfigured ? fmt(t.settings.pinSet, { time: settings.staffPinUpdatedUtc ? fmtDateTime(settings.staffPinUpdatedUtc) : '' }) : t.settings.pinNotSet)
            : '...'}
        </p>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); savePin(); }}>
          <input className="form-input" type="password" autoComplete="new-password" placeholder={t.settings.newPin} value={pin} onChange={e => setPin(e.target.value)} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder={t.settings.repeatPin} value={pin2} onChange={e => setPin2(e.target.value)} />
          <button className="btn btn-primary" type="submit" disabled={pin.length < 8}>{t.settings.savePin}</button>
          {settings?.staffPinConfigured && <button className="btn btn-danger" type="button" onClick={removePin}>{t.settings.removePin}</button>}
        </form>
      </div>

      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">{t.settings.enrollTitle}</span></div>
        <p className="auth-sub">
          {settings?.enrollmentTokenConfigured ? t.settings.tokenSet : t.settings.tokenNotSet}
          {' '}{settings?.allowPendingRegistration ? t.settings.pendingWait : t.settings.pendingRefused}
          {' '}{t.settings.clientHint}
        </p>
      </div>

      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">{t.settings.passwordTitle}</span></div>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); changePassword(); }}>
          <input className="form-input" type="password" autoComplete="current-password" placeholder={t.settings.currentPassword} value={current} onChange={e => setCurrent(e.target.value)} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder={t.settings.newPassword} value={next} onChange={e => setNext(e.target.value)} />
          <button className="btn btn-primary" type="submit" disabled={!current || next.length < 8}>{t.settings.change}</button>
        </form>
      </div>
    </>
  );
}
