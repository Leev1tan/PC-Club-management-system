'use client';
import { useCallback, useEffect, useState } from 'react';
import { Me, api, errorText, getJson, postJson } from './api';
import { roleLabel } from './auth';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;

const fmtDateTime = (iso: string) => {
  const d = new Date(iso);
  return `${d.toLocaleDateString('uk-UA')} ${d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
};

// ===== Audit log (Owner, Manager; read-only) =====
type AuditEntry = {
  id: number; createdUtc: string; actorUsername: string | null; actorRole: string | null; action: string;
  targetType: string | null; targetId: string | null; summary: string | null; detailsJson: string | null; ip: string | null;
};

const auditFilters: { prefix: string; label: string }[] = [
  { prefix: '', label: 'Всі / All' },
  { prefix: 'device.unlock_without_session', label: 'Розблок. без оплати / Unlock unpaid' },
  { prefix: 'player.', label: 'Баланси / Balances' },
  { prefix: 'session.', label: 'Сесії / Sessions' },
  { prefix: 'tariff.', label: 'Тарифи / Tariffs' },
  { prefix: 'auth.', label: 'Входи / Logins' },
  { prefix: 'staff.', label: 'Персонал / Staff' },
  { prefix: 'device.', label: 'ПК / PCs' },
  { prefix: 'settings.', label: 'Налаштування / Settings' },
];

const alertActions = new Set(['device.unlock_without_session', 'auth.login_failed', 'player.balance_adjusted', 'settings.staff_pin_removed']);

export function AuditTab() {
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
        <span className="page-title">Журнал дій / Audit log</span>
        <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>Лише читання · записи не змінюються й не видаляються</span>
      </div>
      <div className="filter-bar">
        {auditFilters.map(f => (
          <button key={f.prefix} className={`filter-chip ${filter === f.prefix ? 'active' : ''}`} onClick={() => setFilter(f.prefix)}>{f.label}</button>
        ))}
        <input className="form-input" style={{ width: 180, padding: '4px 10px' }} placeholder="Логін / user name"
          value={actor} onChange={e => setActor(e.target.value)} />
        <button className="btn btn-sm" onClick={() => load()}>↻</button>
      </div>
      <div className="table-wrapper">
        <table className="data-table">
          <thead><tr><th>Час</th><th>Хто</th><th>Дія</th><th>Опис</th><th>IP</th></tr></thead>
          <tbody>
            {entries.length === 0 ? (
              <tr><td colSpan={5} style={{ textAlign: 'center', padding: 40, color: 'var(--text-muted)' }}>{loading ? '...' : 'Немає записів'}</td></tr>
            ) : entries.map(e => (
              <tr key={e.id}>
                <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(e.createdUtc)}</td>
                <td>{e.actorUsername ? <>{e.actorUsername}<br /><span className="user-chip">{e.actorRole}</span></> : <span className="user-chip">—</span>}</td>
                <td><span className={`badge ${alertActions.has(e.action) ? 'badge-red' : 'badge-blue'}`}>{e.action}</span></td>
                <td>
                  {e.summary}
                  {e.detailsJson && <div className="audit-details">{e.detailsJson}</div>}
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{e.ip || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {more && (
        <div style={{ textAlign: 'center', marginTop: 12 }}>
          <button className="btn" disabled={loading} onClick={() => load(entries[entries.length - 1]?.id)}>Ще / More</button>
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
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(`Акаунт ${form.username} створено ✓`, 'success');
    setForm({ username: '', displayName: '', password: '', role: 'Cashier' });
    load();
  };

  const update = async (s: Staff, change: Partial<Pick<Staff, 'role' | 'isActive'>>) => {
    const res = await postJson(`/api/staff/${s.id}`, change, 'PUT');
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast('Збережено ✓ (активні входи цього акаунта завершено)', 'success');
    load();
  };

  const resetPassword = async (s: Staff) => {
    const password = window.prompt(`Новий пароль для ${s.username} (мін. 8 символів):`);
    if (!password) return;
    const res = await postJson(`/api/staff/${s.id}/password`, { password });
    toast(res.ok ? 'Пароль змінено ✓' : `Помилка: ${await errorText(res)}`, res.ok ? 'success' : 'error');
  };

  return (
    <>
      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">👤 Новий акаунт персоналу / New staff account</span></div>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); create(); }}>
          <input className="form-input" placeholder="Логін / user name" value={form.username} onChange={e => setForm({ ...form, username: e.target.value })} />
          <input className="form-input" placeholder="Ім'я / display name" value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder="Пароль (мін. 8)" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} />
          <select className="form-select" value={form.role} onChange={e => setForm({ ...form, role: e.target.value })}>
            {roles.map(r => <option key={r} value={r}>{roleLabel(r)}</option>)}
          </select>
          <button className="btn btn-primary" type="submit" disabled={!form.username || form.password.length < 8}>Створити</button>
        </form>
      </div>
      <div className="table-wrapper">
        <table className="data-table">
          <thead><tr><th>Логін</th><th>Ім&apos;я</th><th>Роль</th><th>Статус</th><th>Останній вхід</th><th>Дії</th></tr></thead>
          <tbody>
            {staff.map(s => (
              <tr key={s.id}>
                <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.username}{s.id === me.id && ' (ви)'}</td>
                <td>{s.displayName}</td>
                <td>
                  <select className="form-select" style={{ width: 170, padding: '4px 8px', fontSize: 12 }} value={s.role}
                    onChange={e => update(s, { role: e.target.value })}>
                    {roles.map(r => <option key={r} value={r}>{roleLabel(r)}</option>)}
                  </select>
                </td>
                <td><span className={`badge ${s.isActive ? 'badge-green' : 'badge-red'}`}>{s.isActive ? 'Активний' : 'Вимкнено'}</span></td>
                <td>{s.lastLoginUtc ? fmtDateTime(s.lastLoginUtc) : '—'}</td>
                <td>
                  <div style={{ display: 'flex', gap: 4 }}>
                    <button className="btn btn-sm" onClick={() => update(s, { isActive: !s.isActive })}>{s.isActive ? 'Вимкнути' : 'Увімкнути'}</button>
                    <button className="btn btn-sm" onClick={() => resetPassword(s)}>Пароль</button>
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
type Settings = { staffPinConfigured: boolean; staffPinUpdatedUtc: string | null; enrollmentTokenConfigured: boolean; allowPendingRegistration: boolean };

export function SettingsTab({ toast }: { toast: ToastFn }) {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [pin, setPin] = useState('');
  const [pin2, setPin2] = useState('');
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');

  const load = useCallback(async () => setSettings(await getJson<Settings>('/api/settings')), []);
  useEffect(() => { load(); }, [load]);

  const savePin = async () => {
    if (pin !== pin2) { toast('PIN не збігається', 'error'); return; }
    const res = await postJson('/api/settings/staff-pin', { pin }, 'PUT');
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast('PIN персоналу збережено ✓ ПК отримають його за кілька секунд', 'success');
    setPin(''); setPin2('');
    load();
  };

  const removePin = async () => {
    if (!window.confirm('Видалити PIN? Аварійне розблокування на ПК стане неможливим.')) return;
    const res = await api('/api/settings/staff-pin', { method: 'DELETE' });
    toast(res.ok ? 'PIN видалено' : `Помилка: ${await errorText(res)}`, res.ok ? 'info' : 'error');
    load();
  };

  const changePassword = async () => {
    const res = await postJson('/api/auth/password', { currentPassword: current, newPassword: next });
    toast(res.ok ? 'Пароль змінено ✓' : `Помилка: ${await errorText(res)}`, res.ok ? 'success' : 'error');
    if (res.ok) { setCurrent(''); setNext(''); }
  };

  return (
    <>
      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">🔑 PIN персоналу для екрана блокування / Lock-screen staff PIN</span></div>
        <p className="auth-sub">
          Ctrl+Shift+U на ПК + цей PIN розблоковує ПК аварійно, також коли сервер або агент не працює.
          Сервер зберігає лише хеш (PBKDF2); ПК перевіряють PIN локально. Мінімум 8 символів, краще цифри й літери.
          Стан: {settings ? (settings.staffPinConfigured ? `задано ${settings.staffPinUpdatedUtc ? fmtDateTime(settings.staffPinUpdatedUtc) : ''}` : 'НЕ задано — аварійного розблокування немає') : '...'}
        </p>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); savePin(); }}>
          <input className="form-input" type="password" autoComplete="new-password" placeholder="Новий PIN" value={pin} onChange={e => setPin(e.target.value)} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder="Повторіть PIN" value={pin2} onChange={e => setPin2(e.target.value)} />
          <button className="btn btn-primary" type="submit" disabled={pin.length < 8}>Зберегти PIN</button>
          {settings?.staffPinConfigured && <button className="btn btn-danger" type="button" onClick={removePin}>Видалити PIN</button>}
        </form>
      </div>

      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">🖥️ Підключення ПК / PC enrollment</span></div>
        <p className="auth-sub">
          Токен підключення: {settings?.enrollmentTokenConfigured ? 'задано на сервері (CMS_ENROLLMENT_TOKEN)' : 'не задано'}.
          {' '}ПК без токена: {settings?.allowPendingRegistration ? 'чекають на схвалення у вкладці «Пристрої»' : 'відхиляються'}.
          {' '}Клієнт: ClubClientSetup.exe -ServerUrl &quot;http://сервер:5081&quot; -EnrollmentToken &quot;…&quot;
        </p>
      </div>

      <div className="section-card">
        <div className="section-card-header"><span className="section-card-title">🔒 Мій пароль / My password</span></div>
        <form className="staff-form" onSubmit={e => { e.preventDefault(); changePassword(); }}>
          <input className="form-input" type="password" autoComplete="current-password" placeholder="Поточний пароль" value={current} onChange={e => setCurrent(e.target.value)} />
          <input className="form-input" type="password" autoComplete="new-password" placeholder="Новий пароль (мін. 8)" value={next} onChange={e => setNext(e.target.value)} />
          <button className="btn btn-primary" type="submit" disabled={!current || next.length < 8}>Змінити</button>
        </form>
      </div>
    </>
  );
}
