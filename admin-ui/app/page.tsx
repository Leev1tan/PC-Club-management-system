'use client';
import { useState, useEffect, useCallback, useRef } from 'react';
import { AuthStatus, Me, api, errorText, getJson, permissions, postJson, setUnauthorizedHandler } from './api';
import { LoginScreen, SetupScreen, roleLabel } from './auth';
import { AuditTab, SettingsTab, StaffTab } from './admin-tabs';
import { Tariff } from './billing';
import { fmt as fmtText, lang, methodLabel, money, setLang, t } from './i18n';
import { PlayerCard } from './player-card';
import { PosTab } from './pos-tab';
import { EndSessionDialog, StartSessionDialog, TransferDialog } from './session-dialogs';
import { ShiftTab } from './shift-tab';
import { StockTab } from './stock-tab';
import { TariffsTab } from './tariffs-tab';

// ===== Types =====
type Zone = { id: string; name: string; color: string; sortOrder: number };
type User = { id: string; username: string; displayName: string; balance: number; bonusPoints: number; createdUtc: string; debt?: number };
type Device = {
  id: string; hostname: string; osVersion: string; agentVersion: string;
  lastSeenUtc: string | null; lastIp: string | null; status: string;
  zoneId: string | null; zoneName: string | null; zoneColor: string | null;
  positionX: number | null; positionY: number | null;
  occupancyStatus: string; activeSessionId: string | null; activeUserId: string | null; sessionEndUtc: string | null;
  isLocked?: boolean | null; pendingCommands?: number;
  enrollment?: string; // pending (waits for approval), approved, revoked
  sessionPausedUtc?: string | null; sessionCutoffUtc?: string | null; sessionLabel?: string | null; sessionIsPackage?: boolean;
};
type Session = {
  id: string; deviceId: string; deviceHostname: string | null;
  tariffId: string | null; tariffName: string | null; pricePerHour: number | null;
  userId: string | null; username: string | null;
  startUtc: string; endUtc: string | null; status: string;
  totalCost: number; isPrepaid: boolean;
  packageId?: string | null; packageName?: string | null; pausedUtc?: string | null; cutoffUtc?: string | null;
  paymentMethod?: string | null; paidMinutes?: number | null; currentCost?: number | null;
};
type Revenue = { today: number; week: number; month: number; currency: string };
type Transaction = { id: string; userId: string; amount: number; type: string; description: string | null; createdUtc: string };
type Toast = { id: number; message: string; type: 'success' | 'error' | 'info' };
type DemoInfo = { enabled: boolean; resetIntervalHours: number | null; lastResetUtc: string | null; nextResetUtc: string | null };

// ===== Tab IDs =====
type Tab = 'dashboard' | 'map' | 'devices' | 'sessions' | 'pos' | 'shift' | 'stock' | 'users' | 'tariffs' | 'finance' | 'audit' | 'staff' | 'settings';
type Need = 'any' | 'frontDesk' | 'management' | 'owner';

// Each tab is shown only to the roles that may use it (the server checks every request again)
const allTabs: { id: Tab; label: string; icon: string; section?: string; need: Need }[] = [
  { id: 'dashboard', label: t.nav.dashboard, icon: '📊', section: t.nav.sectionMain, need: 'frontDesk' },
  { id: 'map', label: t.nav.map, icon: '🗺️', need: 'any' },
  { id: 'sessions', label: t.nav.sessions, icon: '⏱️', need: 'frontDesk' },
  { id: 'pos', label: t.nav.pos, icon: '🛒', section: t.nav.sectionDesk, need: 'frontDesk' },
  { id: 'shift', label: t.nav.shift, icon: '🧾', need: 'frontDesk' },
  { id: 'stock', label: t.nav.stock, icon: '📦', need: 'frontDesk' },
  { id: 'devices', label: t.nav.devices, icon: '🖥️', section: t.nav.sectionManage, need: 'any' },
  { id: 'users', label: t.nav.users, icon: '👥', need: 'frontDesk' },
  { id: 'tariffs', label: t.nav.tariffs, icon: '💰', need: 'frontDesk' },
  { id: 'finance', label: t.nav.finance, icon: '📈', section: t.nav.sectionAnalytics, need: 'management' },
  { id: 'audit', label: t.nav.audit, icon: '📜', need: 'management' },
  { id: 'staff', label: t.nav.staff, icon: '🧑‍💼', section: t.nav.sectionAdmin, need: 'owner' },
  { id: 'settings', label: t.nav.settings, icon: '⚙️', need: 'owner' },
];

// ===== Helpers =====
const fmt = (n: number) => n.toFixed(2);
const fmtTime = (iso: string) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit', year: 'numeric' });
const fmtDateTime = (iso: string) => `${fmtDate(iso)} ${fmtTime(iso)}`;

/** Remaining time; a paused session keeps the time it had at the pause. */
function timeRemaining(endUtc: string | null, pausedUtc?: string | null): string {
  if (!endUtc) return 'Відкрита';
  const diff = new Date(endUtc).getTime() - (pausedUtc ? new Date(pausedUtc).getTime() : Date.now());
  if (diff <= 0) return 'Завершено';
  const m = Math.floor(diff / 60000);
  const h = Math.floor(m / 60);
  return h > 0 ? `${h}г ${m % 60}хв` : `${m}хв`;
}

function timeAgo(iso: string | null): string {
  if (!iso) return 'Ніколи';
  const diff = Date.now() - new Date(iso).getTime();
  const m = Math.floor(diff / 60000);
  if (m < 1) return 'Щойно';
  if (m < 60) return `${m}хв тому`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}г тому`;
  return `${Math.floor(h / 24)}д тому`;
}

// ===== Auth gate: setup screen, login page or the admin UI =====
export default function Page() {
  const [status, setStatus] = useState<AuthStatus | null>(null);
  const [me, setMe] = useState<Me | null | undefined>(undefined); // undefined = still loading

  const refresh = useCallback(async () => {
    const [st, who] = await Promise.all([getJson<AuthStatus>('/api/auth/status'), getJson<Me>('/api/auth/me')]);
    setStatus(st);
    setMe(who);
  }, []);

  useEffect(() => { refresh(); }, [refresh]);
  useEffect(() => { document.documentElement.lang = lang; }, []);
  useEffect(() => {
    // The cookie expired or the account changed: back to the login page
    setUnauthorizedHandler(() => setMe(null));
    return () => setUnauthorizedHandler(null);
  }, []);

  const logout = async () => {
    await postJson('/api/auth/logout').catch(() => undefined);
    setMe(null);
    refresh();
  };

  if (me === undefined) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', color: 'var(--text-muted)' }}>
        <div style={{ fontSize: 14 }}>Завантаження...</div>
      </div>
    );
  }
  if (!me && status?.setupRequired) return <SetupScreen onDone={setMe} />;
  if (!me) return <LoginScreen status={status} onLogin={setMe} />;
  return <AdminPage key={me.id + me.role} me={me} onLogout={logout} />;
}

// ===== Main =====
function AdminPage({ me, onLogout }: { me: Me; onLogout: () => void }) {
  const can = permissions(me.role);
  const tabs = allTabs.filter(t => t.need === 'any' || can[t.need]);
  const [activeTab, setActiveTab] = useState<Tab>(can.frontDesk ? 'dashboard' : 'map');
  const [devices, setDevices] = useState<Device[]>([]);
  const [zones, setZones] = useState<Zone[]>([]);
  const [tariffs, setTariffs] = useState<Tariff[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [revenue, setRevenue] = useState<Revenue | null>(null);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [loading, setLoading] = useState(true);
  const [demo, setDemo] = useState<DemoInfo | null>(null);
  const [zoneFilter, setZoneFilter] = useState<string | null>(null);
  const [sessionFilter, setSessionFilter] = useState<'all' | 'active' | 'ended'>('all');

  // Modal state
  const [zoneModal, setZoneModal] = useState(false);
  const [playerModal, setPlayerModal] = useState(false);
  // Billing dialogs (top-level components: background refreshes do not reset them)
  const [startDialog, setStartDialog] = useState<{ id: string; hostname: string; zoneId: string | null } | null>(null);
  const [transferTarget, setTransferTarget] = useState<Session | null>(null);
  const [endTarget, setEndTarget] = useState<Session | null>(null);
  const [playerCard, setPlayerCard] = useState<{ user: User; form?: 'topup' } | null>(null);
  const [pcPopover, setPcPopover] = useState<string | null>(null);

  const toast = useCallback((message: string, type: Toast['type'] = 'info') => {
    const id = Date.now();
    setToasts(t => [...t, { id, message, type }]);
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 3500);
  }, []);

  // Only what the role may read (Tech: PCs and zones; Cashier: no revenue)
  const fetchAll = useCallback(async () => {
    const [d, z, t, u, s, r] = await Promise.all([
      getJson<Device[]>('/api/devices'),
      getJson<Zone[]>('/api/zones'),
      can.frontDesk ? getJson<Tariff[]>('/api/tariffs') : Promise.resolve(null),
      can.frontDesk ? getJson<User[]>('/api/users') : Promise.resolve(null),
      can.frontDesk ? getJson<Session[]>('/api/sessions') : Promise.resolve(null),
      can.management ? getJson<Revenue>('/api/sessions/revenue') : Promise.resolve(null),
    ]);
    if (d) setDevices(d);
    if (z) setZones(z);
    if (t) setTariffs(t);
    if (u) setUsers(u);
    if (s) setSessions(s);
    if (r) setRevenue(r);
    setLoading(false);
  }, [can.frontDesk, can.management]);

  // The tab and modal components below are declared inside this component, so every
  // re-render remounts them and resets their inputs. Auto-refresh therefore pauses
  // while a modal is open.
  const modalOpen = !!(zoneModal || playerModal);
  const modalOpenRef = useRef(false);
  useEffect(() => { modalOpenRef.current = modalOpen; }, [modalOpen]);

  useEffect(() => {
    fetchAll();
    const iv = setInterval(() => { if (!modalOpenRef.current) fetchAll(); }, 5000);
    return () => clearInterval(iv);
  }, [fetchAll]);

  useEffect(() => {
    getJson<DemoInfo>('/api/demo').then(setDemo);
  }, []);

  // ===== API Actions =====
  const sendCommand = async (deviceId: string, type: string, payload?: object) => {
    const res = await postJson(`/api/devices/${deviceId}/commands`, { type, payload: payload || null });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(`Команду "${type}" надіслано`, 'success');
    fetchAll();
    setTimeout(fetchAll, 2500); // the agent usually confirms within 1-2 s
  };

  const sendMessage = (deviceId: string, hostname: string) => {
    const text = window.prompt(`Повідомлення на екран ${hostname}:`);
    if (text && text.trim()) sendCommand(deviceId, 'message', { text: text.trim() });
  };

  const extendSession = async (sessionId: string, minutes: number) => {
    const res = await postJson(`/api/sessions/${sessionId}/extend`, { minutes });
    if (res.ok) { toast(fmtText(t.session.extended, { minutes }), 'success'); fetchAll(); }
    else toast(`Помилка: ${await errorText(res)}`, 'error');
  };

  const pauseOrResume = async (sessionId: string, paused: boolean) => {
    const res = await postJson(`/api/sessions/${sessionId}/${paused ? 'resume' : 'pause'}`);
    if (res.ok) { toast(paused ? t.session.resumedToast : t.session.pausedToast, 'success'); fetchAll(); }
    else toast(`Помилка: ${await errorText(res)}`, 'error');
  };

  // The end dialog asks how an open-ended session is paid; it needs the session row
  const endSession = async (sessionId: string) => {
    const session = sessions.find(x => x.id === sessionId)
      ?? (await getJson<Session[]>('/api/sessions/active'))?.find(x => x.id === sessionId);
    if (session) { setEndTarget(session); return; }
    toast(t.errors.session_not_found, 'error');
  };

  const transferSession = async (sessionId: string) => {
    const session = sessions.find(x => x.id === sessionId)
      ?? (await getJson<Session[]>('/api/sessions/active'))?.find(x => x.id === sessionId);
    if (session) setTransferTarget(session);
  };

  const createZone = async (name: string, color: string) => {
    const res = await postJson('/api/zones', { name, color, sortOrder: zones.length });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(`Зону "${name}" створено ✓`, 'success');
    fetchAll();
  };

  const deleteZone = async (id: string) => {
    const res = await api(`/api/zones/${id}`, { method: 'DELETE' });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast('Зону видалено', 'info');
    fetchAll();
  };

  const assignZone = async (deviceId: string, zoneId: string | null) => {
    const res = await postJson(`/api/devices/${deviceId}/zone`, { zoneId }, 'PUT');
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast('Зону призначено ✓', 'success');
    fetchAll();
  };

  // Approval of PCs that registered without the enrollment token
  const deviceAction = async (deviceId: string, action: 'approve' | 'reject' | 'revoke', hostname: string) => {
    if (action !== 'approve' && !window.confirm(action === 'revoke'
      ? `Відкликати ${hostname}? Ключ ПК перестане працювати (історія залишиться).`
      : `Відхилити ${hostname}?`)) return;
    const res = await postJson(`/api/devices/${deviceId}/${action}`);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(action === 'approve' ? `${hostname} схвалено ✓` : action === 'reject' ? `${hostname} відхилено` : `${hostname} відкликано`, 'success');
    fetchAll();
  };

  const createPlayer = async (username: string, displayName: string, password: string) => {
    const res = await postJson('/api/users/register', { username, displayName, password });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(`Гравця @${username} створено ✓`, 'success');
    fetchAll();
  };

  const setPlayerPin = async (userId: string, username: string) => {
    const password = window.prompt(`Новий пароль або PIN для @${username} (мін. 4 символи):`);
    if (!password) return;
    const res = await postJson(`/api/users/${userId}/password`, { password });
    toast(res.ok ? 'Пароль гравця змінено ✓' : `Помилка: ${await errorText(res)}`, res.ok ? 'success' : 'error');
  };


  // ===== Computed =====
  const activeSessions = sessions.filter(s => s.status === 'active');
  const onlineDevices = devices.filter(d => d.status === 'online');
  const freeDevices = devices.filter(d => d.occupancyStatus === 'free');
  const occupiedDevices = devices.filter(d => d.occupancyStatus === 'occupied');

  // ===== Render Helpers =====
  const sessionLabel = (s: Session) => s.packageName
    ? <><span className="badge badge-amber">{t.session.packageBadge}</span> {s.packageName}</>
    : <span className="badge badge-blue">{s.tariffName || '—'}</span>;
  const sessionLeft = (s: Session) => s.pausedUtc
    ? <span style={{ color: 'var(--amber)' }}>⏸ {t.session.paused} · {timeRemaining(s.endUtc, s.pausedUtc)}</span>
    : <span style={{ color: 'var(--blue)' }}>{timeRemaining(s.endUtc)}</span>;
  const sessionCost = (s: Session) => s.status === 'active' && !s.isPrepaid
    ? <span title={t.session.current}>{money(s.currentCost ?? 0)} ₴ …</span>
    : <>{money(s.totalCost)} ₴</>;
  const sessionActions = (s: Session) => (
    <div style={{ display: 'flex', gap: 4 }}>
      <button className="btn btn-sm" title={s.pausedUtc ? t.session.resume : t.session.pause} onClick={() => pauseOrResume(s.id, !!s.pausedUtc)}>{s.pausedUtc ? '▶️' : '⏸️'}</button>
      <button className="btn btn-sm" title={t.session.transfer} onClick={() => transferSession(s.id)}>🔀</button>
      <button className="btn btn-danger btn-sm" onClick={() => endSession(s.id)}>{t.session.end}</button>
    </div>
  );

  const renderTabContent = () => {
    switch (activeTab) {
      case 'dashboard': return <DashboardTab />;
      case 'map': return <MapTab />;
      case 'devices': return <DevicesTab />;
      case 'sessions': return <SessionsTab />;
      case 'users': return <UsersTab />;
      case 'tariffs': return <TariffsTab can={can} zones={zones} devices={devices} tariffs={tariffs} toast={toast}
        onChanged={fetchAll} onAddZone={() => setZoneModal(true)} onDeleteZone={deleteZone} />;
      case 'pos': return <PosTab can={can} players={users} toast={toast} onChanged={fetchAll} />;
      case 'shift': return <ShiftTab can={can} toast={toast} onChanged={fetchAll} />;
      case 'stock': return <StockTab can={can} toast={toast} />;
      case 'finance': return <FinanceTab />;
      case 'audit': return <AuditTab />;
      case 'staff': return <StaffTab me={me} toast={toast} />;
      case 'settings': return <SettingsTab toast={toast} />;
    }
  };

  // ===== Dashboard Tab =====
  function DashboardTab() {
    return (
      <>
        <div className="stats-grid">
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--accent-glow)' }}>🖥️</div>
              <span className="stat-card-label">Всього ПК</span>
            </div>
            <div className="stat-card-value">{devices.length}</div>
            <div className="stat-card-sub">{onlineDevices.length} онлайн</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--green-glow)' }}>✅</div>
              <span className="stat-card-label">Вільних</span>
            </div>
            <div className="stat-card-value">{freeDevices.length}</div>
            <div className="stat-card-sub">Готові до гри</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--blue-glow)' }}>🎮</div>
              <span className="stat-card-label">Активні сесії</span>
            </div>
            <div className="stat-card-value">{activeSessions.length}</div>
            <div className="stat-card-sub">Зараз грають</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--amber-glow)' }}>💰</div>
              <span className="stat-card-label">Дохід сьогодні</span>
            </div>
            <div className="stat-card-value">{revenue ? `${fmt(revenue.today)} ₴` : '—'}</div>
            <div className="stat-card-sub">Тиждень: {revenue ? `${fmt(revenue.week)} ₴` : '—'}</div>
          </div>
        </div>

        {/* Active Sessions */}
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">🎮 Активні сесії</span>
          </div>
          {activeSessions.length === 0 ? (
            <div className="empty-state">
              <div className="empty-state-icon">😴</div>
              <div className="empty-state-title">Немає активних сесій</div>
              <div className="empty-state-sub">Перейдіть на Карту ПК щоб розпочати сесію</div>
            </div>
          ) : (
            <div className="table-wrapper">
              <table className="data-table">
                <thead><tr>
                  <th>ПК</th><th>Гравець</th><th>Тариф</th><th>Залишилось</th><th>Вартість</th><th></th>
                </tr></thead>
                <tbody>
                  {activeSessions.map(s => (
                    <tr key={s.id}>
                      <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.deviceHostname || '—'}</td>
                      <td>{s.username || 'Гість'}</td>
                      <td>{sessionLabel(s)}</td>
                      <td>{sessionLeft(s)}</td>
                      <td>{sessionCost(s)}</td>
                      <td>{sessionActions(s)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        {/* Zone Summary */}
        {zones.length > 0 && (
          <div className="section-card">
            <div className="section-card-header">
              <span className="section-card-title">🗺️ Зони</span>
            </div>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              {zones.map(z => {
                const zoneDevices = devices.filter(d => d.zoneId === z.id);
                const zoneFree = zoneDevices.filter(d => d.occupancyStatus === 'free').length;
                return (
                  <div key={z.id} className="stat-card" style={{ minWidth: 150, flex: '1 1 150px', borderLeftColor: z.color, borderLeftWidth: 3 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
                      <span className="zone-dot" style={{ background: z.color }} />
                      <span style={{ fontWeight: 600, fontSize: 14 }}>{z.name}</span>
                    </div>
                    <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>
                      {zoneDevices.length} ПК · {zoneFree} вільних
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </>
    );
  }

  // ===== PC Map Tab =====
  function MapTab() {
    const filtered = zoneFilter ? devices.filter(d => d.zoneId === zoneFilter) : devices;

    return (
      <>
        <div className="page-header">
          <span className="page-title">Карта ПК</span>
          <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>
            {freeDevices.length} вільних · {occupiedDevices.length} зайнятих
          </span>
        </div>

        {zones.length > 0 && (
          <div className="filter-bar">
            <button className={`filter-chip ${!zoneFilter ? 'active' : ''}`} onClick={() => setZoneFilter(null)}>Всі</button>
            {zones.map(z => (
              <button key={z.id} className={`filter-chip ${zoneFilter === z.id ? 'active' : ''}`}
                onClick={() => setZoneFilter(zoneFilter === z.id ? null : z.id)}
                style={zoneFilter === z.id ? { borderColor: z.color, color: z.color } : {}}>
                <span className="zone-dot" style={{ background: z.color, marginRight: 4 }} />
                {z.name}
              </button>
            ))}
          </div>
        )}

        {filtered.length === 0 ? (
          <div className="empty-state">
            <div className="empty-state-icon">🖥️</div>
            <div className="empty-state-title">Немає пристроїв</div>
            <div className="empty-state-sub">Встановіть агент на ПК щоб вони з&apos;явились тут</div>
          </div>
        ) : (
          <div className="pc-map">
            {filtered.map(d => (
              <div key={d.id} className={`pc-card status-${d.occupancyStatus} ${d.sessionPausedUtc ? 'paused' : ''}`}
                style={{ borderLeftColor: d.zoneColor || undefined }}
                onClick={() => setPcPopover(pcPopover === d.id ? null : d.id)}>
                <div className="pc-card-name">{d.hostname}</div>
                <div className="pc-card-status">
                  <span className={`dot ${d.sessionPausedUtc ? 'amber' : d.occupancyStatus === 'free' ? 'green' : d.occupancyStatus === 'occupied' ? 'blue' : 'red'}`} />
                  {d.sessionPausedUtc ? t.session.paused : d.occupancyStatus === 'free' ? 'Вільний' : d.occupancyStatus === 'occupied' ? 'Зайнятий' : 'Офлайн'}
                </div>
                {d.sessionEndUtc && <div className="pc-card-timer">{d.sessionPausedUtc ? '⏸ ' : ''}{timeRemaining(d.sessionEndUtc, d.sessionPausedUtc)}</div>}
                {d.sessionLabel && <div className="pc-card-zone" title={d.sessionLabel}>{d.sessionIsPackage ? '🎟️ ' : ''}{d.sessionLabel}</div>}
                {d.zoneName && <div className="pc-card-zone">{d.zoneName}</div>}
                <div className="pc-card-flags">
                  {d.isLocked === true && <span title="Заблоковано">🔒</span>}
                  {d.isLocked === false && <span title="Розблоковано">🔓</span>}
                  {(d.pendingCommands ?? 0) > 0 && <span title="Команди чекають на підтвердження від ПК">⏳ {d.pendingCommands}</span>}
                </div>

                {pcPopover === d.id && (
                  <div className="popover" onClick={e => e.stopPropagation()}>
                    {can.frontDesk && d.occupancyStatus === 'free' && d.enrollment !== 'pending' && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); setStartDialog({ id: d.id, hostname: d.hostname, zoneId: d.zoneId }); }}>
                        🎮 Розпочати сесію
                      </button>
                    )}
                    {can.frontDesk && d.activeSessionId && d.sessionEndUtc && !d.sessionPausedUtc && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); extendSession(d.activeSessionId!, 30); }}>
                        {t.session.extend30}
                      </button>
                    )}
                    {can.frontDesk && d.activeSessionId && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); pauseOrResume(d.activeSessionId!, !!d.sessionPausedUtc); }}>
                        {d.sessionPausedUtc ? t.session.resume : t.session.pause}
                      </button>
                    )}
                    {can.frontDesk && d.activeSessionId && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); transferSession(d.activeSessionId!); }}>
                        {t.session.transfer}
                      </button>
                    )}
                    {can.frontDesk && d.activeSessionId && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); endSession(d.activeSessionId!); }}>
                        ⏹️ Завершити сесію
                      </button>
                    )}
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'lock'); }}>🔒 Заблокувати</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'unlock'); }}>🔓 Розблокувати</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'restart'); }}>🔄 Перезавантажити</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendMessage(d.id, d.hostname); }}>💬 Повідомлення</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'logoff'); }}>🚪 Вийти з системи</button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </>
    );
  }

  // ===== Devices Tab =====
  function DevicesTab() {
    return (
      <>
        <div className="page-header">
          <span className="page-title">Пристрої</span>
          <span className="badge badge-green">{onlineDevices.length} онлайн</span>
        </div>

        {devices.length === 0 ? (
          <div className="empty-state">
            <div className="empty-state-icon">🖥️</div>
            <div className="empty-state-title">Немає пристроїв</div>
            <div className="empty-state-sub">Агент автоматично зареєструє ПК при першому підключенні</div>
          </div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>Ім&apos;я</th><th>Статус</th><th>Підключення</th><th>Зона</th><th>IP</th><th>ОС</th><th>Останній зв&apos;язок</th><th>Дії</th>
              </tr></thead>
              <tbody>
                {devices.map(d => (
                  <tr key={d.id}>
                    <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{d.hostname}</td>
                    <td>
                      <span className={`badge ${d.status === 'online' ? 'badge-green' : 'badge-red'}`}>
                        {d.status === 'online' ? 'Онлайн' : 'Офлайн'}
                      </span>
                    </td>
                    <td>
                      <span className={`badge ${d.enrollment === 'pending' ? 'badge-amber' : d.enrollment === 'revoked' ? 'badge-red' : 'badge-green'}`}>
                        {d.enrollment === 'pending' ? 'Чекає схвалення' : d.enrollment === 'revoked' ? 'Відкликано' : 'Схвалено'}
                      </span>
                    </td>
                    <td>
                      <select className="form-select" style={{ width: 140, padding: '4px 8px', fontSize: 12 }} disabled={!can.deviceAdmin}
                        value={d.zoneId || ''} onChange={e => assignZone(d.id, e.target.value || null)}>
                        <option value="">Без зони</option>
                        {zones.map(z => <option key={z.id} value={z.id}>{z.name}</option>)}
                      </select>
                    </td>
                    <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{d.lastIp || '—'}</td>
                    <td style={{ fontSize: 12 }}>{d.osVersion || '—'}</td>
                    <td>{timeAgo(d.lastSeenUtc)}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 4 }}>
                        {d.enrollment === 'pending' ? (can.deviceAdmin && <>
                          <button className="btn btn-success btn-sm" onClick={() => deviceAction(d.id, 'approve', d.hostname)}>Схвалити</button>
                          <button className="btn btn-danger btn-sm" onClick={() => deviceAction(d.id, 'reject', d.hostname)}>Відхилити</button>
                        </>) : d.enrollment === 'revoked' ? null : <>
                          <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'restart')} title="Перезавантажити">🔄</button>
                          <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'lock')} title="Заблокувати">🔒</button>
                          <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'logoff')} title="Вийти">🚪</button>
                          {can.deviceAdmin && <button className="btn btn-danger btn-sm" onClick={() => deviceAction(d.id, 'revoke', d.hostname)} title="Відкликати ключ ПК">⛔</button>}
                        </>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </>
    );
  }

  // ===== Sessions Tab =====
  function SessionsTab() {
    const filter = sessionFilter;
    const setFilter = setSessionFilter;
    const filtered = filter === 'all' ? sessions : sessions.filter(s => s.status === filter);

    return (
      <>
        <div className="page-header">
          <span className="page-title">Сесії</span>
          <div className="filter-bar" style={{ marginBottom: 0 }}>
            {(['all', 'active', 'ended'] as const).map(f => (
              <button key={f} className={`filter-chip ${filter === f ? 'active' : ''}`} onClick={() => setFilter(f)}>
                {f === 'all' ? 'Всі' : f === 'active' ? 'Активні' : 'Завершені'}
              </button>
            ))}
          </div>
        </div>

        <div className="table-wrapper">
          <table className="data-table">
            <thead><tr>
              <th>ПК</th><th>Гравець</th><th>Тариф</th><th>Початок</th><th>Залишилось</th><th>Вартість</th><th>Статус</th><th></th>
            </tr></thead>
            <tbody>
              {filtered.length === 0 ? (
                <tr><td colSpan={8} style={{ textAlign: 'center', padding: 40, color: 'var(--text-muted)' }}>Немає сесій</td></tr>
              ) : filtered.map(s => (
                <tr key={s.id}>
                  <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.deviceHostname || '—'}</td>
                  <td>{s.username || 'Гість'}</td>
                  <td>{sessionLabel(s)}</td>
                  <td>{fmtDateTime(s.startUtc)}</td>
                  <td style={{ color: s.status === 'active' ? 'var(--blue)' : 'var(--text-muted)' }}>
                    {s.status === 'active' ? sessionLeft(s) : s.endUtc ? fmtTime(s.endUtc) : '—'}
                  </td>
                  <td style={{ fontWeight: 600 }}>{sessionCost(s)}</td>
                  <td>
                    <span className={`badge ${s.status !== 'active' ? 'badge-amber' : s.pausedUtc ? 'badge-amber' : 'badge-green'}`}>
                      {s.status !== 'active' ? 'Завершена' : s.pausedUtc ? t.session.paused : 'Активна'}
                    </span>
                    <div className="user-chip">{methodLabel(s.paymentMethod)}</div>
                  </td>
                  <td>{s.status === 'active' && sessionActions(s)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </>
    );
  }

  // ===== Users Tab =====
  function UsersTab() {
    return (
      <>
        <div className="page-header">
          <span className="page-title">Гравці</span>
          <span style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
            <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>{users.length} зареєстровано</span>
            <button className="btn btn-primary btn-sm" onClick={() => setPlayerModal(true)}>+ Новий гравець</button>
          </span>
        </div>

        {users.length === 0 ? (
          <div className="empty-state">
            <div className="empty-state-icon">👥</div>
            <div className="empty-state-title">Немає гравців</div>
            <div className="empty-state-sub">Гравці з&apos;являться після реєстрації через клієнт</div>
          </div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>Логін</th><th>Ім&apos;я</th><th>Баланс</th><th>Бонуси</th><th>Зареєстрований</th><th>Дії</th>
              </tr></thead>
              <tbody>
                {users.map(u => (
                  <tr key={u.id}>
                    <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>@{u.username}</td>
                    <td>{u.displayName}</td>
                    <td style={{ fontWeight: 600, color: u.balance > 0 ? 'var(--green)' : 'var(--text-muted)' }}>{fmt(u.balance)} ₴</td>
                    <td style={{ color: u.bonusPoints > 0 ? 'var(--amber)' : 'var(--text-muted)' }}>
                      {money(u.bonusPoints)} ₴ 🎁
                      {(u.debt ?? 0) > 0 && <div><span className="badge badge-red">{t.player.debt}: {money(u.debt)} ₴</span></div>}
                    </td>
                    <td>{fmtDate(u.createdUtc)}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 4 }}>
                        <button className="btn btn-sm" onClick={() => setPlayerCard({ user: u })}>{t.player.card}</button>
                        <button className="btn btn-success btn-sm" onClick={() => setPlayerCard({ user: u, form: 'topup' })}>
                          {t.player.topUp}
                        </button>
                        <button className="btn btn-sm" onClick={() => setPlayerPin(u.id, u.username)} title="Пароль / PIN">🔑</button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </>
    );
  }

  // ===== Finance Tab =====
  function FinanceTab() {
    return (
      <>
        <div className="page-header">
          <span className="page-title">Фінанси</span>
        </div>

        <div className="stats-grid">
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--green-glow)' }}>📅</div>
              <span className="stat-card-label">Сьогодні</span>
            </div>
            <div className="stat-card-value" style={{ color: 'var(--green)' }}>{revenue ? `${fmt(revenue.today)} ₴` : '—'}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--blue-glow)' }}>📊</div>
              <span className="stat-card-label">Тиждень</span>
            </div>
            <div className="stat-card-value" style={{ color: 'var(--blue)' }}>{revenue ? `${fmt(revenue.week)} ₴` : '—'}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--accent-glow)' }}>💎</div>
              <span className="stat-card-label">Місяць</span>
            </div>
            <div className="stat-card-value">{revenue ? `${fmt(revenue.month)} ₴` : '—'}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--amber-glow)' }}>👥</div>
              <span className="stat-card-label">Гравців</span>
            </div>
            <div className="stat-card-value">{users.length}</div>
            <div className="stat-card-sub">Зареєстровано</div>
          </div>
        </div>

        {/* Session Revenue by Zone */}
        {zones.length > 0 && (
          <div className="section-card">
            <div className="section-card-header">
              <span className="section-card-title">Дохід по зонах</span>
            </div>
            <div className="table-wrapper">
              <table className="data-table">
                <thead><tr><th>Зона</th><th>ПК</th><th>Активних сесій</th><th>Завантаженість</th></tr></thead>
                <tbody>
                  {zones.map(z => {
                    const zd = devices.filter(d => d.zoneId === z.id);
                    const occupied = zd.filter(d => d.occupancyStatus === 'occupied').length;
                    const util = zd.length > 0 ? Math.round(occupied / zd.length * 100) : 0;
                    return (
                      <tr key={z.id}>
                        <td>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                            <span className="zone-dot" style={{ background: z.color }} />
                            <span style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{z.name}</span>
                          </div>
                        </td>
                        <td>{zd.length}</td>
                        <td>{occupied}</td>
                        <td>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                            <div style={{ flex: 1, height: 6, background: 'var(--bg-glass)', borderRadius: 3, overflow: 'hidden' }}>
                              <div style={{ height: '100%', width: `${util}%`, background: z.color, borderRadius: 3, transition: 'width 0.3s' }} />
                            </div>
                            <span style={{ fontSize: 12, fontWeight: 600 }}>{util}%</span>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {/* Recent ended sessions */}
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">Останні транзакції</span>
          </div>
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr><th>ПК</th><th>Гравець</th><th>Час</th><th>Тариф</th><th>Сума</th></tr></thead>
              <tbody>
                {sessions.filter(s => s.status === 'ended').slice(0, 20).map(s => (
                  <tr key={s.id}>
                    <td>{s.deviceHostname || '—'}</td>
                    <td>{s.username || 'Гість'}</td>
                    <td>{fmtDateTime(s.startUtc)}</td>
                    <td>{s.tariffName || '—'}</td>
                    <td style={{ fontWeight: 600, color: 'var(--green)' }}>{fmt(s.totalCost)} ₴</td>
                  </tr>
                ))}
                {sessions.filter(s => s.status === 'ended').length === 0 && (
                  <tr><td colSpan={5} style={{ textAlign: 'center', padding: 30, color: 'var(--text-muted)' }}>Ще немає транзакцій</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      </>
    );
  }

  // ===== Modals =====
  function ZoneModal() {
    if (!zoneModal) return null;
    const [name, setName] = useState('');
    const [color, setColor] = useState('#6366f1');
    const colors = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#3b82f6', '#a855f7', '#06b6d4', '#ec4899'];

    return (
      <div className="modal-overlay" onClick={() => setZoneModal(false)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">🗺️ Нова зона</div>
          <div className="form-group">
            <label className="form-label">Назва</label>
            <input className="form-input" placeholder="напр. VIP, Стандарт, Консолі..." value={name} onChange={e => setName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">Колір</label>
            <div style={{ display: 'flex', gap: 8 }}>
              {colors.map(c => (
                <div key={c} onClick={() => setColor(c)}
                  style={{
                    width: 32, height: 32, borderRadius: 8, background: c, cursor: 'pointer',
                    border: color === c ? '2px solid white' : '2px solid transparent', transition: 'border 0.15s'
                  }} />
              ))}
            </div>
          </div>
          <div className="modal-actions">
            <button className="btn" onClick={() => setZoneModal(false)}>Скасувати</button>
            <button className="btn btn-primary" disabled={!name.trim()} onClick={() => { createZone(name.trim(), color); setZoneModal(false); }}>
              Створити
            </button>
          </div>
        </div>
      </div>
    );
  }

  function PlayerModal() {
    if (!playerModal) return null;
    const [username, setUsername] = useState('');
    const [displayName, setDisplayName] = useState('');
    const [password, setPassword] = useState('');

    return (
      <div className="modal-overlay" onClick={() => setPlayerModal(false)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">👤 Новий гравець</div>
          <div className="form-group">
            <label className="form-label">Логін</label>
            <input className="form-input" value={username} onChange={e => setUsername(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">Ім&apos;я</label>
            <input className="form-input" value={displayName} onChange={e => setDisplayName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">Пароль або PIN (мін. 4) — для входу на екрані блокування</label>
            <input className="form-input" type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} />
          </div>
          <div className="modal-actions">
            <button className="btn" onClick={() => setPlayerModal(false)}>Скасувати</button>
            <button className="btn btn-primary" disabled={!username.trim() || password.length < 4}
              onClick={() => { createPlayer(username.trim(), displayName.trim(), password); setPlayerModal(false); }}>
              Створити
            </button>
          </div>
        </div>
      </div>
    );
  }

  // ===== Main Layout =====
  if (loading) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', color: 'var(--text-muted)' }}>
        <div style={{ textAlign: 'center' }}>
          <div style={{ fontSize: 48, marginBottom: 12, animation: 'pulse 1.5s infinite' }}>🎮</div>
          <div style={{ fontSize: 14 }}>Завантаження...</div>
        </div>
      </div>
    );
  }

  return (
    <div className="app-layout">
      {/* Sidebar */}
      <aside className="sidebar">
        <div className="sidebar-header">
          <div className="sidebar-logo">
            <div className="sidebar-logo-icon">🎮</div>
            <div>
              <div className="sidebar-logo-text">PC Club</div>
              <div className="sidebar-logo-sub">Management</div>
            </div>
          </div>
        </div>
        <nav className="sidebar-nav">
          {tabs.map(tab => (
            <div key={tab.id}>
              {tab.section && <div className="sidebar-section">{tab.section}</div>}
              <button className={`nav-item ${activeTab === tab.id ? 'active' : ''}`} onClick={() => { setActiveTab(tab.id); setPcPopover(null); }}>
                <span className="icon">{tab.icon}</span>
                <span>{tab.label}</span>
                {tab.id === 'sessions' && activeSessions.length > 0 && (
                  <span className="nav-badge">{activeSessions.length}</span>
                )}
              </button>
            </div>
          ))}
        </nav>
      </aside>

      {/* Main */}
      <main className="main-content">
        {demo?.enabled && (
          <div className="demo-banner">
            <strong>LIVE DEMO</strong> · {demo.resetIntervalHours
              ? `Дані скидаються кожні ${demo.resetIntervalHours} год · Data resets every ${demo.resetIntervalHours} h`
              : 'Дані скидаються при перезапуску · Data resets on restart'}
            {demo.nextResetUtc && ` · наступне / next: ${fmtTime(demo.nextResetUtc)}`}
            {' · ПК симульовані / PCs are simulated'}
          </div>
        )}
        <div className="top-bar">
          <span className="top-bar-title">
            {tabs.find(t => t.id === activeTab)?.icon} {tabs.find(t => t.id === activeTab)?.label}
          </span>
          <div className="top-bar-actions">
            <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {onlineDevices.length}/{devices.length} ПК онлайн
            </span>
            <span className="lang-switch" role="group" aria-label={t.lang.label}>
              {(['uk', 'en'] as const).map(l => (
                <button key={l} className={`btn btn-sm ${lang === l ? 'active' : ''}`} aria-pressed={lang === l} onClick={() => lang !== l && setLang(l)}>{t.lang[l]}</button>
              ))}
            </span>
            <span className="user-chip" title={roleLabel(me.role)}>👤 {me.displayName} · {roleLabel(me.role)}</span>
            <button className="btn btn-sm" onClick={onLogout}>{t.nav.logout}</button>
          </div>
        </div>
        <div className="content-area">
          {renderTabContent()}
        </div>
      </main>

      {/* Modals */}
      <ZoneModal />
      <PlayerModal />
      {startDialog && (
        <StartSessionDialog device={startDialog} tariffs={tariffs} players={users} toast={toast}
          onClose={() => setStartDialog(null)} onDone={() => { setStartDialog(null); fetchAll(); }} />
      )}
      {transferTarget && (
        <TransferDialog session={transferTarget} devices={devices} players={users} toast={toast}
          onClose={() => setTransferTarget(null)} onDone={() => { setTransferTarget(null); fetchAll(); }} />
      )}
      {endTarget && (
        <EndSessionDialog session={endTarget} players={users} toast={toast}
          onClose={() => setEndTarget(null)} onDone={() => { setEndTarget(null); fetchAll(); }} />
      )}
      {playerCard && (
        <PlayerCard key={playerCard.user.id} player={playerCard.user} can={can} toast={toast} initialForm={playerCard.form ?? null}
          onClose={() => setPlayerCard(null)} onChanged={fetchAll} />
      )}

      {/* Toasts */}
      <div className="toast-container">
        {toasts.map(t => (
          <div key={t.id} className={`toast toast-${t.type}`}>{t.message}</div>
        ))}
      </div>
    </div>
  );
}
