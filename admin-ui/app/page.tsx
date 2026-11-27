'use client';
import { useState, useEffect, useCallback, useRef } from 'react';
import { AuthStatus, Me, api, errorText, getJson, permissions, postJson, setUnauthorizedHandler } from './api';
import { LangSwitch, LoginScreen, SetupScreen, roleLabel } from './auth';
import { AnalyticsTab } from './analytics-tab';
import { AuditTab, SettingsTab, StaffTab } from './admin-tabs';
import { Tariff } from './billing';
import { cash, fmt as fmtText, lang, locale, methodLabel, money, setClubZone, setCurrency, t } from './i18n';
import { PlayerCard } from './player-card';
import { PosTab } from './pos-tab';
import { EndSessionDialog, StartSessionDialog, TransferDialog } from './session-dialogs';
import { GamesTab } from './games-tab';
import { LoginLockouts } from './lockouts';
import { CheckInDialog, ReservationsTab } from './reservations-tab';
import { EventsTab, PcEvent } from './events-tab';
import { PcReservation } from './reservations';
import { OrderQueue, OrdersTab, orderChime } from './orders-tab';
import { PowerAction, PowerDialog, PowerTarget } from './power-dialog';
import { ShiftTab } from './shift-tab';
import { StockTab } from './stock-tab';
import { TariffsTab } from './tariffs-tab';

// ===== Types =====
type Zone = { id: string; name: string; color: string; sortOrder: number };
type User = { id: string; username: string; displayName: string; balance: number; bonusPoints: number; createdUtc: string; debt?: number; tier?: string | null };
type Device = {
  id: string; hostname: string; osVersion: string; agentVersion: string;
  lastSeenUtc: string | null; lastIp: string | null; status: string;
  zoneId: string | null; zoneName: string | null; zoneColor: string | null;
  positionX: number | null; positionY: number | null;
  occupancyStatus: string; activeSessionId: string | null; activeUserId: string | null; sessionEndUtc: string | null;
  isLocked?: boolean | null; pendingCommands?: number;
  enrollment?: string; // pending (waits for approval), approved, revoked
  sessionPausedUtc?: string | null; sessionCutoffUtc?: string | null; sessionLabel?: string | null; sessionIsPackage?: boolean;
  currentGame?: string | null; currentGameColor?: string | null; currentGameStartedUtc?: string | null;
  macAddress?: string | null;
  reservation?: PcReservation | null;
  event?: PcEvent | null;
};
type Session = {
  id: string; deviceId: string; deviceHostname: string | null;
  tariffId: string | null; tariffName: string | null; pricePerHour: number | null;
  userId: string | null; username: string | null;
  startUtc: string; endUtc: string | null; status: string;
  totalCost: number; isPrepaid: boolean;
  packageId?: string | null; packageName?: string | null; pausedUtc?: string | null; cutoffUtc?: string | null;
  paymentMethod?: string | null; paidMinutes?: number | null; currentCost?: number | null;
  limitToBalance?: boolean; balanceEndUtc?: string | null;
};
type Revenue = { today: number; week: number; month: number; currency: string };
type Transaction = { id: string; userId: string; amount: number; type: string; description: string | null; createdUtc: string };
type Toast = { id: number; message: string; type: 'success' | 'error' | 'info' };
type DemoInfo = { enabled: boolean; resetIntervalHours: number | null; lastResetUtc: string | null; nextResetUtc: string | null };

// ===== Tab IDs =====
type Tab = 'dashboard' | 'map' | 'devices' | 'sessions' | 'reservations' | 'events' | 'orders' | 'pos' | 'shift' | 'stock' | 'users' | 'tariffs' | 'games' | 'analytics' | 'audit' | 'staff' | 'settings';
type Need = 'any' | 'frontDesk' | 'management' | 'owner';

// Each tab is shown only to the roles that may use it (the server checks every request again)
const allTabs: { id: Tab; label: string; icon: string; section?: string; need: Need }[] = [
  { id: 'dashboard', label: t.nav.dashboard, icon: '📊', section: t.nav.sectionMain, need: 'frontDesk' },
  { id: 'map', label: t.nav.map, icon: '🗺️', need: 'any' },
  { id: 'sessions', label: t.nav.sessions, icon: '⏱️', need: 'frontDesk' },
  { id: 'reservations', label: t.nav.reservations, icon: '📅', need: 'frontDesk' },
  { id: 'events', label: t.nav.events, icon: '🏆', need: 'frontDesk' },
  { id: 'pos', label: t.nav.pos, icon: '🛒', section: t.nav.sectionDesk, need: 'frontDesk' },
  { id: 'orders', label: t.nav.orders, icon: '🥤', need: 'frontDesk' },
  { id: 'shift', label: t.nav.shift, icon: '🧾', need: 'frontDesk' },
  { id: 'stock', label: t.nav.stock, icon: '📦', need: 'frontDesk' },
  { id: 'devices', label: t.nav.devices, icon: '🖥️', section: t.nav.sectionManage, need: 'any' },
  { id: 'users', label: t.nav.users, icon: '👥', need: 'frontDesk' },
  { id: 'tariffs', label: t.nav.tariffs, icon: '💰', need: 'frontDesk' },
  { id: 'games', label: t.nav.games, icon: '🎮', need: 'management' },
  { id: 'analytics', label: t.analytics.title, icon: '📈', section: t.nav.sectionAnalytics, need: 'management' },
  { id: 'audit', label: t.nav.audit, icon: '📜', need: 'management' },
  { id: 'staff', label: t.nav.staff, icon: '🧑‍💼', section: t.nav.sectionAdmin, need: 'owner' },
  { id: 'settings', label: t.nav.settings, icon: '⚙️', need: 'owner' },
];

// ===== Helpers =====
const fmtTime = (iso: string) => new Date(iso).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' });
const fmtDate = (iso: string) => new Date(iso).toLocaleDateString(locale, { day: '2-digit', month: '2-digit', year: 'numeric' });
const fmtDateTime = (iso: string) => `${fmtDate(iso)} ${fmtTime(iso)}`;

/** Remaining time; a paused session keeps the time it had at the pause. */
function timeRemaining(endUtc: string | null, pausedUtc?: string | null): string {
  if (!endUtc) return t.ago.open;
  const diff = new Date(endUtc).getTime() - (pausedUtc ? new Date(pausedUtc).getTime() : Date.now());
  if (diff <= 0) return t.ago.over;
  const m = Math.floor(diff / 60000);
  const h = Math.floor(m / 60);
  return h > 0 ? fmtText(t.ago.hm, { h, m: m % 60 }) : fmtText(t.ago.m, { m });
}

function timeAgo(iso: string | null): string {
  if (!iso) return t.ago.never;
  const diff = Date.now() - new Date(iso).getTime();
  const m = Math.floor(diff / 60000);
  if (m < 1) return t.ago.now;
  if (m < 60) return fmtText(t.ago.minutes, { n: m });
  const h = Math.floor(m / 60);
  if (h < 24) return fmtText(t.ago.hours, { n: h });
  return fmtText(t.ago.days, { n: Math.floor(h / 24) });
}

// ===== Auth gate: setup screen, login page or the admin UI =====
export default function Page() {
  const [status, setStatus] = useState<AuthStatus | null>(null);
  const [me, setMe] = useState<Me | null | undefined>(undefined); // undefined = still loading

  // The club currency comes with the signed-in staff member; set it before the admin page renders
  const accept = useCallback((who: Me | null) => {
    if (who) { setCurrency(who.currency); setClubZone(who.timeZone); }
    setMe(who);
  }, []);

  const refresh = useCallback(async () => {
    const [st, who] = await Promise.all([getJson<AuthStatus>('/api/auth/status'), getJson<Me>('/api/auth/me')]);
    setStatus(st);
    accept(who);
  }, [accept]);

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
    // No text: the static page is rendered in Ukrainian, the browser may show English (no mismatch)
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', color: 'var(--text-muted)' }}>
        <div style={{ fontSize: 40, animation: 'pulse 1.5s infinite' }} aria-busy="true">🎮</div>
      </div>
    );
  }
  if (!me && status?.setupRequired) return <SetupScreen onDone={accept} />;
  if (!me) return <LoginScreen status={status} onLogin={accept} />;
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
  const [, setCurrencyRev] = useState(0); // a new club currency renders every screen again
  const [sessionFilter, setSessionFilter] = useState<'all' | 'active' | 'ended'>('all');

  // Modal state
  const [zoneModal, setZoneModal] = useState(false);
  const [playerModal, setPlayerModal] = useState(false);
  // Billing dialogs (top-level components: background refreshes do not reset them)
  const [startDialog, setStartDialog] = useState<{ id: string; hostname: string; zoneId: string | null; reservation?: PcReservation | null } | null>(null);
  const [checkInId, setCheckInId] = useState<number | null>(null);
  // New bar orders from the PCs: a badge on the tab and a short tone when one more arrives
  const [newOrders, setNewOrders] = useState(0);
  const lastNewOrders = useRef<number | null>(null);
  const [transferTarget, setTransferTarget] = useState<Session | null>(null);
  const [endTarget, setEndTarget] = useState<Session | null>(null);
  const [playerCard, setPlayerCard] = useState<{ user: User; form?: 'topup' } | null>(null);
  const [pcPopover, setPcPopover] = useState<string | null>(null);
  const [power, setPower] = useState<{ target: PowerTarget; action: PowerAction } | null>(null);

  const toast = useCallback((message: string, type: Toast['type'] = 'info') => {
    const id = Date.now();
    setToasts(t => [...t, { id, message, type }]);
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 3500);
  }, []);

  // Only what the role may read (Tech: PCs and zones; Cashier: no revenue)
  const fetchAll = useCallback(async () => {
    if (can.frontDesk) {
      getJson<OrderQueue>('/api/orders').then(q => {
        if (!q) return;
        if (lastNewOrders.current !== null && q.newCount > lastNewOrders.current) orderChime();
        lastNewOrders.current = q.newCount;
        setNewOrders(q.newCount);
      });
    }
    const [d, z, t, u, s, r, who] = await Promise.all([
      getJson<Device[]>('/api/devices'),
      getJson<Zone[]>('/api/zones'),
      can.frontDesk ? getJson<Tariff[]>('/api/tariffs') : Promise.resolve(null),
      can.frontDesk ? getJson<User[]>('/api/users') : Promise.resolve(null),
      can.frontDesk ? getJson<Session[]>('/api/sessions') : Promise.resolve(null),
      can.management ? getJson<Revenue>('/api/sessions/revenue') : Promise.resolve(null),
      getJson<Me>('/api/auth/me'),
    ]);
    // The owner may change the club currency at any time: every screen follows within one refresh
    if (who && setCurrency(who.currency)) setCurrencyRev(v => v + 1);
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
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(fmtText(t.map.commandSent, { type }), 'success');
    fetchAll();
    setTimeout(fetchAll, 2500); // the agent usually confirms within 1-2 s
  };

  // Shutdown, reboot, Wake-on-LAN (Owner, Manager, Tech) after a confirmation
  const openPower = (d: Device, action: PowerAction) => {
    const player = d.activeUserId ? users.find(u => u.id === d.activeUserId) : null;
    setPower({
      action,
      target: {
        id: d.id, hostname: d.hostname, macAddress: d.macAddress, online: d.status === 'online',
        sessionOf: d.activeSessionId ? (player ? `@${player.username}` : d.activeUserId ? t.power.player : t.power.guest) : null,
      },
    });
  };

  const sendMessage = (deviceId: string, hostname: string) => {
    const text = window.prompt(fmtText(t.map.messagePrompt, { host: hostname }));
    if (text && text.trim()) sendCommand(deviceId, 'message', { text: text.trim() });
  };

  const extendSession = async (sessionId: string, minutes: number) => {
    const res = await postJson(`/api/sessions/${sessionId}/extend`, { minutes });
    if (res.ok) { toast(fmtText(t.session.extended, { minutes }), 'success'); fetchAll(); }
    else toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error');
  };

  const pauseOrResume = async (sessionId: string, paused: boolean) => {
    const res = await postJson(`/api/sessions/${sessionId}/${paused ? 'resume' : 'pause'}`);
    if (res.ok) { toast(paused ? t.session.resumedToast : t.session.pausedToast, 'success'); fetchAll(); }
    else toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error');
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
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(fmtText(t.zones.created, { name }), 'success');
    fetchAll();
  };

  const deleteZone = async (id: string) => {
    const res = await api(`/api/zones/${id}`, { method: 'DELETE' });
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(t.zones.deleted, 'info');
    fetchAll();
  };

  const assignZone = async (deviceId: string, zoneId: string | null) => {
    const res = await postJson(`/api/devices/${deviceId}/zone`, { zoneId }, 'PUT');
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(t.devices.zoneAssigned, 'success');
    fetchAll();
  };

  // Approval of PCs that registered without the enrollment token
  const deviceAction = async (deviceId: string, action: 'approve' | 'reject' | 'revoke', hostname: string) => {
    if (action !== 'approve' && !window.confirm(fmtText(action === 'revoke' ? t.devices.confirmRevoke : t.devices.confirmReject, { host: hostname }))) return;
    const res = await postJson(`/api/devices/${deviceId}/${action}`);
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(fmtText(action === 'approve' ? t.devices.approvedToast : action === 'reject' ? t.devices.rejectedToast : t.devices.revokedToast, { host: hostname }), 'success');
    fetchAll();
  };

  const createPlayer = async (username: string, displayName: string, password: string) => {
    const res = await postJson('/api/users/register', { username, displayName, password });
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(fmtText(t.players.created, { username }), 'success');
    fetchAll();
  };

  const setPlayerPin = async (userId: string, username: string) => {
    const password = window.prompt(fmtText(t.players.pinPrompt, { username }));
    if (!password) return;
    const res = await postJson(`/api/users/${userId}/password`, { password });
    toast(res.ok ? t.players.pinChanged : `${t.common.errorPrefix}: ${await errorText(res)}`, res.ok ? 'success' : 'error');
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
  // A session from the balance (player login) ends when the balance is used up: its planned end
  const sessionLeft = (s: Session) => s.pausedUtc
    ? <span style={{ color: 'var(--amber)' }}>⏸ {t.session.paused} · {timeRemaining(s.endUtc ?? s.balanceEndUtc ?? null, s.pausedUtc)}</span>
    : <span style={{ color: 'var(--blue)' }}>{timeRemaining(s.endUtc ?? s.balanceEndUtc ?? null)}
        {s.limitToBalance && <span className="badge badge-green" style={{ marginLeft: 6 }} title={t.session.fromBalanceHint}>{t.session.fromBalance}</span>}</span>;
  const sessionCost = (s: Session) => s.status === 'active' && !s.isPrepaid
    ? <span title={t.session.current}>{cash(s.currentCost ?? 0)} …</span>
    : <>{cash(s.totalCost)}</>;
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
      case 'reservations': return <ReservationsTab players={users} toast={toast} management={can.management} onChanged={fetchAll} />;
      case 'events': return <EventsTab players={users} zones={zones} devices={devices} toast={toast} management={can.management} onChanged={fetchAll} />;
      case 'orders': return <OrdersTab toast={toast} management={can.management} onChanged={fetchAll} />;
      case 'users': return <>{can.management && <LoginLockouts toast={toast} />}<UsersTab /></>;
      case 'tariffs': return <TariffsTab can={can} zones={zones} devices={devices} tariffs={tariffs} toast={toast}
        onChanged={fetchAll} onAddZone={() => setZoneModal(true)} onDeleteZone={deleteZone} />;
      case 'pos': return <PosTab can={can} players={users} toast={toast} onChanged={fetchAll} />;
      case 'shift': return <ShiftTab can={can} toast={toast} onChanged={fetchAll} />;
      case 'stock': return <StockTab can={can} toast={toast} />;
      case 'games': return <GamesTab zones={zones} toast={toast} />;
      case 'analytics': return <AnalyticsTab />;
      case 'audit': return <AuditTab owner={can.owner} />;
      case 'staff': return <StaffTab me={me} toast={toast} />;
      case 'settings': return <SettingsTab toast={toast} onCurrency={code => { if (setCurrency(code)) setCurrencyRev(v => v + 1); }} />;
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
              <span className="stat-card-label">{t.dashboard.totalPcs}</span>
            </div>
            <div className="stat-card-value">{devices.length}</div>
            <div className="stat-card-sub">{fmtText(t.dashboard.online, { n: onlineDevices.length })}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--green-glow)' }}>✅</div>
              <span className="stat-card-label">{t.dashboard.free}</span>
            </div>
            <div className="stat-card-value">{freeDevices.length}</div>
            <div className="stat-card-sub">{t.dashboard.ready}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--blue-glow)' }}>🎮</div>
              <span className="stat-card-label">{t.dashboard.active}</span>
            </div>
            <div className="stat-card-value">{activeSessions.length}</div>
            <div className="stat-card-sub">{t.dashboard.playing}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-header">
              <div className="stat-card-icon" style={{ background: 'var(--amber-glow)' }}>💰</div>
              <span className="stat-card-label">{t.dashboard.revenueToday}</span>
            </div>
            <div className="stat-card-value">{revenue ? cash(revenue.today) : t.none}</div>
            <div className="stat-card-sub">{fmtText(t.dashboard.week, { amount: revenue ? cash(revenue.week) : t.none })}</div>
          </div>
        </div>

        {/* Active Sessions */}
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">{t.dashboard.activeTitle}</span>
          </div>
          {activeSessions.length === 0 ? (
            <div className="empty-state">
              <div className="empty-state-icon">😴</div>
              <div className="empty-state-title">{t.dashboard.noActive}</div>
              <div className="empty-state-sub">{t.dashboard.noActiveHint}</div>
            </div>
          ) : (
            <div className="table-wrapper">
              <table className="data-table">
                <thead><tr>
                  <th>{t.sessions.col.pc}</th><th>{t.sessions.col.player}</th><th>{t.sessions.col.tariff}</th><th>{t.sessions.col.left}</th><th>{t.sessions.col.cost}</th><th></th>
                </tr></thead>
                <tbody>
                  {activeSessions.map(s => (
                    <tr key={s.id}>
                      <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.deviceHostname || '—'}</td>
                      <td>{s.username || t.guest}</td>
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
              <span className="section-card-title">{t.dashboard.zones}</span>
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
                      {fmtText(t.dashboard.zoneSummary, { pcs: zoneDevices.length, free: zoneFree })}
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
    // By zone (in the zone order), then by name: STD-01 ... STD-12, VIP-01 ...
    const zoneOrder = (id: string | null) => { const z = zones.find(x => x.id === id); return z ? z.sortOrder : Number.MAX_SAFE_INTEGER; };
    const filtered = (zoneFilter ? devices.filter(d => d.zoneId === zoneFilter) : devices).slice()
      .sort((a, b) => zoneOrder(a.zoneId) - zoneOrder(b.zoneId) || a.hostname.localeCompare(b.hostname, undefined, { numeric: true }));

    return (
      <>
        <div className="page-header">
          <span className="page-title">{t.map.title}</span>
          <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>
            {fmtText(t.map.summary, { free: freeDevices.length, busy: occupiedDevices.length })}
          </span>
        </div>

        {zones.length > 0 && (
          <div className="filter-bar">
            <button className={`filter-chip ${!zoneFilter ? 'active' : ''}`} onClick={() => setZoneFilter(null)}>{t.common.all}</button>
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
            <div className="empty-state-title">{t.map.empty}</div>
            <div className="empty-state-sub">{t.map.emptyHint}</div>
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
                  {d.sessionPausedUtc ? t.session.paused : d.occupancyStatus === 'free' ? t.map.free : d.occupancyStatus === 'occupied' ? t.map.busy : t.map.offline}
                </div>
                {d.sessionEndUtc && <div className="pc-card-timer">{d.sessionPausedUtc ? '⏸ ' : ''}{timeRemaining(d.sessionEndUtc, d.sessionPausedUtc)}</div>}
                {d.sessionLabel && <div className="pc-card-zone" title={d.sessionLabel}>{d.sessionIsPackage ? '🎟️ ' : ''}{d.sessionLabel}</div>}
                {d.reservation && (
                  <div className={`pc-card-resv ${d.reservation.held ? 'held' : ''}`} title={fmtText(t.resv.reservedFor, { name: d.reservation.name, time: d.reservation.startLocal })}>
                    {fmtText(t.resv.reservedAt, { time: d.reservation.startLocal })}{d.reservation.held ? ` · ${d.reservation.name}` : ''}
                  </div>
                )}
                {d.event && (
                  <div className={`pc-card-event ${d.event.held ? 'held' : ''}`} title={fmtText(t.events.eventOn, { name: d.event.name, start: d.event.startLocal, end: d.event.endLocal })}>
                    {fmtText(t.events.eventAt, { start: d.event.startLocal, end: d.event.endLocal })}
                  </div>
                )}
                {d.currentGame && (
                  <div className="pc-card-game" title={`${t.map.currentGame}: ${d.currentGame}`}>
                    <span className="game-dot" style={{ background: d.currentGameColor || 'var(--neon-cyan)' }} aria-hidden="true" />
                    <span>🎮 {d.currentGame}</span>
                  </div>
                )}
                {d.zoneName && <div className="pc-card-zone">{d.zoneName}</div>}
                <div className="pc-card-flags">
                  {d.isLocked === true && <span title={t.map.locked}>🔒</span>}
                  {d.isLocked === false && <span title={t.map.unlocked}>🔓</span>}
                  {(d.pendingCommands ?? 0) > 0 && <span title={t.map.pendingCommands}>⏳ {d.pendingCommands}</span>}
                </div>

                {pcPopover === d.id && (
                  <div className="popover" onClick={e => e.stopPropagation()}>
                    {can.frontDesk && !d.activeSessionId && d.reservation?.held && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); setCheckInId(d.reservation!.id); }}>📅 {t.resv.checkIn}</button>
                    )}
                    {can.frontDesk && d.occupancyStatus === 'free' && d.enrollment !== 'pending' && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); setStartDialog({ id: d.id, hostname: d.hostname, zoneId: d.zoneId,
                        reservation: d.event?.held ? { id: d.event.id, name: d.event.name, startUtc: d.event.startUtc, startLocal: d.event.startLocal, endLocal: d.event.endLocal, held: true, isEvent: true } : d.reservation }); }}>
                        {t.map.start}
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
                        {t.map.end}
                      </button>
                    )}
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'lock'); }}>{t.map.lock}</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'unlock'); }}>{t.map.unlock}</button>
                    {can.deviceAdmin && d.status === 'online' && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); openPower(d, 'reboot'); }}>{t.power.reboot.menu}</button>
                    )}
                    {can.deviceAdmin && d.status === 'online' && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); openPower(d, 'shutdown'); }}>{t.power.shutdown.menu}</button>
                    )}
                    {can.deviceAdmin && d.status !== 'online' && d.macAddress && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); openPower(d, 'wake'); }}>{t.power.wake.menu}</button>
                    )}
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendMessage(d.id, d.hostname); }}>{t.map.message}</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'logoff'); }}>{t.map.logoff}</button>
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
          <span className="page-title">{t.devices.title}</span>
          <span className="badge badge-green">{fmtText(t.devices.online, { n: onlineDevices.length })}</span>
        </div>

        {devices.length === 0 ? (
          <div className="empty-state">
            <div className="empty-state-icon">🖥️</div>
            <div className="empty-state-title">{t.devices.empty}</div>
            <div className="empty-state-sub">{t.devices.emptyHint}</div>
          </div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>{t.devices.col.name}</th><th>{t.devices.col.status}</th><th>{t.devices.col.enrollment}</th><th>{t.devices.col.zone}</th><th>{t.devices.col.ip}</th><th>{t.devices.col.mac}</th><th>{t.devices.col.os}</th><th>{t.devices.col.lastSeen}</th><th>{t.devices.col.actions}</th>
              </tr></thead>
              <tbody>
                {devices.map(d => (
                  <tr key={d.id}>
                    <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{d.hostname}</td>
                    <td>
                      <span className={`badge ${d.status === 'online' ? 'badge-green' : 'badge-red'}`}>
                        {d.status === 'online' ? t.devices.statusOnline : t.devices.statusOffline}
                      </span>
                    </td>
                    <td>
                      <span className={`badge ${d.enrollment === 'pending' ? 'badge-amber' : d.enrollment === 'revoked' ? 'badge-red' : 'badge-green'}`}>
                        {d.enrollment === 'pending' ? t.devices.pending : d.enrollment === 'revoked' ? t.devices.revoked : t.devices.approved}
                      </span>
                    </td>
                    <td>
                      <select className="form-select" style={{ width: 140, padding: '4px 8px', fontSize: 12 }} disabled={!can.deviceAdmin}
                        value={d.zoneId || ''} onChange={e => assignZone(d.id, e.target.value || null)}>
                        <option value="">{t.devices.noZone}</option>
                        {zones.map(z => <option key={z.id} value={z.id}>{z.name}</option>)}
                      </select>
                    </td>
                    <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{d.lastIp || '—'}</td>
                    <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{d.macAddress || '—'}</td>
                    <td style={{ fontSize: 12 }}>{d.osVersion || '—'}</td>
                    <td>{timeAgo(d.lastSeenUtc)}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 4 }}>
                        {d.enrollment === 'pending' ? (can.deviceAdmin && <>
                          <button className="btn btn-success btn-sm" onClick={() => deviceAction(d.id, 'approve', d.hostname)}>{t.devices.approve}</button>
                          <button className="btn btn-danger btn-sm" onClick={() => deviceAction(d.id, 'reject', d.hostname)}>{t.devices.reject}</button>
                        </>) : d.enrollment === 'revoked' ? null : <>
                          {can.deviceAdmin && <button className="btn btn-sm" onClick={() => openPower(d, 'reboot')} title={t.power.reboot.menu} aria-label={t.power.reboot.menu}>🔄</button>}
                          {can.deviceAdmin && <button className="btn btn-sm" onClick={() => openPower(d, 'shutdown')} title={t.power.shutdown.menu} aria-label={t.power.shutdown.menu}>⏻</button>}
                          {can.deviceAdmin && d.macAddress && <button className="btn btn-sm" onClick={() => openPower(d, 'wake')} title={t.power.wake.menu} aria-label={t.power.wake.menu}>⚡</button>}
                          <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'lock')} title={t.devices.lock}>🔒</button>
                          <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'logoff')} title={t.devices.logoff}>🚪</button>
                          {can.deviceAdmin && <button className="btn btn-danger btn-sm" onClick={() => deviceAction(d.id, 'revoke', d.hostname)} title={t.devices.revoke}>⛔</button>}
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
          <span className="page-title">{t.sessions.title}</span>
          <div className="filter-bar" style={{ marginBottom: 0 }}>
            {(['all', 'active', 'ended'] as const).map(f => (
              <button key={f} className={`filter-chip ${filter === f ? 'active' : ''}`} onClick={() => setFilter(f)}>
                {f === 'all' ? t.common.all : f === 'active' ? t.sessions.active : t.sessions.ended}
              </button>
            ))}
          </div>
        </div>

        <div className="table-wrapper">
          <table className="data-table">
            <thead><tr>
              <th>{t.sessions.col.pc}</th><th>{t.sessions.col.player}</th><th>{t.sessions.col.tariff}</th><th>{t.sessions.col.start}</th><th>{t.sessions.col.left}</th><th>{t.sessions.col.cost}</th><th>{t.sessions.col.status}</th><th></th>
            </tr></thead>
            <tbody>
              {filtered.length === 0 ? (
                <tr><td colSpan={8} style={{ textAlign: 'center', padding: 40, color: 'var(--text-muted)' }}>{t.sessions.empty}</td></tr>
              ) : filtered.map(s => (
                <tr key={s.id}>
                  <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{s.deviceHostname || '—'}</td>
                  <td>{s.username || t.guest}</td>
                  <td>{sessionLabel(s)}</td>
                  <td>{fmtDateTime(s.startUtc)}</td>
                  <td style={{ color: s.status === 'active' ? 'var(--blue)' : 'var(--text-muted)' }}>
                    {s.status === 'active' ? sessionLeft(s) : s.endUtc ? fmtTime(s.endUtc) : '—'}
                  </td>
                  <td style={{ fontWeight: 600 }}>{sessionCost(s)}</td>
                  <td>
                    <span className={`badge ${s.status !== 'active' ? 'badge-amber' : s.pausedUtc ? 'badge-amber' : 'badge-green'}`}>
                      {s.status !== 'active' ? t.sessions.statusEnded : s.pausedUtc ? t.session.paused : t.sessions.statusActive}
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
          <span className="page-title">{t.players.title}</span>
          <span style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
            <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>{fmtText(t.players.count, { n: users.length })}</span>
            <button className="btn btn-primary btn-sm" onClick={() => setPlayerModal(true)}>{t.players.add}</button>
          </span>
        </div>

        {users.length === 0 ? (
          <div className="empty-state">
            <div className="empty-state-icon">👥</div>
            <div className="empty-state-title">{t.players.empty}</div>
            <div className="empty-state-sub">{t.players.emptyHint}</div>
          </div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>{t.players.col.login}</th><th>{t.players.col.name}</th><th>{t.players.col.balance}</th><th>{t.players.col.bonus}</th><th>{t.players.col.tier}</th><th>{t.players.col.created}</th><th>{t.players.col.actions}</th>
              </tr></thead>
              <tbody>
                {users.map(u => (
                  <tr key={u.id}>
                    <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>@{u.username}</td>
                    <td>{u.displayName}</td>
                    <td style={{ fontWeight: 600, color: u.balance > 0 ? 'var(--green)' : 'var(--text-muted)' }}>{cash(u.balance)}</td>
                    <td style={{ color: u.bonusPoints > 0 ? 'var(--amber)' : 'var(--text-muted)' }}>
                      {cash(u.bonusPoints)} 🎁
                      {(u.debt ?? 0) > 0 && <div><span className="badge badge-red">{t.player.debt}: {cash(u.debt)}</span></div>}
                    </td>
                    <td>{u.tier ? <span className="badge badge-purple data">{u.tier}</span> : <span className="user-chip">—</span>}</td>
                    <td>{fmtDate(u.createdUtc)}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 4 }}>
                        <button className="btn btn-sm" onClick={() => setPlayerCard({ user: u })}>{t.player.card}</button>
                        <button className="btn btn-success btn-sm" onClick={() => setPlayerCard({ user: u, form: 'topup' })}>
                          {t.player.topUp}
                        </button>
                        <button className="btn btn-sm" onClick={() => setPlayerPin(u.id, u.username)} title={t.players.pin}>🔑</button>
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

  // ===== Modals =====
  function ZoneModal() {
    if (!zoneModal) return null;
    const [name, setName] = useState('');
    const [color, setColor] = useState('#6366f1');
    const colors = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#3b82f6', '#a855f7', '#06b6d4', '#ec4899'];

    return (
      <div className="modal-overlay" onClick={() => setZoneModal(false)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">{t.zones.newTitle}</div>
          <div className="form-group">
            <label className="form-label">{t.zones.name}</label>
            <input className="form-input" placeholder={t.zones.namePlaceholder} value={name} onChange={e => setName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.zones.color}</label>
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
            <button className="btn" onClick={() => setZoneModal(false)}>{t.cancel}</button>
            <button className="btn btn-primary" disabled={!name.trim()} onClick={() => { createZone(name.trim(), color); setZoneModal(false); }}>
              {t.common.create}
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
          <div className="modal-title">{t.players.newTitle}</div>
          <div className="form-group">
            <label className="form-label">{t.players.login}</label>
            <input className="form-input" value={username} onChange={e => setUsername(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.players.name}</label>
            <input className="form-input" value={displayName} onChange={e => setDisplayName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.players.password}</label>
            <input className="form-input" type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} />
          </div>
          <div className="modal-actions">
            <button className="btn" onClick={() => setPlayerModal(false)}>{t.cancel}</button>
            <button className="btn btn-primary" disabled={!username.trim() || password.length < 4}
              onClick={() => { createPlayer(username.trim(), displayName.trim(), password); setPlayerModal(false); }}>
              {t.common.create}
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
          <div style={{ fontSize: 14 }}>{t.loading}</div>
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
                {tab.id === 'orders' && newOrders > 0 && (
                  <span className="nav-badge nav-badge-alert" aria-label={fmtText(t.orders.newCount, { n: newOrders })}>{newOrders}</span>
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
              ? fmtText(t.banner.resetsEvery, { hours: demo.resetIntervalHours })
              : t.banner.resetsOnRestart}
            {demo.nextResetUtc && ` · ${fmtText(t.banner.next, { time: fmtTime(demo.nextResetUtc) })}`}
            {` · ${t.banner.simulated}`}
          </div>
        )}
        <div className="top-bar">
          <span className="top-bar-title">
            {tabs.find(t => t.id === activeTab)?.icon} {tabs.find(t => t.id === activeTab)?.label}
          </span>
          <div className="top-bar-actions">
            <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {fmtText(t.top.pcsOnline, { online: onlineDevices.length, total: devices.length })}
            </span>
            <LangSwitch />
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
        <StartSessionDialog device={startDialog} tariffs={tariffs} players={users} toast={toast} canGiveFree={can.management}
          reservation={startDialog.reservation} canOverride={can.management}
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
      {checkInId !== null && (
        <CheckInDialog reservationId={checkInId} players={users} toast={toast}
          onClose={() => setCheckInId(null)} onDone={() => { setCheckInId(null); fetchAll(); }} />
      )}
      {power && (
        <PowerDialog target={power.target} action={power.action} toast={toast}
          onClose={() => setPower(null)} onDone={() => { setPower(null); fetchAll(); setTimeout(fetchAll, 2500); }} />
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
