'use client';
import { useState, useEffect, useCallback } from 'react';

const API = process.env.NEXT_PUBLIC_API_URL ||
  (typeof window !== 'undefined' ? window.location.origin : 'http://localhost:5081');

// ===== Types =====
type Zone = { id: string; name: string; color: string; sortOrder: number };
type Tariff = { id: string; name: string; pricePerHour: number; isDefault: boolean; zoneId: string | null; zoneName: string | null };
type User = { id: string; username: string; displayName: string; balance: number; bonusPoints: number; createdUtc: string };
type Device = {
  id: string; hostname: string; osVersion: string; agentVersion: string;
  lastSeenUtc: string | null; lastIp: string | null; status: string;
  zoneId: string | null; zoneName: string | null; zoneColor: string | null;
  positionX: number | null; positionY: number | null;
  occupancyStatus: string; activeSessionId: string | null; activeUserId: string | null; sessionEndUtc: string | null;
};
type Session = {
  id: string; deviceId: string; deviceHostname: string | null;
  tariffId: string | null; tariffName: string | null; pricePerHour: number | null;
  userId: string | null; username: string | null;
  startUtc: string; endUtc: string | null; status: string;
  totalCost: number; isPrepaid: boolean;
};
type Revenue = { today: number; week: number; month: number; currency: string };
type Transaction = { id: string; userId: string; amount: number; type: string; description: string | null; createdUtc: string };
type Toast = { id: number; message: string; type: 'success' | 'error' | 'info' };

// ===== Tab IDs =====
type Tab = 'dashboard' | 'map' | 'devices' | 'sessions' | 'users' | 'tariffs' | 'finance';

const tabs: { id: Tab; label: string; icon: string; section?: string }[] = [
  { id: 'dashboard', label: 'Дашборд', icon: '📊', section: 'ОСНОВНЕ' },
  { id: 'map', label: 'Карта ПК', icon: '🗺️' },
  { id: 'sessions', label: 'Сесії', icon: '⏱️' },
  { id: 'devices', label: 'Пристрої', icon: '🖥️', section: 'УПРАВЛІННЯ' },
  { id: 'users', label: 'Гравці', icon: '👥' },
  { id: 'tariffs', label: 'Тарифи', icon: '💰' },
  { id: 'finance', label: 'Фінанси', icon: '📈', section: 'АНАЛІТИКА' },
];

// ===== Helpers =====
const fmt = (n: number) => n.toFixed(2);
const fmtTime = (iso: string) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit', year: 'numeric' });
const fmtDateTime = (iso: string) => `${fmtDate(iso)} ${fmtTime(iso)}`;

function timeRemaining(endUtc: string | null): string {
  if (!endUtc) return 'Відкрита';
  const diff = new Date(endUtc).getTime() - Date.now();
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

// ===== Main =====
export default function AdminPage() {
  const [activeTab, setActiveTab] = useState<Tab>('dashboard');
  const [devices, setDevices] = useState<Device[]>([]);
  const [zones, setZones] = useState<Zone[]>([]);
  const [tariffs, setTariffs] = useState<Tariff[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [revenue, setRevenue] = useState<Revenue | null>(null);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [loading, setLoading] = useState(true);

  // Modal state
  const [sessionModal, setSessionModal] = useState<{ deviceId: string; hostname: string } | null>(null);
  const [zoneModal, setZoneModal] = useState(false);
  const [tariffModal, setTariffModal] = useState(false);
  const [topUpModal, setTopUpModal] = useState<{ userId: string; username: string } | null>(null);
  const [pcPopover, setPcPopover] = useState<string | null>(null);

  const toast = useCallback((message: string, type: Toast['type'] = 'info') => {
    const id = Date.now();
    setToasts(t => [...t, { id, message, type }]);
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 3500);
  }, []);

  const fetchAll = useCallback(async () => {
    try {
      const [dRes, zRes, tRes, uRes, sRes, rRes] = await Promise.all([
        fetch(`${API}/api/devices`),
        fetch(`${API}/api/zones`),
        fetch(`${API}/api/tariffs`),
        fetch(`${API}/api/users`),
        fetch(`${API}/api/sessions`),
        fetch(`${API}/api/sessions/revenue`),
      ]);
      setDevices(await dRes.json());
      setZones(await zRes.json());
      setTariffs(await tRes.json());
      setUsers(await uRes.json());
      setSessions(await sRes.json());
      setRevenue(await rRes.json());
    } catch { /* silent */ }
    setLoading(false);
  }, []);

  useEffect(() => {
    fetchAll();
    const iv = setInterval(fetchAll, 5000);
    return () => clearInterval(iv);
  }, [fetchAll]);

  // ===== API Actions =====
  const sendCommand = async (deviceId: string, type: string, payload?: object) => {
    await fetch(`${API}/api/devices/${deviceId}/commands`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ type, payload: payload || null }),
    });
    toast(`Команду "${type}" надіслано`, 'success');
  };

  const startSession = async (deviceId: string, tariffId: string, userId: string | null, duration: number | null) => {
    const res = await fetch(`${API}/api/sessions`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId, tariffId, userId, durationMinutes: duration }),
    });
    if (res.ok) { toast('Сесію розпочато ✓', 'success'); fetchAll(); }
    else { const err = await res.text(); toast(`Помилка: ${err}`, 'error'); }
  };

  const endSession = async (sessionId: string) => {
    const res = await fetch(`${API}/api/sessions/${sessionId}/end`, { method: 'POST' });
    if (res.ok) { toast('Сесію завершено ✓', 'success'); fetchAll(); }
    else toast('Не вдалося завершити сесію', 'error');
  };

  const createZone = async (name: string, color: string) => {
    await fetch(`${API}/api/zones`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, color, sortOrder: zones.length }),
    });
    toast(`Зону "${name}" створено ✓`, 'success');
    fetchAll();
  };

  const deleteZone = async (id: string) => {
    await fetch(`${API}/api/zones/${id}`, { method: 'DELETE' });
    toast('Зону видалено', 'info');
    fetchAll();
  };

  const createTariff = async (name: string, pricePerHour: number, zoneId: string | null) => {
    await fetch(`${API}/api/tariffs`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, pricePerHour, isDefault: tariffs.length === 0, zoneId }),
    });
    toast(`Тариф "${name}" створено ✓`, 'success');
    fetchAll();
  };

  const deleteTariff = async (id: string) => {
    await fetch(`${API}/api/tariffs/${id}`, { method: 'DELETE' });
    toast('Тариф видалено', 'info');
    fetchAll();
  };

  const assignZone = async (deviceId: string, zoneId: string | null) => {
    await fetch(`${API}/api/devices/${deviceId}/zone`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ zoneId }),
    });
    toast('Зону призначено ✓', 'success');
    fetchAll();
  };

  const topUpUser = async (userId: string, amount: number) => {
    const res = await fetch(`${API}/api/users/${userId}/topup`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ amount, description: null }),
    });
    if (res.ok) { toast(`Баланс поповнено на ${amount} ₴ ✓`, 'success'); fetchAll(); }
    else toast('Помилка поповнення', 'error');
  };

  // ===== Computed =====
  const activeSessions = sessions.filter(s => s.status === 'active');
  const onlineDevices = devices.filter(d => d.status === 'online');
  const freeDevices = devices.filter(d => d.occupancyStatus === 'free');
  const occupiedDevices = devices.filter(d => d.occupancyStatus === 'occupied');

  // ===== Render Helpers =====
  const renderTabContent = () => {
    switch (activeTab) {
      case 'dashboard': return <DashboardTab />;
      case 'map': return <MapTab />;
      case 'devices': return <DevicesTab />;
      case 'sessions': return <SessionsTab />;
      case 'users': return <UsersTab />;
      case 'tariffs': return <TariffsTab />;
      case 'finance': return <FinanceTab />;
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
                      <td><span className="badge badge-blue">{s.tariffName || '—'}</span></td>
                      <td style={{ color: 'var(--blue)' }}>{timeRemaining(s.endUtc)}</td>
                      <td>{fmt(s.totalCost)} ₴</td>
                      <td><button className="btn btn-danger btn-sm" onClick={() => endSession(s.id)}>Завершити</button></td>
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
    const [zoneFilter, setZoneFilter] = useState<string | null>(null);
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
              <div key={d.id} className={`pc-card status-${d.occupancyStatus}`}
                style={{ borderLeftColor: d.zoneColor || undefined }}
                onClick={() => setPcPopover(pcPopover === d.id ? null : d.id)}>
                <div className="pc-card-name">{d.hostname}</div>
                <div className="pc-card-status">
                  <span className={`dot ${d.occupancyStatus === 'free' ? 'green' : d.occupancyStatus === 'occupied' ? 'blue' : 'red'}`} />
                  {d.occupancyStatus === 'free' ? 'Вільний' : d.occupancyStatus === 'occupied' ? 'Зайнятий' : 'Офлайн'}
                </div>
                {d.sessionEndUtc && <div className="pc-card-timer">{timeRemaining(d.sessionEndUtc)}</div>}
                {d.zoneName && <div className="pc-card-zone">{d.zoneName}</div>}

                {pcPopover === d.id && (
                  <div className="popover" onClick={e => e.stopPropagation()}>
                    {d.occupancyStatus === 'free' && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); setSessionModal({ deviceId: d.id, hostname: d.hostname }); }}>
                        🎮 Розпочати сесію
                      </button>
                    )}
                    {d.activeSessionId && (
                      <button className="popover-item" onClick={() => { setPcPopover(null); endSession(d.activeSessionId!); }}>
                        ⏹️ Завершити сесію
                      </button>
                    )}
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'lock'); }}>🔒 Заблокувати</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'unlock'); }}>🔓 Розблокувати</button>
                    <button className="popover-item" onClick={() => { setPcPopover(null); sendCommand(d.id, 'restart'); }}>🔄 Перезавантажити</button>
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
                <th>Ім&apos;я</th><th>Статус</th><th>Зона</th><th>IP</th><th>ОС</th><th>Останній зв&apos;язок</th><th>Дії</th>
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
                      <select className="form-select" style={{ width: 140, padding: '4px 8px', fontSize: 12 }}
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
                        <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'restart')} title="Перезавантажити">🔄</button>
                        <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'lock')} title="Заблокувати">🔒</button>
                        <button className="btn btn-sm" onClick={() => sendCommand(d.id, 'logoff')} title="Вийти">🚪</button>
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
    const [filter, setFilter] = useState<'all' | 'active' | 'ended'>('all');
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
                  <td>{s.tariffName || '—'} {s.pricePerHour ? `(${s.pricePerHour} ₴/г)` : ''}</td>
                  <td>{fmtDateTime(s.startUtc)}</td>
                  <td style={{ color: s.status === 'active' ? 'var(--blue)' : 'var(--text-muted)' }}>
                    {s.status === 'active' ? timeRemaining(s.endUtc) : s.endUtc ? fmtTime(s.endUtc) : '—'}
                  </td>
                  <td style={{ fontWeight: 600 }}>{fmt(s.totalCost)} ₴</td>
                  <td>
                    <span className={`badge ${s.status === 'active' ? 'badge-green' : 'badge-amber'}`}>
                      {s.status === 'active' ? 'Активна' : 'Завершена'}
                    </span>
                  </td>
                  <td>
                    {s.status === 'active' && (
                      <button className="btn btn-danger btn-sm" onClick={() => endSession(s.id)}>Завершити</button>
                    )}
                  </td>
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
          <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>{users.length} зареєстровано</span>
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
                    <td>{u.bonusPoints} 🏆</td>
                    <td>{fmtDate(u.createdUtc)}</td>
                    <td>
                      <button className="btn btn-success btn-sm" onClick={() => setTopUpModal({ userId: u.id, username: u.username })}>
                        💳 Поповнити
                      </button>
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

  // ===== Tariffs Tab =====
  function TariffsTab() {
    return (
      <>
        {/* Zones */}
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">🗺️ Зони</span>
            <button className="btn btn-primary btn-sm" onClick={() => setZoneModal(true)}>+ Додати зону</button>
          </div>
          {zones.length === 0 ? (
            <div className="empty-state" style={{ padding: 30 }}>
              <div className="empty-state-sub">Зони дозволяють групувати ПК за локацією та ціною</div>
            </div>
          ) : (
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              {zones.map(z => {
                const count = devices.filter(d => d.zoneId === z.id).length;
                return (
                  <div key={z.id} className="stat-card" style={{ minWidth: 160, flex: '1 1 160px', borderLeftColor: z.color, borderLeftWidth: 3 }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <span style={{ fontWeight: 600, fontSize: 14 }}>{z.name}</span>
                      <button className="btn btn-sm btn-danger" onClick={() => deleteZone(z.id)} style={{ padding: '2px 6px', fontSize: 11 }}>✕</button>
                    </div>
                    <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 4 }}>{count} ПК</div>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        {/* Tariffs */}
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">💰 Тарифи</span>
            <button className="btn btn-primary btn-sm" onClick={() => setTariffModal(true)}>+ Додати тариф</button>
          </div>
          {tariffs.length === 0 ? (
            <div className="empty-state" style={{ padding: 30 }}>
              <div className="empty-state-sub">Створіть тариф щоб почати виставляти рахунки</div>
            </div>
          ) : (
            <div className="table-wrapper">
              <table className="data-table">
                <thead><tr><th>Назва</th><th>Ціна/год</th><th>Зона</th><th>За замовчуванням</th><th></th></tr></thead>
                <tbody>
                  {tariffs.map(t => (
                    <tr key={t.id}>
                      <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{t.name}</td>
                      <td style={{ fontWeight: 600, color: 'var(--green)' }}>{fmt(t.pricePerHour)} ₴/год</td>
                      <td>{t.zoneName || 'Всі зони'}</td>
                      <td>{t.isDefault ? <span className="badge badge-green">Так</span> : '—'}</td>
                      <td><button className="btn btn-sm btn-danger" onClick={() => deleteTariff(t.id)}>✕</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
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
  function SessionModal() {
    if (!sessionModal) return null;
    const [selectedTariff, setSelectedTariff] = useState(tariffs.find(t => t.isDefault)?.id || tariffs[0]?.id || '');
    const [selectedUser, setSelectedUser] = useState('');
    const [duration, setDuration] = useState<number | null>(60);
    const durations = [
      { min: 30, label: '30 хв' },
      { min: 60, label: '1 год' },
      { min: 120, label: '2 год' },
      { min: 180, label: '3 год' },
      { min: 300, label: '5 год' },
      { min: null as number | null, label: 'Відкрита' },
    ];
    const tariff = tariffs.find(t => t.id === selectedTariff);
    const cost = duration && tariff ? (tariff.pricePerHour * duration / 60) : 0;

    return (
      <div className="modal-overlay" onClick={() => setSessionModal(null)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">🎮 Нова сесія — {sessionModal.hostname}</div>

          <div className="form-group">
            <label className="form-label">Тариф</label>
            <select className="form-select" value={selectedTariff} onChange={e => setSelectedTariff(e.target.value)}>
              {tariffs.map(t => (
                <option key={t.id} value={t.id}>{t.name} — {fmt(t.pricePerHour)} ₴/год</option>
              ))}
            </select>
          </div>

          <div className="form-group">
            <label className="form-label">Тривалість</label>
            <div className="quick-select-grid">
              {durations.map(d => (
                <div key={d.label} className={`quick-select-item ${duration === d.min ? 'selected' : ''}`}
                  onClick={() => setDuration(d.min)}>
                  <div className="quick-select-label">{d.label}</div>
                  {d.min && tariff && (
                    <div className="quick-select-sub">{fmt(tariff.pricePerHour * d.min / 60)} ₴</div>
                  )}
                </div>
              ))}
            </div>
          </div>

          <div className="form-group">
            <label className="form-label">Гравець (необов&apos;язково)</label>
            <select className="form-select" value={selectedUser} onChange={e => setSelectedUser(e.target.value)}>
              <option value="">Гість</option>
              {users.map(u => (
                <option key={u.id} value={u.id}>{u.displayName} (@{u.username}) — {fmt(u.balance)} ₴</option>
              ))}
            </select>
          </div>

          {cost > 0 && (
            <div style={{ padding: '12px 16px', background: 'var(--accent-glow)', borderRadius: 'var(--radius-sm)', marginBottom: 12, fontSize: 14 }}>
              💰 Вартість: <strong>{fmt(cost)} ₴</strong>
            </div>
          )}

          <div className="modal-actions">
            <button className="btn" onClick={() => setSessionModal(null)}>Скасувати</button>
            <button className="btn btn-primary" onClick={() => {
              startSession(sessionModal.deviceId, selectedTariff, selectedUser || null, duration);
              setSessionModal(null);
            }}>Розпочати</button>
          </div>
        </div>
      </div>
    );
  }

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

  function TariffModal() {
    if (!tariffModal) return null;
    const [name, setName] = useState('');
    const [price, setPrice] = useState('60');
    const [zoneId, setZoneId] = useState('');

    return (
      <div className="modal-overlay" onClick={() => setTariffModal(false)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">💰 Новий тариф</div>
          <div className="form-group">
            <label className="form-label">Назва</label>
            <input className="form-input" placeholder="напр. Стандарт, VIP, Нічний..." value={name} onChange={e => setName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">Ціна за годину (₴)</label>
            <input className="form-input" type="number" min="1" step="5" value={price} onChange={e => setPrice(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">Зона (необов&apos;язково)</label>
            <select className="form-select" value={zoneId} onChange={e => setZoneId(e.target.value)}>
              <option value="">Всі зони</option>
              {zones.map(z => <option key={z.id} value={z.id}>{z.name}</option>)}
            </select>
          </div>
          <div className="modal-actions">
            <button className="btn" onClick={() => setTariffModal(false)}>Скасувати</button>
            <button className="btn btn-primary" disabled={!name.trim() || !price}
              onClick={() => { createTariff(name.trim(), parseFloat(price), zoneId || null); setTariffModal(false); }}>
              Створити
            </button>
          </div>
        </div>
      </div>
    );
  }

  function TopUpModal() {
    if (!topUpModal) return null;
    const [amount, setAmount] = useState('100');
    const presets = [50, 100, 200, 500];

    return (
      <div className="modal-overlay" onClick={() => setTopUpModal(null)}>
        <div className="modal" onClick={e => e.stopPropagation()}>
          <div className="modal-title">💳 Поповнення — @{topUpModal.username}</div>
          <div className="form-group">
            <label className="form-label">Сума (₴)</label>
            <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(4, 1fr)' }}>
              {presets.map(p => (
                <div key={p} className={`quick-select-item ${amount === String(p) ? 'selected' : ''}`}
                  onClick={() => setAmount(String(p))}>
                  <div className="quick-select-label">{p} ₴</div>
                </div>
              ))}
            </div>
            <input className="form-input" type="number" min="1" value={amount} onChange={e => setAmount(e.target.value)} />
          </div>
          <div className="modal-actions">
            <button className="btn" onClick={() => setTopUpModal(null)}>Скасувати</button>
            <button className="btn btn-success" onClick={() => { topUpUser(topUpModal.userId, parseFloat(amount)); setTopUpModal(null); }}>
              Поповнити {amount} ₴
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
        <div className="top-bar">
          <span className="top-bar-title">
            {tabs.find(t => t.id === activeTab)?.icon} {tabs.find(t => t.id === activeTab)?.label}
          </span>
          <div className="top-bar-actions">
            <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {onlineDevices.length}/{devices.length} ПК онлайн
            </span>
          </div>
        </div>
        <div className="content-area">
          {renderTabContent()}
        </div>
      </main>

      {/* Modals */}
      <SessionModal />
      <ZoneModal />
      <TariffModal />
      <TopUpModal />

      {/* Toasts */}
      <div className="toast-container">
        {toasts.map(t => (
          <div key={t.id} className={`toast toast-${t.type}`}>{t.message}</div>
        ))}
      </div>
    </div>
  );
}
