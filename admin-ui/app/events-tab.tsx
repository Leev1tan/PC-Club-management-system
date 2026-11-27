'use client';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, errorText, getJson, postJson } from './api';
import { cash, clubToday, fmt, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type PlayerLite = { id: string; username: string; displayName: string; balance: number; bonusPoints: number };
type Zone = { id: string; name: string; color: string };
type DeviceLite = { id: string; hostname: string; zoneId: string | null; enrollment?: string };

export type EventStatus = 'scheduled' | 'running' | 'finished' | 'cancelled';
export type EventSummary = {
  id: number; name: string; status: EventStatus; zoneId: string | null; zoneName: string | null; startUtc: string; endUtc: string;
  date: string; startLocal: string; endLocal: string; durationMinutes: number; priceMode: 'free' | 'fixed'; price: number; entryFee: number;
  teamSize: number; pcs: number; teams: number; players: number; maxTeams: number; winnerTeamId: number | null; winnerName: string | null;
  cancelReason: string | null; startedUtc: string | null; endedUtc: string | null;
};
type EventPlayer = {
  id: number; userId: string; username: string; displayName: string; deviceId: string | null; hostname: string | null; sessionId: string | null;
  sessionActive: boolean; fee: number; feeStatus: 'none' | 'held' | 'released' | 'kept'; startError: string | null; captain: boolean;
};
type EventTeam = { id: number; name: string; seed: number; status: 'active' | 'withdrawn'; players: EventPlayer[] };
type EventMatch = { id: number; round: number; slot: number; teamAId: number | null; teamBId: number | null; winnerTeamId: number | null; scoreA: number | null; scoreB: number | null; bye: boolean };
type EventPc = { id: string; hostname: string; playerId: number | null; username: string | null; sessionActive: boolean };
type EventDetail = { event: EventSummary; pcs: EventPc[]; teams: EventTeam[]; matches: EventMatch[]; rounds: number; feesHeld: number };

/** The next event of a PC on the PC map. */
export type PcEvent = { id: number; name: string; startUtc: string; startLocal: string; endLocal: string; held: boolean; running: boolean };

const durations = [30, 60, 90, 120, 150, 180, 240, 300, 360, 480, 600, 720];
const failed = async (res: Response) => `${t.common.errorPrefix}: ${await errorText(res)}`;
const durationLabel = (m: number) => m % 60 === 0 ? `${m / 60} ${t.hoursShort}` : m > 60 ? `${Math.floor(m / 60)} ${t.hoursShort} ${m % 60} ${t.minutesShort}` : `${m} ${t.minutesShort}`;
const statusBadge: Record<EventStatus, string> = { scheduled: 'badge-blue', running: 'badge-green', finished: 'badge-purple', cancelled: 'badge-red' };
const statusLabel = (s: EventStatus) => t.events.status[s];
const priceLabel = (e: EventSummary) => e.priceMode === 'free' ? t.events.free : fmt(t.events.perPlayer, { amount: cash(e.price) });

/**
 * Events and tournaments (spec area 9): front desk registers teams and enters match results; Owner and Manager create
 * events, start, finish or cancel them and make the bracket. Club times come from the server as strings.
 */
export function EventsTab({ players, zones, devices, toast, management, onChanged }: {
  players: PlayerLite[]; zones: Zone[]; devices: DeviceLite[]; toast: ToastFn; management: boolean; onChanged: () => void;
}) {
  const [list, setList] = useState<EventSummary[] | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [form, setForm] = useState<{ event?: EventSummary } | null>(null);
  const [showOld, setShowOld] = useState(false);

  const load = useCallback(async () => setList(await getJson<EventSummary[]>('/api/events')), []);
  useEffect(() => {
    load();
    const iv = setInterval(load, 15000);
    return () => clearInterval(iv);
  }, [load]);

  const visible = (list ?? []).filter(e => showOld || e.status === 'scheduled' || e.status === 'running' || (e.endedUtc && Date.now() - new Date(e.endedUtc).getTime() < 7 * 86400000));
  const ordered = [...visible].sort((a, b) => {
    const rank = (e: EventSummary) => e.status === 'running' ? 0 : e.status === 'scheduled' ? 1 : 2;
    return rank(a) - rank(b) || (rank(a) === 1 ? a.startUtc.localeCompare(b.startUtc) : b.startUtc.localeCompare(a.startUtc));
  });

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.events.title}</span>
        <span className="resv-toolbar">
          <label className="check-row" style={{ margin: 0 }}><input type="checkbox" checked={showOld} onChange={e => setShowOld(e.target.checked)} /> {t.events.showOld}</label>
          {management && <button className="btn btn-primary btn-sm" onClick={() => setForm({})}>{t.events.add}</button>}
        </span>
      </div>
      <p className="auth-sub">{t.events.hint}</p>

      {list && ordered.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon">🏆</div>
          <div className="empty-state-title">{t.events.empty}</div>
        </div>
      ) : (
        <div className="event-grid">
          {ordered.map(e => (
            <button key={e.id} className={`event-card ${selected === e.id ? 'active' : ''}`} onClick={() => setSelected(selected === e.id ? null : e.id)}>
              <span className="event-card-top">
                <span className={`badge ${statusBadge[e.status]}`}>{statusLabel(e.status)}</span>
                <span className="event-card-when">{e.date} · {e.startLocal}–{e.endLocal}</span>
              </span>
              <span className="event-name">{e.name}</span>
              <span className="event-card-facts">
                <span>{e.zoneName ? fmt(t.events.zonePcs, { zone: e.zoneName, n: e.pcs }) : fmt(t.events.pcsCount, { n: e.pcs })}</span>
                <span>{e.teamSize === 1 ? fmt(t.events.playersOf, { n: e.teams, max: e.maxTeams }) : fmt(t.events.teamsOf, { n: e.teams, max: e.maxTeams, size: e.teamSize })}</span>
                <span>{priceLabel(e)}{e.entryFee > 0 ? ` · ${fmt(t.events.feeShort, { amount: cash(e.entryFee) })}` : ''}</span>
                {e.winnerName && <span>🥇 <span className="event-data">{e.winnerName}</span></span>}
              </span>
            </button>
          ))}
        </div>
      )}

      {selected != null && (
        <EventPanel key={selected} id={selected} players={players} devices={devices} toast={toast} management={management}
          onEdit={ev => setForm({ event: ev })} onChanged={() => { load(); onChanged(); }} onClose={() => setSelected(null)} />
      )}
      {form && <EventForm initial={form.event} zones={zones} devices={devices} toast={toast} onClose={() => setForm(null)}
        onDone={id => { setForm(null); setSelected(id); load(); onChanged(); }} />}
    </>
  );
}

function EventPanel({ id, players, devices, toast, management, onEdit, onChanged, onClose }: {
  id: number; players: PlayerLite[]; devices: DeviceLite[]; toast: ToastFn; management: boolean;
  onEdit: (e: EventSummary) => void; onChanged: () => void; onClose: () => void;
}) {
  const [d, setD] = useState<EventDetail | null>(null);
  const [register, setRegister] = useState(false);
  const [match, setMatch] = useState<EventMatch | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(async () => setD(await getJson<EventDetail>(`/api/events/${id}`)), [id]);
  useEffect(() => {
    load();
    const iv = setInterval(load, 15000);
    return () => clearInterval(iv);
  }, [load]);
  if (!d) return null;
  const e = d.event;
  const teamName = (teamId: number | null) => teamId == null ? null : d.teams.find(x => x.id === teamId)?.name ?? `#${teamId}`;

  const run = async (path: string, body: unknown, done: string, confirmText?: string) => {
    if (confirmText && !window.confirm(confirmText)) return;
    setBusy(true);
    const res = await postJson(`/api/events/${id}/${path}`, body);
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); load(); return; }
    toast(done, 'success');
    setD(await res.json());
    onChanged();
  };
  const cancel = () => {
    const reason = window.prompt(t.events.cancelPrompt, '');
    if (reason === null) return;
    run('cancel', { reason }, t.events.cancelled);
  };
  const open = e.status === 'scheduled' || e.status === 'running';

  return (
    <div className="section-card event-panel">
      <div className="section-card-header">
        <span className="section-card-title">🏆 {fmt(t.events.detailTitle, { id: e.id })}</span>
        <button className="btn btn-sm" onClick={onClose}>{t.close}</button>
      </div>
      <div className="event-name event-name-big">{e.name}</div>
      <div className="event-facts">
        <span className={`badge ${statusBadge[e.status]}`}>{statusLabel(e.status)}</span>
        <span>{e.date} · {e.startLocal}–{e.endLocal} ({durationLabel(e.durationMinutes)})</span>
        <span>{priceLabel(e)}</span>
        <span>{e.entryFee > 0 ? fmt(t.events.feeLine, { amount: cash(e.entryFee), held: cash(d.feesHeld) }) : t.events.noFee}</span>
        <span>{e.teamSize === 1 ? t.events.solo : fmt(t.events.teamOf, { n: e.teamSize })}</span>
      </div>
      {e.status === 'cancelled' && e.cancelReason && <div className="form-hint">{t.events.cancelReason}: <span className="event-data">{e.cancelReason}</span></div>}
      {e.winnerName && <div className="event-winner">🥇 {t.events.winner}: <span className="event-data">{e.winnerName}</span></div>}

      <div className="event-actions">
        {e.status === 'scheduled' && <button className="btn btn-primary btn-sm" disabled={busy || d.pcs.length - d.teams.filter(x => x.status === 'active').length * e.teamSize < e.teamSize}
          onClick={() => setRegister(true)}>{e.teamSize === 1 ? t.events.registerPlayer : t.events.registerTeam}</button>}
        {management && e.status === 'scheduled' && <button className="btn btn-sm" disabled={busy} onClick={() => onEdit(e)}>{t.edit}</button>}
        {management && e.status === 'scheduled' && <button className="btn btn-success btn-sm" disabled={busy}
          onClick={() => run('start', {}, t.events.started, fmt(t.events.startConfirm, { end: e.endLocal }))}>{t.events.startNow}</button>}
        {management && e.status === 'running' && <button className="btn btn-sm" disabled={busy}
          onClick={() => run('finish', {}, t.events.finished, t.events.finishConfirm)}>{t.events.finishNow}</button>}
        {management && open && d.teams.filter(x => x.status === 'active').length >= 2 && <button className="btn btn-sm" disabled={busy}
          onClick={() => run('bracket', { shuffle: false }, t.events.bracketDone, d.matches.length ? t.events.bracketAgain : undefined)}>{t.events.makeBracket}</button>}
        {management && open && d.teams.filter(x => x.status === 'active').length >= 2 && <button className="btn btn-sm" disabled={busy}
          onClick={() => run('bracket', { shuffle: true }, t.events.bracketDone, d.matches.length ? t.events.bracketAgain : undefined)}>{t.events.shuffleBracket}</button>}
        {management && open && <button className="btn btn-danger btn-sm" disabled={busy} onClick={cancel}>{t.events.cancel}</button>}
      </div>

      <div className="event-columns">
        <div>
          <div className="event-subtitle">{t.events.seats}</div>
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr><th>{t.events.col.pc}</th><th>{t.events.col.player}</th><th>{t.events.col.session}</th></tr></thead>
              <tbody>
                {d.pcs.map(pc => {
                  const p = d.teams.flatMap(x => x.players).find(x => x.id === pc.playerId);
                  return (
                    <tr key={pc.id}>
                      <td>{pc.hostname}</td>
                      <td>{pc.username ? <span className="event-data">@{pc.username}</span> : <span className="user-chip">{t.events.freeSeat}</span>}</td>
                      <td>
                        {pc.sessionActive ? <span className="badge badge-green">{t.events.playing}</span>
                          : p?.startError ? <span className="badge badge-red" title={p.startError}>{seatError(p.startError)}</span>
                          : p?.sessionId ? <span className="badge badge-purple">{t.events.ended}</span> : <span className="user-chip">—</span>}
                        {e.status === 'running' && p && !pc.sessionActive && (
                          <button className="btn btn-sm" style={{ marginLeft: 6 }} disabled={busy}
                            onClick={() => run(`players/${p.id}/start`, {}, t.events.seatStarted)}>{t.events.startSeat}</button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
        <div>
          <div className="event-subtitle">{e.teamSize === 1 ? t.events.playersTitle : t.events.teamsTitle}</div>
          {d.teams.length === 0 ? <div className="form-hint">{t.events.noTeams}</div> : (
            <div className="event-teams">
              {d.teams.map(team => (
                <div key={team.id} className={`event-team ${team.status === 'withdrawn' ? 'row-muted' : ''}`}>
                  <div className="event-team-head">
                    <span className="event-seed">{team.seed}</span>
                    <span className="event-data event-team-name">{team.name}</span>
                    {team.status === 'withdrawn' && <span className="badge badge-red">{t.events.withdrawn}</span>}
                    {team.status === 'active' && e.status === 'scheduled' && (
                      <button className="btn btn-sm" disabled={busy} onClick={() => run(`teams/${team.id}/withdraw`, {}, t.events.withdrawnDone, t.events.withdrawConfirm)}>{t.events.withdraw}</button>
                    )}
                  </div>
                  {team.players.map(p => (
                    <div key={p.id} className="event-team-player">
                      <span className="event-data">{p.captain && e.teamSize > 1 ? '★ ' : ''}@{p.username}</span>
                      {p.fee > 0 && <span className="user-chip">{fmt(t.events.feeState, { amount: cash(p.fee), state: t.events.feeStatus[p.feeStatus] })}</span>}
                    </div>
                  ))}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      <div className="event-subtitle">{t.events.bracket}</div>
      {d.matches.length === 0 ? <div className="form-hint">{management ? t.events.noBracketManager : t.events.noBracket}</div> : (
        <div className="bracket">
          {Array.from({ length: d.rounds }, (_, i) => i + 1).map(round => (
            <div key={round} className="bracket-round">
              <div className="bracket-round-title">{round === d.rounds ? t.events.final : round === d.rounds - 1 ? t.events.semi : fmt(t.events.round, { n: round })}</div>
              <div className="bracket-matches">
              {d.matches.filter(m => m.round === round).map(m => (
                <button key={m.id} className={`bracket-match ${m.bye ? 'bye' : ''}`} disabled={m.bye || !(e.status === 'running' || e.status === 'finished') || m.teamAId == null || m.teamBId == null}
                  onClick={() => setMatch(m)} title={m.bye ? t.events.bye : t.events.enterResult}>
                  <span className={`bracket-side ${m.winnerTeamId != null && m.winnerTeamId === m.teamAId ? 'won' : ''}`}>
                    <span className="event-data">{teamName(m.teamAId) ?? '…'}</span><span className="bracket-score">{m.bye ? '' : m.scoreA ?? ''}</span>
                  </span>
                  <span className={`bracket-side ${m.winnerTeamId != null && m.winnerTeamId === m.teamBId ? 'won' : ''}`}>
                    <span className="event-data">{m.bye && m.teamBId == null ? t.events.bye : teamName(m.teamBId) ?? '…'}</span><span className="bracket-score">{m.bye ? '' : m.scoreB ?? ''}</span>
                  </span>
                </button>
              ))}
              </div>
            </div>
          ))}
        </div>
      )}

      {register && <RegisterDialog detail={d} players={players} toast={toast} onClose={() => setRegister(false)}
        onDone={next => { setRegister(false); setD(next); toast(t.events.registered, 'success'); onChanged(); }} />}
      {match && <ResultDialog id={id} match={match} teamName={teamName} toast={toast} onClose={() => setMatch(null)}
        onDone={next => { setMatch(null); setD(next); toast(t.events.resultSaved, 'success'); }} />}
    </div>
  );
}

function seatError(code: string): string {
  return (t.events.seatErrors as Record<string, string>)[code] ?? code;
}

function EventForm({ initial, zones, devices, toast, onClose, onDone }: {
  initial?: EventSummary; zones: Zone[]; devices: DeviceLite[]; toast: ToastFn; onClose: () => void; onDone: (id: number) => void;
}) {
  const edit = !!initial;
  const [name, setName] = useState(initial?.name ?? '');
  const [date, setDate] = useState(initial?.date ?? clubToday(1));
  const [time, setTime] = useState(initial?.startLocal ?? '18:00');
  const [minutes, setMinutes] = useState(initial?.durationMinutes ?? 180);
  const [target, setTarget] = useState<'zone' | 'pcs'>('zone');
  const [zoneId, setZoneId] = useState(initial?.zoneId ?? zones[0]?.id ?? '');
  const [pcIds, setPcIds] = useState<string[]>([]);
  const [teamSize, setTeamSize] = useState(initial?.teamSize ?? 1);
  const [priceMode, setPriceMode] = useState<'free' | 'fixed'>(initial?.priceMode ?? 'free');
  const [price, setPrice] = useState(String(initial?.price ?? 0));
  const [fee, setFee] = useState(String(initial?.entryFee ?? 0));
  const [busy, setBusy] = useState(false);
  const approved = useMemo(() => devices.filter(d => d.enrollment !== 'pending' && d.enrollment !== 'revoked')
    .sort((a, b) => a.hostname.localeCompare(b.hostname)), [devices]);
  const zonePcs = approved.filter(d => d.zoneId === zoneId).length;
  const pcCount = target === 'zone' ? zonePcs : pcIds.length;
  const valid = name.trim().length > 0 && name.trim().length <= 80 && /^\d{4}-\d{2}-\d{2}$/.test(date) && /^\d{2}:\d{2}$/.test(time)
    && (edit || (pcCount >= teamSize && pcCount > 0 && (priceMode === 'free' || Number(price) > 0) && Number(fee) >= 0));

  const save = async () => {
    setBusy(true);
    const res = edit
      ? await postJson(`/api/events/${initial!.id}`, { name: name.trim(), date, time, durationMinutes: minutes }, 'PUT')
      : await postJson('/api/events', {
        name: name.trim(), zoneId: target === 'zone' ? zoneId : null, deviceIds: target === 'pcs' ? pcIds : null, date, time,
        durationMinutes: minutes, priceMode, price: priceMode === 'fixed' ? Number(price) : 0, entryFee: Number(fee) || 0, teamSize,
      });
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    const detail = await res.json() as EventDetail;
    toast(edit ? t.events.saved : t.events.created, 'success');
    onDone(detail.event.id);
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{edit ? fmt(t.events.editTitle, { id: initial!.id }) : t.events.newTitle}</div>
        <div className="form-group">
          <label className="form-label">{t.events.name}</label>
          <input className="form-input" maxLength={80} value={name} onChange={e => setName(e.target.value)} placeholder={t.events.namePlaceholder} />
        </div>
        <div className="form-row-2">
          <div className="form-group">
            <label className="form-label">{t.events.date}</label>
            <input className="form-input" type="date" value={date} onChange={e => setDate(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.events.start}</label>
            <input className="form-input" type="time" step={900} value={time} onChange={e => setTime(e.target.value)} />
          </div>
        </div>
        <div className="form-group">
          <label className="form-label">{t.events.duration}</label>
          <select className="form-select" value={minutes} onChange={e => setMinutes(Number(e.target.value))}>
            {durations.map(m => <option key={m} value={m}>{durationLabel(m)}</option>)}
          </select>
        </div>
        {!edit && (
          <>
            <div className="form-group">
              <label className="form-label">{t.events.pcs}</label>
              <div className="filter-bar">
                <button className={`filter-chip ${target === 'zone' ? 'active' : ''}`} onClick={() => setTarget('zone')}>{t.events.wholeZone}</button>
                <button className={`filter-chip ${target === 'pcs' ? 'active' : ''}`} onClick={() => setTarget('pcs')}>{t.events.choosePcs}</button>
              </div>
              {target === 'zone' ? (
                <select className="form-select" value={zoneId} onChange={e => setZoneId(e.target.value)}>
                  {zones.map(z => <option key={z.id} value={z.id}>{z.name} ({approved.filter(d => d.zoneId === z.id).length})</option>)}
                </select>
              ) : (
                <div className="event-pc-picker">
                  {approved.map(pc => (
                    <label key={pc.id} className="check-row">
                      <input type="checkbox" checked={pcIds.includes(pc.id)}
                        onChange={e => setPcIds(e.target.checked ? [...pcIds, pc.id] : pcIds.filter(x => x !== pc.id))} /> {pc.hostname}
                    </label>
                  ))}
                </div>
              )}
              <div className="form-hint">{fmt(t.events.pcsHint, { n: pcCount })}</div>
            </div>
            <div className="form-group">
              <label className="form-label">{t.events.teamSize}</label>
              <select className="form-select" value={teamSize} onChange={e => setTeamSize(Number(e.target.value))}>
                {[1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => <option key={n} value={n}>{n === 1 ? t.events.solo : fmt(t.events.teamOf, { n })}</option>)}
              </select>
              <div className="form-hint">{fmt(t.events.capacityHint, { n: teamSize > 0 ? Math.floor(pcCount / teamSize) : 0 })}</div>
            </div>
            <div className="form-group">
              <label className="form-label">{t.events.pcTime}</label>
              <div className="filter-bar">
                <button className={`filter-chip ${priceMode === 'free' ? 'active' : ''}`} onClick={() => setPriceMode('free')}>{t.events.free}</button>
                <button className={`filter-chip ${priceMode === 'fixed' ? 'active' : ''}`} onClick={() => setPriceMode('fixed')}>{t.events.fixed}</button>
              </div>
              {priceMode === 'fixed' && <input className="form-input" type="number" min={1} max={10000} step={5} value={price} onChange={e => setPrice(e.target.value)} />}
              <div className="form-hint">{priceMode === 'free' ? t.events.freeHint : t.events.fixedHint}</div>
            </div>
            <div className="form-group">
              <label className="form-label">{t.events.entryFee}</label>
              <input className="form-input" type="number" min={0} max={10000} step={10} value={fee} onChange={e => setFee(e.target.value)} />
              <div className="form-hint">{t.events.feeHint}</div>
            </div>
          </>
        )}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={!valid || busy} onClick={save}>{edit ? t.save : t.events.create}</button>
        </div>
      </div>
    </div>
  );
}

function RegisterDialog({ detail, players, toast, onClose, onDone }: {
  detail: EventDetail; players: PlayerLite[]; toast: ToastFn; onClose: () => void; onDone: (d: EventDetail) => void;
}) {
  const e = detail.event;
  const taken = useMemo(() => new Set(detail.teams.filter(x => x.status === 'active').flatMap(x => x.players).map(p => p.userId)), [detail]);
  const [name, setName] = useState('');
  const [picked, setPicked] = useState<string[]>(Array(e.teamSize).fill(''));
  const [captain, setCaptain] = useState(0);
  const [filter, setFilter] = useState('');
  const [busy, setBusy] = useState(false);
  const options = players.filter(p => !taken.has(p.id))
    .filter(p => !filter.trim() || `${p.username} ${p.displayName}`.toLowerCase().includes(filter.trim().toLowerCase()))
    .sort((a, b) => a.username.localeCompare(b.username));
  const chosen = picked.filter(Boolean);
  const valid = chosen.length === e.teamSize && new Set(chosen).size === chosen.length && (e.teamSize === 1 || name.trim().length > 0);
  const short = (id: string) => {
    const p = players.find(x => x.id === id);
    return p && e.entryFee > 0 && p.balance < e.entryFee;
  };

  const save = async () => {
    setBusy(true);
    const res = await postJson(`/api/events/${e.id}/teams`, { name: name.trim() || null, playerIds: picked, captainId: picked[captain] || null });
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    onDone(await res.json());
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={x => x.stopPropagation()}>
        <div className="modal-title">{e.teamSize === 1 ? t.events.registerPlayer : t.events.registerTeam}</div>
        {e.entryFee > 0 && <div className="form-hint">{fmt(t.events.registerFeeHint, { amount: cash(e.entryFee) })}</div>}
        {e.teamSize > 1 && (
          <div className="form-group">
            <label className="form-label">{t.events.teamName}</label>
            <input className="form-input" maxLength={40} value={name} onChange={x => setName(x.target.value)} />
          </div>
        )}
        <div className="form-group">
          <label className="form-label">{t.events.search}</label>
          <input className="form-input" value={filter} onChange={x => setFilter(x.target.value)} />
        </div>
        {picked.map((value, i) => (
          <div key={i} className="form-group event-player-row">
            <label className="form-label">{fmt(t.events.playerN, { n: i + 1 })}</label>
            <select className="form-select" value={value} onChange={x => setPicked(picked.map((v, k) => k === i ? x.target.value : v))}>
              <option value="">—</option>
              {[...(value ? players.filter(p => p.id === value) : []), ...options.filter(p => p.id !== value)].map(p => (
                <option key={p.id} value={p.id} disabled={picked.includes(p.id) && p.id !== value}>
                  @{p.username} · {cash(p.balance)}
                </option>
              ))}
            </select>
            {e.teamSize > 1 && <label className="check-row"><input type="radio" name="captain" checked={captain === i} onChange={() => setCaptain(i)} /> {t.events.captain}</label>}
            {value && short(value) && <div className="form-hint" style={{ color: 'var(--danger)' }}>{t.events.feeTooLow}</div>}
          </div>
        ))}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={!valid || busy} onClick={save}>{t.events.register}</button>
        </div>
      </div>
    </div>
  );
}

function ResultDialog({ id, match, teamName, toast, onClose, onDone }: {
  id: number; match: EventMatch; teamName: (id: number | null) => string | null; toast: ToastFn; onClose: () => void; onDone: (d: EventDetail) => void;
}) {
  const [winner, setWinner] = useState<number | null>(match.winnerTeamId);
  const [a, setA] = useState(match.scoreA != null ? String(match.scoreA) : '');
  const [b, setB] = useState(match.scoreB != null ? String(match.scoreB) : '');
  const [busy, setBusy] = useState(false);
  const save = async (clear = false) => {
    setBusy(true);
    const res = await api(`/api/events/${id}/matches/${match.id}`, {
      method: 'PUT',
      body: JSON.stringify(clear ? { winnerTeamId: null } : { winnerTeamId: winner, scoreA: a === '' ? null : Number(a), scoreB: b === '' ? null : Number(b) }),
    });
    setBusy(false);
    if (!res.ok) { toast(await failed(res), 'error'); return; }
    onDone(await res.json());
  };
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" onClick={x => x.stopPropagation()}>
        <div className="modal-title">{fmt(t.events.resultTitle, { round: match.round, n: match.slot + 1 })}</div>
        <div className="form-group">
          <label className="form-label">{t.events.whoWon}</label>
          {[match.teamAId, match.teamBId].map((team, i) => team != null && (
            <label key={team} className="check-row">
              <input type="radio" name="winner" checked={winner === team} onChange={() => setWinner(team)} />
              <span className="event-data">{teamName(team)}</span>
              <input className="form-input bracket-score-input" type="number" min={0} max={999} value={i === 0 ? a : b}
                onChange={x => (i === 0 ? setA : setB)(x.target.value)} aria-label={t.events.score} />
            </label>
          ))}
          <div className="form-hint">{t.events.resultHint}</div>
        </div>
        <div className="modal-actions">
          {match.winnerTeamId != null && <button className="btn btn-danger" disabled={busy} onClick={() => save(true)}>{t.events.clearResult}</button>}
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={winner == null || busy} onClick={() => save()}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}
