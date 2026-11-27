'use client';
import { useEffect, useMemo, useState } from 'react';
import { api, errorText, getJson, postJson } from './api';
import { Package, Quote, Tariff, TransferQuote, clockTime } from './billing';
import { fmt, methodLabel, money, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type PlayerLite = { id: string; username: string; displayName: string; balance: number; bonusPoints: number; debt?: number };
type DeviceLite = {
  id: string; hostname: string; zoneId: string | null; zoneName: string | null; status: string;
  occupancyStatus: string; enrollment?: string;
};
export type SessionLite = {
  id: string; deviceId: string; deviceHostname: string | null; userId: string | null; username: string | null;
  isPrepaid: boolean; endUtc: string | null; pausedUtc?: string | null; cutoffUtc?: string | null; totalCost: number;
  currentCost?: number | null; tariffName: string | null; packageName?: string | null;
};

type Method = 'balance' | 'cash' | 'card' | 'free';

function MethodPicker({ value, onChange, player, free }: { value: Method; onChange: (m: Method) => void; player: boolean; free?: boolean }) {
  const options: Method[] = [...(player ? ['balance', 'cash', 'card'] as Method[] : ['cash', 'card'] as Method[]), ...(free ? ['free'] as Method[] : [])];
  return (
    <div className="quick-select-grid" style={{ gridTemplateColumns: `repeat(${options.length}, 1fr)` }}>
      {options.map(m => (
        <div key={m} className={`quick-select-item ${value === m ? 'selected' : ''}`} onClick={() => onChange(m)}>
          <div className="quick-select-label">{methodLabel(m)}</div>
        </div>
      ))}
    </div>
  );
}

const durations = [
  { min: 30, label: `30 ${t.minutesShort}` }, { min: 60, label: `1 ${t.hoursShort}` }, { min: 120, label: `2 ${t.hoursShort}` },
  { min: 180, label: `3 ${t.hoursShort}` }, { min: 300, label: `5 ${t.hoursShort}` }, { min: null as number | null, label: t.session.open },
];

/** Start a session on a free PC: tariff time (prepaid or open-ended) or a package of the PC's zone. */
export function StartSessionDialog({ device, tariffs, players, toast, onClose, onDone, canGiveFree = false, reservation = null, canOverride = false }: {
  device: { id: string; hostname: string; zoneId: string | null }; tariffs: Tariff[]; players: PlayerLite[];
  toast: ToastFn; onClose: () => void; onDone: () => void; canGiveFree?: boolean;
  /** The reservation that holds the PC (a start needs the override of a manager). */
  reservation?: { name: string; startLocal: string; held: boolean } | null; canOverride?: boolean;
}) {
  const held = !!reservation?.held;
  const [override, setOverride] = useState(false);
  const zoneTariffs = useMemo(() => tariffs.filter(x => !x.zoneId || x.zoneId === device.zoneId), [tariffs, device.zoneId]);
  const defaultTariff = zoneTariffs.find(x => x.zoneId === device.zoneId && x.isDefault) ?? zoneTariffs.find(x => x.zoneId === device.zoneId)
    ?? zoneTariffs.find(x => x.isDefault) ?? zoneTariffs[0];
  const [mode, setMode] = useState<'tariff' | 'package'>('tariff');
  const [tariffId, setTariffId] = useState(defaultTariff?.id ?? '');
  const [duration, setDuration] = useState<number | null>(60);
  const [packages, setPackages] = useState<Package[] | null>(null);
  const [packageId, setPackageId] = useState('');
  const [playerId, setPlayerId] = useState('');
  const [method, setMethod] = useState<Method>('cash');
  const [useBonus, setUseBonus] = useState(true);
  const [quote, setQuote] = useState<Quote | null>(null);
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState('');
  const player = players.find(p => p.id === playerId) ?? null;

  useEffect(() => {
    getJson<Package[]>(`/api/packages?deviceId=${device.id}&activeOnly=true`).then(list => {
      setPackages(list ?? []);
      setPackageId(list?.find(p => p.availableNow)?.id ?? '');
    });
  }, [device.id]);

  useEffect(() => { setMethod(player ? 'balance' : 'cash'); }, [player]);

  useEffect(() => {
    if (mode !== 'tariff' || !duration || !tariffId) { setQuote(null); return; }
    let live = true;
    postJson('/api/tariffs/quote', { minutes: duration, tariffId }).then(async res => {
      if (live) setQuote(res.ok ? await res.json() : null);
    });
    return () => { live = false; };
  }, [mode, duration, tariffId]);

  const pkg = packages?.find(p => p.id === packageId) ?? null;
  const price = mode === 'package' ? pkg?.price ?? 0 : quote?.total ?? 0;
  const prepaid = mode === 'package' || duration !== null;
  const affordable = !player || method !== 'balance' || player.balance + (useBonus ? player.bonusPoints : 0) >= price;
  const free = prepaid && method === 'free';
  const debtBlocks = !prepaid && (player?.debt ?? 0) > 0;

  const start = async () => {
    setBusy(true);
    const body = mode === 'package'
      ? { deviceId: device.id, userId: playerId || null, packageId, paymentMethod: method, useBonus, reason: free ? reason : null, overrideReservation: held && override }
      : { deviceId: device.id, tariffId, userId: playerId || null, durationMinutes: duration, paymentMethod: prepaid ? method : null, useBonus, reason: free ? reason : null, overrideReservation: held && override };
    const res = await postJson('/api/sessions', body);
    setBusy(false);
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(t.session.started, 'success');
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.session.startTitle, { host: device.hostname })}</div>
        {held && (
          <div className="resv-hold-box">
            {fmt(t.resv.heldWarning, { name: reservation!.name, time: reservation!.startLocal })}
            {canOverride && <label className="check-row"><input type="checkbox" checked={override} onChange={e => setOverride(e.target.checked)} /> {t.resv.override}</label>}
          </div>
        )}
        <div className="filter-bar">
          <button className={`filter-chip ${mode === 'tariff' ? 'active' : ''}`} onClick={() => setMode('tariff')}>{t.session.modeTariff}</button>
          <button className={`filter-chip ${mode === 'package' ? 'active' : ''}`} onClick={() => setMode('package')}>
            {t.session.modePackage} {packages && `(${packages.filter(p => p.availableNow).length})`}
          </button>
        </div>

        {mode === 'tariff' ? (
          <>
            <div className="form-group">
              <label className="form-label">{t.session.tariff}</label>
              <select className="form-select" value={tariffId} onChange={e => setTariffId(e.target.value)}>
                {zoneTariffs.map(x => (
                  <option key={x.id} value={x.id}>{x.name} — {t.session.current.toLowerCase()} {money(x.priceNow)} {t.perHour}{x.ruleNow ? ` (${x.ruleNow})` : ''}</option>
                ))}
              </select>
            </div>
            <div className="form-group">
              <label className="form-label">{t.session.duration}</label>
              <div className="quick-select-grid">
                {durations.map(d => (
                  <div key={d.label} className={`quick-select-item ${duration === d.min ? 'selected' : ''}`} onClick={() => setDuration(d.min)}>
                    <div className="quick-select-label">{d.label}</div>
                  </div>
                ))}
              </div>
              {duration === null && <div className="auth-sub">{t.session.openHint}</div>}
            </div>
          </>
        ) : (
          <div className="form-group">
            {packages === null ? <div className="user-chip">{t.loading}</div>
              : packages.length === 0 ? <div className="user-chip">{t.session.noPackages}</div> : (
                <div className="package-list">
                  {packages.map(p => (
                    <div key={p.id} className={`quick-select-item ${packageId === p.id ? 'selected' : ''} ${p.availableNow ? '' : 'disabled'}`}
                      title={p.unavailableReason ?? ''} onClick={() => p.availableNow && setPackageId(p.id)}>
                      <div className="quick-select-label">{p.name}</div>
                      <div className="quick-select-sub">
                        {money(p.price)} {t.currency} · {p.availableNow && p.endsAtIfStartedNow
                          ? fmt(t.session.endsAt, { time: clockTime(p.endsAtIfStartedNow) })
                          : p.windowStart ? `${p.windowStart}–${p.windowEnd}` : t.packages.notNow}
                      </div>
                    </div>
                  ))}
                </div>
              )}
          </div>
        )}

        <div className="form-group">
          <label className="form-label">{t.session.player}</label>
          <select className="form-select" value={playerId} onChange={e => setPlayerId(e.target.value)}>
            <option value="">{t.guest}</option>
            {players.map(u => (
              <option key={u.id} value={u.id}>{u.displayName} (@{u.username}) — {money(u.balance)} {t.currency} + {money(u.bonusPoints)} {t.player.bonus.toLowerCase()}{(u.debt ?? 0) > 0 ? ` · ${t.player.debtShort} ${money(u.debt)}` : ''}</option>
            ))}
          </select>
        </div>

        {prepaid && (
          <div className="form-group">
            <label className="form-label">{t.pay.method}</label>
            <MethodPicker value={method} onChange={setMethod} player={!!player} free={canGiveFree} />
            {player && method === 'balance' && (
              <label className="check-row"><input type="checkbox" checked={useBonus} onChange={e => setUseBonus(e.target.checked)} /> {t.pay.useBonus}</label>
            )}
            {!player && method !== 'free' && <div className="auth-sub">{t.pay.guestAtDesk}</div>}
            {free && (
              <>
                <div className="auth-sub">{t.pay.freeHint}</div>
                <input className="form-input" placeholder={t.pay.freeReason} value={reason} onChange={e => setReason(e.target.value)} />
              </>
            )}
          </div>
        )}
        {debtBlocks && <div className="auth-error">{fmt(t.session.playerDebt, { amount: money(player?.debt) })}</div>}

        {prepaid && price > 0 && (
          <div className="cost-box">
            💰 {t.session.cost}: <strong>{money(price)} {t.currency}</strong>
            {mode === 'tariff' && quote && quote.slices.length > 1 && (
              <div className="quote-slices">
                {quote.slices.map((s, i) => (
                  <span key={i}>{clockTime(s.startUtc)}–{clockTime(s.endUtc)} · {s.ruleName ?? t.tariffs.base} · {money(s.pricePerHour)} {t.perHour} = {money(s.amount)} {t.currency}</span>
                ))}
              </div>
            )}
            {mode === 'package' && pkg?.endsAtIfStartedNow && <div className="quote-slices"><span>{fmt(t.session.endsAt, { time: clockTime(pkg.endsAtIfStartedNow) })}</span></div>}
            {!affordable && <div className="auth-error">{t.errors.insufficient_balance.replace(/\{\d\}/g, '…')}</div>}
          </div>
        )}

        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || (mode === 'package' ? !packageId : !tariffId) || !affordable || debtBlocks || (free && reason.trim().length < 3) || (held && !override)} onClick={start}>{t.session.start}</button>
        </div>
      </div>
    </div>
  );
}

/** Move an active session to a free PC; shows the price difference before the move. */
export function TransferDialog({ session, devices, players, toast, onClose, onDone }: {
  session: SessionLite; devices: DeviceLite[]; players: PlayerLite[]; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const from = devices.find(d => d.id === session.deviceId);
  const free = devices.filter(d => d.id !== session.deviceId && d.occupancyStatus === 'free' && (d.enrollment ?? 'approved') === 'approved');
  const [target, setTarget] = useState('');
  const [quote, setQuote] = useState<TransferQuote | null>(null);
  const [quoteError, setQuoteError] = useState<string | null>(null);
  const player = players.find(p => p.id === session.userId) ?? null;
  const [method, setMethod] = useState<Method>(player ? 'balance' : 'cash');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!target) { setQuote(null); setQuoteError(null); return; }
    let live = true;
    (async () => {
      const res = await api(`/api/sessions/${session.id}/transfer-quote?targetDeviceId=${target}`);
      if (!live) return;
      if (res.ok) { setQuote(await res.json()); setQuoteError(null); }
      else { setQuote(null); setQuoteError(await errorText(res)); }
    })();
    return () => { live = false; };
  }, [session.id, target]);

  const move = async () => {
    setBusy(true);
    const res = await postJson(`/api/sessions/${session.id}/transfer`, { targetDeviceId: target, paymentMethod: quote && quote.surcharge > 0 ? method : null });
    setBusy(false);
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(fmt(t.transfer.done, { host: free.find(d => d.id === target)?.hostname ?? '' }), 'success');
    onDone();
  };

  const basis = quote && (quote.basis === 'tariff' ? fmt(t.transfer.basisTariff, { minutes: quote.remainingMinutes })
    : quote.basis === 'package' ? fmt(t.transfer.basisPackage, { minutes: quote.remainingMinutes, name: quote.targetPackageName ?? '' })
    : quote.basis === 'segments' ? fmt(t.transfer.basisSegments, { tariff: quote.targetTariffName ?? '' })
    : t.transfer.basisNone);

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.transfer.title, { host: session.deviceHostname ?? '' })}</div>
        <div className="form-group">
          <label className="form-label">{t.transfer.target}</label>
          {free.length === 0 ? <div className="user-chip">{t.transfer.noFree}</div> : (
            <select className="form-select" value={target} onChange={e => setTarget(e.target.value)}>
              <option value="">—</option>
              {free.map(d => (
                <option key={d.id} value={d.id}>
                  {d.hostname} · {d.zoneName ?? t.none}{d.zoneId === from?.zoneId ? ` (${t.transfer.sameZone})` : ''}{d.status !== 'online' ? ` · ${t.devices.offlineShort}` : ''}
                </option>
              ))}
            </select>
          )}
        </div>
        {quoteError && <div className="auth-error">{quoteError}</div>}
        {quote && (
          <div className="cost-box">
            {quote.surcharge > 0 ? <>💰 <strong>{fmt(t.transfer.surcharge, { amount: money(quote.surcharge) })}</strong></> : <strong>{t.transfer.noSurcharge}</strong>}
            <div className="quote-slices"><span>{basis}</span><span>{t.transfer.noRefund}</span></div>
          </div>
        )}
        {quote && quote.surcharge > 0 && (
          <div className="form-group">
            <label className="form-label">{t.pay.method}</label>
            <MethodPicker value={method} onChange={setMethod} player={!!player} />
          </div>
        )}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !quote} onClick={move}>{t.transfer.confirm}</button>
        </div>
      </div>
    </div>
  );
}

/** End a session; for open-ended sessions choose how the rest is paid at the desk. */
export function EndSessionDialog({ session, players, toast, onClose, onDone }: {
  session: SessionLite; players: PlayerLite[]; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const player = players.find(p => p.id === session.userId) ?? null;
  const [method, setMethod] = useState<Method>(player ? 'balance' : 'cash');
  const [deskMethod, setDeskMethod] = useState<'cash' | 'card' | 'debt'>('cash');
  const [useBonus, setUseBonus] = useState(true);
  const [busy, setBusy] = useState(false);

  const end = async () => {
    setBusy(true);
    const res = await postJson(`/api/sessions/${session.id}/end`, session.isPrepaid ? {} : { paymentMethod: method, deskMethod, useBonus });
    setBusy(false);
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    const ended = await res.json();
    if (ended.debtRecorded > 0) toast(fmt(t.session.endedDebt, { amount: money(ended.debtRecorded) }), 'info');
    else toast(ended.amountDue > 0 ? fmt(t.session.endedDue, { amount: money(ended.amountDue) }) : t.session.ended, ended.amountDue > 0 ? 'info' : 'success');
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">⏹️ {fmt(t.session.endTitle, { host: session.deviceHostname ?? '' })}</div>
        {session.isPrepaid ? <div className="auth-sub">{t.session.endPrepaid}</div> : (
          <>
            <div className="cost-box">💰 {fmt(t.session.endOpenCost, { amount: money(session.currentCost ?? 0) })}</div>
            <div className="form-group">
              <label className="form-label">{t.pay.method}</label>
              <MethodPicker value={method} onChange={setMethod} player={!!player} />
              {player && method === 'balance' && (
                <>
                  <div className="auth-sub">{t.session.endOpenPlayer} ({money(player.balance)} {t.currency} + {money(player.bonusPoints)} {t.player.bonus.toLowerCase()})</div>
                  <label className="check-row"><input type="checkbox" checked={useBonus} onChange={e => setUseBonus(e.target.checked)} /> {t.pay.useBonus}</label>
                  <label className="form-label">{t.session.deskMethod}</label>
                  <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(3, 1fr)' }}>
                    {(['cash', 'card', 'debt'] as const).map(m => (
                      <div key={m} className={`quick-select-item ${deskMethod === m ? 'selected' : ''}`} onClick={() => setDeskMethod(m)}>
                        <div className="quick-select-label">{methodLabel(m)}</div>
                      </div>
                    ))}
                  </div>
                  {deskMethod === 'debt' && <div className="auth-sub">{t.session.debtHint}</div>}
                </>
              )}
            </div>
          </>
        )}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-danger" disabled={busy} onClick={end}>{t.session.end}</button>
        </div>
      </div>
    </div>
  );
}
