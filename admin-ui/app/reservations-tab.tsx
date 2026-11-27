'use client';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { errorText, getJson, postJson } from './api';
import { cash, clubToday, fmt, methodLabel, t } from './i18n';
import { Reservation, ReservationDay, ReservationSettings, addDays, durationsMinutes, pcOf, whoOf } from './reservations';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type PlayerLite = { id: string; username: string; displayName: string; balance: number; bonusPoints: number };

const HOUR = 56;     // px per hour on the timeline
const LABEL = 118;   // px of the PC name column
const failed = async (res: Response) => `${t.common.errorPrefix}: ${await errorText(res)}`;
const statusLabel = (s: string) => (t.resv.status as Record<string, string>)[s] ?? s;
const durationLabel = (m: number) => m % 60 === 0 ? `${m / 60} ${t.hoursShort}` : m > 60 ? `${Math.floor(m / 60)} ${t.hoursShort} ${m % 60} ${t.minutesShort}` : `${m} ${t.minutesShort}`;
const badgeOf = (r: Reservation) => r.held ? 'badge-amber' : r.status === 'confirmed' ? 'badge-blue' : r.status === 'checked_in' ? 'badge-green'
  : r.status === 'requested' ? 'badge-amber' : 'badge-red';

/** Reservations (front desk): a day timeline (rows = PCs by zone, click a free place to book), the list, requests and the settings. */
export function ReservationsTab({ players, toast, management, onChanged }: {
  players: PlayerLite[]; toast: ToastFn; management: boolean; onChanged: () => void;
}) {
  const [date, setDate] = useState(() => clubToday());
  const [day, setDay] = useState<ReservationDay | null>(null);
  const [showClosed, setShowClosed] = useState(false);
  const [form, setForm] = useState<{ reservation?: Reservation; pc?: string; zone?: string; time?: string } | null>(null);
  const [details, setDetails] = useState<Reservation | null>(null);
  const [checkIn, setCheckIn] = useState<Reservation | null>(null);

  const load = useCallback(async () => setDay(await getJson<ReservationDay>(`/api/reservations?date=${date}`)), [date]);
  useEffect(() => {
    load();
    const iv = setInterval(load, 15000);
    return () => clearInterval(iv);
  }, [load]);
  const done = () => { load(); onChanged(); };

  const act = async (r: Reservation, action: 'cancel' | 'decline' | 'confirm') => {
    let body: object = {};
    if (action !== 'confirm') {
      const reason = window.prompt(action === 'cancel' ? t.resv.cancelPrompt : t.resv.declinePrompt, '');
      if (reason === null) return;
      body = { reason };
    }
    const res = await postJson(`/api/reservations/${r.id}/${action}`, body);
    toast(res.ok ? (action === 'cancel' ? t.resv.cancelled : action === 'decline' ? t.resv.declined : t.resv.confirmed) : await failed(res), res.ok ? 'success' : 'error');
    setDetails(null);
    done();
  };

  const visible = (day?.reservations ?? []).filter(r => showClosed || ['confirmed', 'checked_in', 'requested', 'no_show'].includes(r.status));

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.resv.title}</span>
        <span className="resv-toolbar">
          <button className="btn btn-sm" onClick={() => setDate(addDays(date, -1))} aria-label={t.resv.prev}>◀</button>
          <input className="form-input resv-date" type="date" value={date} onChange={e => e.target.value && setDate(e.target.value)} aria-label={t.resv.date} />
          <button className="btn btn-sm" onClick={() => setDate(addDays(date, 1))} aria-label={t.resv.next}>▶</button>
          <button className="btn btn-sm" onClick={() => setDate(clubToday())}>{t.resv.today}</button>
          <button className="btn btn-primary btn-sm" onClick={() => setForm({})}>{t.resv.newBtn}</button>
        </span>
      </div>
      {day && <div className="form-hint" style={{ marginBottom: 12 }}>{fmt(t.resv.holdLine, { hold: day.holdMinutes, grace: day.graceMinutes })} {day.noShowKeepsDeposit ? t.resv.noShowKeeps : t.resv.noShowReturns} {t.resv.clickHint}</div>}

      {day && day.requests.length > 0 && (
        <div className="section-card">
          <div className="section-card-header"><span className="section-card-title">{t.resv.requests} ({day.requests.length})</span></div>
          <div className="form-hint" style={{ marginBottom: 8 }}>{t.resv.requestsHint}</div>
          <ReservationTable rows={day.requests} onOpen={setDetails} onAct={act} onCheckIn={setCheckIn} />
        </div>
      )}

      {day && <Timeline day={day} rows={visible} onFree={(pc, zone, minute) => setForm({ pc, zone, time: `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}` })} onOpen={setDetails} />}

      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">{t.resv.list}</span>
          <label className="check-row" style={{ margin: 0 }}><input type="checkbox" checked={showClosed} onChange={e => setShowClosed(e.target.checked)} /> {t.resv.showClosed}</label>
        </div>
        {!day ? <div className="form-hint">{t.loading}</div> : visible.length === 0 ? <div className="form-hint">{t.resv.empty}</div>
          : <ReservationTable rows={visible} onOpen={setDetails} onAct={act} onCheckIn={setCheckIn} />}
      </div>

      {management && <SettingsCard toast={toast} onSaved={load} />}

      {form && day && <ReservationForm day={day} players={players} initial={form} toast={toast} onClose={() => setForm(null)} onDone={() => { setForm(null); done(); }} />}
      {details && <ReservationDetails r={details} onClose={() => setDetails(null)} onAct={act}
        onEdit={() => { setForm({ reservation: details }); setDetails(null); }} onCheckIn={() => { setCheckIn(details); setDetails(null); }} />}
      {checkIn && <CheckInDialog reservation={checkIn} players={players} toast={toast} onClose={() => setCheckIn(null)} onDone={() => { setCheckIn(null); done(); }} />}
    </>
  );
}

function Timeline({ day, rows, onFree, onOpen }: {
  day: ReservationDay; rows: Reservation[]; onFree: (pc: string | undefined, zone: string | undefined, minute: number) => void; onOpen: (r: Reservation) => void;
}) {
  const pcIds = new Set(day.zones.flatMap(z => z.pcs.map(p => p.id)));
  const width = LABEL + 24 * HOUR;
  // Opens at one hour before now (today) or at 08:00, once per day shown
  const scroller = useRef<HTMLDivElement>(null);
  const scrolledFor = useRef<string | null>(null);
  useEffect(() => {
    if (!scroller.current || scrolledFor.current === day.date) return;
    scrolledFor.current = day.date;
    const minute = day.nowMinute !== null ? Math.max(0, day.nowMinute - 60) : 8 * 60;
    scroller.current.scrollLeft = minute / 60 * HOUR;
  }, [day.date, day.nowMinute]);
  const bar = (r: Reservation) => {
    const from = Math.max(0, r.startMinute);
    const to = Math.min(24 * 60, r.startMinute + r.durationMinutes);
    if (to <= from) return null;
    return (
      <button key={r.id} className={`resv-bar status-${r.status} ${r.held ? 'held' : ''}`} style={{ left: LABEL + from / 60 * HOUR, width: Math.max(10, (to - from) / 60 * HOUR - 2) }}
        title={`${r.startLocal}–${r.endLocal} · ${whoOf(r, t.resv.erased)} · ${statusLabel(r.status)}`} onClick={e => { e.stopPropagation(); onOpen(r); }}>
        {r.startLocal} {whoOf(r, t.resv.erased)}
      </button>
    );
  };
  const track = (key: string, label: string, list: Reservation[], pc?: string, zone?: string, sub?: string) => (
    <div className="resv-row" key={key}>
      <div className="resv-label" title={label}>{label}{sub && <span className="resv-sub">{sub}</span>}</div>
      <div className="resv-track" style={{ width: 24 * HOUR }} role="button" tabIndex={-1} aria-label={label}
        onClick={e => {
          const x = e.clientX - (e.currentTarget as HTMLElement).getBoundingClientRect().left;
          onFree(pc, zone, Math.max(0, Math.min(23 * 60 + 30, Math.floor(x / HOUR * 2) * 30)));
        }} />
      {list.map(bar)}
    </div>
  );
  return (
    <div className="section-card resv-card">
      <div className="resv-scroll" ref={scroller}>
        <div className="resv-grid" style={{ width }}>
          <div className="resv-row resv-hours">
            <div className="resv-label" />
            {Array.from({ length: 24 }, (_, h) => <div key={h} className="resv-hour" style={{ left: LABEL + h * HOUR, width: HOUR }}>{String(h).padStart(2, '0')}</div>)}
          </div>
          {day.zones.map(z => {
            const floating = rows.filter(r => r.zoneId === z.id && (!pcOf(r) || !pcIds.has(pcOf(r)!)));
            return (
              <div key={z.id ?? 'none'} className="resv-zone">
                <div className="resv-zone-name" style={{ borderLeftColor: z.color }}>{z.name || t.devices.noZone}</div>
                {z.id && track(`any-${z.id}`, t.resv.anyPc, floating, undefined, z.id, z.name)}
                {z.pcs.map(p => track(p.id, p.hostname, rows.filter(r => pcOf(r) === p.id), p.id))}
              </div>
            );
          })}
          {day.nowMinute !== null && <div className="resv-now" style={{ left: LABEL + day.nowMinute / 60 * HOUR }} aria-hidden="true" />}
        </div>
      </div>
    </div>
  );
}

function ReservationTable({ rows, onOpen, onAct, onCheckIn }: {
  rows: Reservation[]; onOpen: (r: Reservation) => void; onAct: (r: Reservation, a: 'cancel' | 'decline' | 'confirm') => void; onCheckIn: (r: Reservation) => void;
}) {
  return (
    <div className="table-wrapper">
      <table className="data-table resv-table">
        <thead><tr><th>{t.resv.col.time}</th><th>{t.resv.col.pc}</th><th>{t.resv.col.who}</th><th>{t.resv.col.deposit}</th><th>{t.resv.col.status}</th><th></th></tr></thead>
        <tbody>
          {rows.map(r => (
            <tr key={r.id}>
              <td><button className="link-btn" onClick={() => onOpen(r)}>{r.date.slice(8, 10)}.{r.date.slice(5, 7)} {r.startLocal}–{r.endLocal}</button></td>
              <td>{r.deviceHostname ?? (r.assignedHostname ? `${r.assignedHostname} (${t.resv.anyPc.toLowerCase()})` : fmt(t.resv.anyPcOf, { zone: r.zoneName ?? '' }))}</td>
              <td>{whoOf(r, t.resv.erased)}{r.phone && <div className="user-chip">{r.phone}</div>}</td>
              <td>{r.deposit > 0 ? <>{cash(r.deposit)} <span className="user-chip">{(t.resv.depositStatus as Record<string, string>)[r.depositStatus]}</span></> : t.none}</td>
              <td><span className={`badge ${badgeOf(r)}`}>{r.held ? t.resv.held : statusLabel(r.status)}</span></td>
              <td>
                <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                  {r.canCheckIn && <button className="btn btn-success btn-sm" onClick={() => onCheckIn(r)}>{t.resv.checkIn}</button>}
                  {r.status === 'requested' && <button className="btn btn-success btn-sm" onClick={() => onAct(r, 'confirm')}>{t.resv.confirm}</button>}
                  {r.status === 'requested' && <button className="btn btn-sm" onClick={() => onAct(r, 'decline')}>{t.resv.decline}</button>}
                  {r.status === 'confirmed' && <button className="btn btn-danger btn-sm" onClick={() => onAct(r, 'cancel')}>{t.resv.cancel}</button>}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ReservationDetails({ r, onClose, onAct, onEdit, onCheckIn }: {
  r: Reservation; onClose: () => void; onAct: (r: Reservation, a: 'cancel' | 'decline' | 'confirm') => void; onEdit: () => void; onCheckIn: () => void;
}) {
  const line = (label: string, value: React.ReactNode) => <div className="resv-detail"><span>{label}</span><strong>{value}</strong></div>;
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.resv.detailsTitle, { id: r.id })}</div>
        {line(t.resv.col.time, `${r.date} ${r.startLocal}–${r.endLocal} (${durationLabel(r.durationMinutes)})`)}
        {line(t.resv.col.pc, r.deviceHostname ?? (r.assignedHostname ? `${r.assignedHostname} · ${fmt(t.resv.anyPcOf, { zone: r.zoneName ?? '' })}` : fmt(t.resv.anyPcOf, { zone: r.zoneName ?? '' })))}
        {line(t.resv.col.who, whoOf(r, t.resv.erased))}
        {r.phone && line(t.resv.phone, r.phone)}
        {line(t.resv.col.status, r.held ? `${statusLabel(r.status)} · ${t.resv.held}` : statusLabel(r.status))}
        {r.deposit > 0 && line(t.resv.deposit, `${cash(r.deposit)} · ${(t.resv.depositStatus as Record<string, string>)[r.depositStatus]}`)}
        {r.status === 'confirmed' && line(t.resv.window, fmt(t.resv.windowValue, { from: r.holdFromLocal, until: r.noShowAtLocal }))}
        {r.note && line(t.resv.note, r.note)}
        {r.cancelReason && line(t.reason, r.cancelReason)}
        {line(t.resv.source, `${(t.resv.sources as Record<string, string>)[r.source]}${r.createdByUsername ? ` · ${r.createdByUsername}` : ''}`)}
        <div className="modal-actions" style={{ flexWrap: 'wrap' }}>
          <button className="btn" onClick={onClose}>{t.close}</button>
          {(r.status === 'confirmed' || r.status === 'requested') && <button className="btn" onClick={onEdit}>{t.edit}</button>}
          {r.status === 'requested' && <button className="btn" onClick={() => onAct(r, 'decline')}>{t.resv.decline}</button>}
          {r.status === 'requested' && <button className="btn btn-success" onClick={() => onAct(r, 'confirm')}>{t.resv.confirm}</button>}
          {r.status === 'confirmed' && <button className="btn btn-danger" onClick={() => onAct(r, 'cancel')}>{t.resv.cancel}</button>}
          {r.canCheckIn && <button className="btn btn-success" onClick={onCheckIn}>{t.resv.checkIn}</button>}
        </div>
      </div>
    </div>
  );
}

function ReservationForm({ day, players, initial, toast, onClose, onDone }: {
  day: ReservationDay; players: PlayerLite[]; initial: { reservation?: Reservation; pc?: string; zone?: string; time?: string };
  toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const r = initial.reservation;
  const [target, setTarget] = useState(r ? (r.deviceId ? `pc:${r.deviceId}` : `zone:${r.zoneId}`) : initial.pc ? `pc:${initial.pc}` : initial.zone ? `zone:${initial.zone}` : '');
  const [date, setDate] = useState(r?.date ?? day.date);
  const [time, setTime] = useState(r?.startLocal ?? initial.time ?? '18:00');
  const [minutes, setMinutes] = useState(r?.durationMinutes ?? 120);
  const [who, setWho] = useState<'player' | 'guest'>(r && !r.userId ? 'guest' : 'player');
  const [playerId, setPlayerId] = useState(r?.userId ?? '');
  const [guest, setGuest] = useState(r?.guestName ?? '');
  const [deposit, setDeposit] = useState('0');
  const [note, setNote] = useState(r?.note ?? '');
  const [busy, setBusy] = useState(false);
  const player = players.find(p => p.id === playerId);

  const save = async () => {
    const [kind, id] = target.split(':');
    const place = kind === 'pc' ? { deviceId: id, zoneId: null } : { deviceId: null, zoneId: id };
    setBusy(true);
    const res = r
      ? await postJson(`/api/reservations/${r.id}`, { ...place, date, time, durationMinutes: minutes, note: note || null }, 'PUT')
      : await postJson('/api/reservations', {
          ...place, date, time, durationMinutes: minutes, note: note || null,
          userId: who === 'player' ? playerId : null, guestName: who === 'guest' ? guest : null,
          deposit: who === 'player' ? Number(deposit) || 0 : 0,
        });
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    toast(r ? t.resv.saved : t.resv.created, 'success');
    onDone();
  };

  const valid = target && date && /^\d\d:\d\d$/.test(time) && (r || (who === 'player' ? !!playerId : guest.trim().length > 0));
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{r ? fmt(t.resv.editTitle, { id: r.id }) : t.resv.newTitle}</div>
        <div className="form-group">
          <label className="form-label">{t.resv.target}</label>
          <select className="form-select" value={target} onChange={e => setTarget(e.target.value)}>
            <option value="">—</option>
            {day.zones.map(z => (
              <optgroup key={z.id ?? 'none'} label={z.name || t.devices.noZone}>
                {z.id && <option value={`zone:${z.id}`}>{fmt(t.resv.anyPcOf, { zone: z.name })}</option>}
                {z.pcs.map(p => <option key={p.id} value={`pc:${p.id}`}>{p.hostname}</option>)}
              </optgroup>
            ))}
          </select>
        </div>
        <div className="form-row-2">
          <div className="form-group">
            <label className="form-label">{t.resv.date}</label>
            <input className="form-input" type="date" value={date} onChange={e => setDate(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.resv.time}</label>
            <input className="form-input" type="time" step={900} value={time} onChange={e => setTime(e.target.value.slice(0, 5))} />
          </div>
        </div>
        <div className="form-group">
          <label className="form-label">{t.resv.duration}</label>
          <select className="form-select" value={minutes} onChange={e => setMinutes(Number(e.target.value))}>
            {durationsMinutes.map(m => <option key={m} value={m}>{durationLabel(m)}</option>)}
          </select>
        </div>
        {!r && (
          <>
            <div className="filter-bar">
              <button className={`filter-chip ${who === 'player' ? 'active' : ''}`} onClick={() => setWho('player')}>{t.resv.player}</button>
              <button className={`filter-chip ${who === 'guest' ? 'active' : ''}`} onClick={() => setWho('guest')}>{t.resv.walkIn}</button>
            </div>
            {who === 'player' ? (
              <>
                <div className="form-group">
                  <select className="form-select" value={playerId} onChange={e => setPlayerId(e.target.value)} aria-label={t.resv.player}>
                    <option value="">—</option>
                    {players.map(u => <option key={u.id} value={u.id}>{u.displayName} (@{u.username}) — {cash(u.balance)}</option>)}
                  </select>
                </div>
                <div className="form-group">
                  <label className="form-label">{t.resv.deposit}</label>
                  <input className="form-input" type="number" min={0} max={player?.balance ?? 10000} step={10} value={deposit} onChange={e => setDeposit(e.target.value)} />
                  <div className="form-hint">{t.resv.depositHint}</div>
                </div>
              </>
            ) : (
              <div className="form-group">
                <label className="form-label">{t.resv.name}</label>
                <input className="form-input" maxLength={60} value={guest} onChange={e => setGuest(e.target.value)} />
              </div>
            )}
          </>
        )}
        <div className="form-group">
          <label className="form-label">{t.resv.note}</label>
          <input className="form-input" maxLength={200} value={note} onChange={e => setNote(e.target.value)} />
        </div>
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={!valid || busy} onClick={save}>{r ? t.save : t.resv.create}</button>
        </div>
      </div>
    </div>
  );
}

/** Check-in: starts the session of the reservation (a held deposit pays it). Also used from the PC map. */
export function CheckInDialog({ reservation, reservationId, players, toast, onClose, onDone }: {
  reservation?: Reservation; reservationId?: number; players: PlayerLite[]; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const [r, setR] = useState<Reservation | null>(reservation ?? null);
  const [mode, setMode] = useState<'minutes' | 'open' | 'balance'>('minutes');
  const [minutes, setMinutes] = useState(reservation?.durationMinutes ?? 60);
  const [method, setMethod] = useState<'balance' | 'cash' | 'card'>('cash');
  const [useBonus, setUseBonus] = useState(true);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (r || !reservationId) return;
    getJson<Reservation>(`/api/reservations/${reservationId}`).then(x => { if (x) { setR(x); setMinutes(x.durationMinutes); } });
  }, [r, reservationId]);
  useEffect(() => { if (r) setMethod(r.userId ? 'balance' : 'cash'); }, [r]);
  const player = useMemo(() => players.find(p => p.id === r?.userId), [players, r]);

  if (!r) return null;
  const go = async () => {
    setBusy(true);
    const res = await postJson(`/api/reservations/${r.id}/check-in`, {
      mode, minutes: mode === 'minutes' ? minutes : null, paymentMethod: mode === 'balance' ? 'balance' : method, useBonus,
    });
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    toast(t.resv.checkedIn, 'success');
    onDone();
  };
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.resv.checkInTitle, { id: r.id, who: whoOf(r, t.resv.erased) })}</div>
        <div className="auth-sub">{r.startLocal}–{r.endLocal} · {r.deviceHostname ?? r.assignedHostname ?? fmt(t.resv.anyPcOf, { zone: r.zoneName ?? '' })}
          {r.depositStatus === 'held' && <> · {fmt(t.resv.depositApplied, { amount: cash(r.deposit) })}</>}</div>
        <div className="filter-bar" style={{ marginTop: 10 }}>
          <button className={`filter-chip ${mode === 'minutes' ? 'active' : ''}`} onClick={() => setMode('minutes')}>{t.resv.mode.minutes}</button>
          <button className={`filter-chip ${mode === 'open' ? 'active' : ''}`} onClick={() => setMode('open')}>{t.resv.mode.open}</button>
          {r.userId && <button className={`filter-chip ${mode === 'balance' ? 'active' : ''}`} onClick={() => setMode('balance')}>{t.resv.mode.balance}</button>}
        </div>
        {mode === 'minutes' && (
          <div className="form-group">
            <label className="form-label">{t.resv.duration}</label>
            <select className="form-select" value={minutes} onChange={e => setMinutes(Number(e.target.value))}>
              {durationsMinutes.map(m => <option key={m} value={m}>{durationLabel(m)}</option>)}
            </select>
          </div>
        )}
        {mode !== 'balance' && mode === 'minutes' && (
          <div className="form-group">
            <label className="form-label">{t.pay.method}</label>
            <div className="filter-bar">
              {(r.userId ? ['balance', 'cash', 'card'] as const : ['cash', 'card'] as const).map(m => (
                <button key={m} className={`filter-chip ${method === m ? 'active' : ''}`} onClick={() => setMethod(m)}>{methodLabel(m)}</button>
              ))}
            </div>
            {player && method === 'balance' && <label className="check-row"><input type="checkbox" checked={useBonus} onChange={e => setUseBonus(e.target.checked)} /> {t.pay.useBonus}</label>}
          </div>
        )}
        {mode === 'open' && <div className="auth-sub">{t.session.openHint}</div>}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-success" disabled={busy} onClick={go}>{t.resv.checkIn}</button>
        </div>
      </div>
    </div>
  );
}

function SettingsCard({ toast, onSaved }: { toast: ToastFn; onSaved: () => void }) {
  const [s, setS] = useState<ReservationSettings | null>(null);
  useEffect(() => { getJson<ReservationSettings>('/api/reservations/settings').then(setS); }, []);
  if (!s) return null;
  const save = async () => {
    const res = await postJson('/api/reservations/settings', s, 'PUT');
    toast(res.ok ? t.resv.settingsSaved : await failed(res), res.ok ? 'success' : 'error');
    onSaved();
  };
  return (
    <div className="section-card">
      <div className="section-card-header"><span className="section-card-title">{t.resv.settings}</span></div>
      <div className="form-row-2">
        <div className="form-group">
          <label className="form-label">{t.resv.holdMinutes}</label>
          <input className="form-input" type="number" min={0} max={180} value={s.holdMinutes} onChange={e => setS({ ...s, holdMinutes: Number(e.target.value) })} />
        </div>
        <div className="form-group">
          <label className="form-label">{t.resv.graceMinutes}</label>
          <input className="form-input" type="number" min={5} max={120} value={s.graceMinutes} onChange={e => setS({ ...s, graceMinutes: Number(e.target.value) })} />
        </div>
      </div>
      <div className="form-group">
        <label className="form-label">{t.resv.noShowDeposit}</label>
        <select className="form-select" value={s.noShowKeepsDeposit ? 'keep' : 'return'} onChange={e => setS({ ...s, noShowKeepsDeposit: e.target.value === 'keep' })}>
          <option value="keep">{t.resv.keep}</option>
          <option value="return">{t.resv.return}</option>
        </select>
      </div>
      <label className="check-row"><input type="checkbox" checked={s.publicBooking} onChange={e => setS({ ...s, publicBooking: e.target.checked })} /> {t.resv.publicBooking}</label>
      <div className="form-hint" style={{ marginBottom: 8 }}>
        {t.resv.publicHint} <a className="link-btn" href="/book" target="_blank" rel="noopener">/book</a>
      </div>
      <div className="form-row-2">
        <div className="form-group">
          <label className="form-label">{t.resv.eraseAfterDays}</label>
          <input className="form-input" type="number" min={1} max={365} value={s.eraseAfterDays} onChange={e => setS({ ...s, eraseAfterDays: Number(e.target.value) })} />
        </div>
        <div className="form-group">
          <label className="form-label">{t.resv.maxOpenPerPhone}</label>
          <input className="form-input" type="number" min={1} max={5} value={s.maxOpenPerPhone} onChange={e => setS({ ...s, maxOpenPerPhone: Number(e.target.value) })} />
        </div>
      </div>
      <button className="btn btn-primary btn-sm" onClick={save}>{t.save}</button>
    </div>
  );
}
