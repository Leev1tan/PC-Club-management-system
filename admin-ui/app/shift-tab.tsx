'use client';
import { useCallback, useEffect, useState } from 'react';
import { API, Permissions, errorText, getJson, postJson } from './api';
import { dateTime, fmt, lang, money, t } from './i18n';
import { CurrentShift, Shift, signedMoney } from './pos';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;

const parseAmount = (text: string) => parseFloat(text.replace(',', '.'));
export const zReportUrl = (id: number) => `/zreport.html?id=${id}`;
const csvParams = () => (lang === 'en' ? 'lang=en&sep=comma' : 'lang=uk&sep=semicolon');
const isoDate = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/** Opening float form (shift tab and the checkout when no shift is open). */
export function OpenShiftForm({ previous, toast, onDone }: { previous: CurrentShift | null; toast: ToastFn; onDone: () => void }) {
  const [openingFloat, setOpeningFloat] = useState(previous?.previousCountedCash != null ? String(previous.previousCountedCash) : '1000');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const value = parseAmount(openingFloat);

  const open = async () => {
    setBusy(true);
    const res = await postJson('/api/shifts', { openingFloat: value, note: note || null });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    const shift: Shift = await res.json();
    toast(fmt(t.shift.opened, { id: shift.id }), 'success');
    onDone();
  };

  return (
    <form className="staff-form" onSubmit={e => { e.preventDefault(); open(); }}>
      <label className="form-label" htmlFor="opening-float">{t.shift.float}</label>
      <input id="opening-float" className="form-input" type="number" min="0" step="0.01" value={openingFloat} onChange={e => setOpeningFloat(e.target.value)} />
      <input className="form-input" placeholder={t.note} value={note} onChange={e => setNote(e.target.value)} />
      <button className="btn btn-success" type="submit" disabled={busy || !(value >= 0)}>{t.shift.open}</button>
      {previous?.previousShiftId != null && previous.previousCountedCash != null && (
        <span className="user-chip">{fmt(t.shift.previous, { id: previous.previousShiftId, amount: money(previous.previousCountedCash) })}</span>
      )}
    </form>
  );
}

/**
 * Shift of the desk: open with a float; close with the counted cash (cashiers count blind: the expected
 * cash is shown after the close); cash payouts (Owner, Manager); the list of shifts with their Z-reports;
 * the ledger as CSV for accounting (Owner, Manager).
 */
export function ShiftTab({ can, toast, onChanged }: { can: Permissions; toast: ToastFn; onChanged: () => void }) {
  const [current, setCurrent] = useState<CurrentShift | null>(null);
  const [shifts, setShifts] = useState<Shift[]>([]);
  const [counted, setCounted] = useState('');
  const [closeNote, setCloseNote] = useState('');
  const [result, setResult] = useState<Shift | null>(null);
  const [payout, setPayout] = useState('');
  const [payoutReason, setPayoutReason] = useState('');
  const [busy, setBusy] = useState(false);
  const today = new Date();
  const [from, setFrom] = useState(isoDate(new Date(today.getFullYear(), today.getMonth(), 1)));
  const [to, setTo] = useState(isoDate(today));

  const load = useCallback(async () => {
    const [c, list] = await Promise.all([getJson<CurrentShift>('/api/shifts/current'), getJson<Shift[]>('/api/shifts?take=60')]);
    setCurrent(c);
    setShifts(list ?? []);
  }, []);
  useEffect(() => { load(); }, [load]);

  const shift = current?.shift ?? null;

  const close = async () => {
    if (!shift) return;
    setBusy(true);
    const res = await postJson(`/api/shifts/${shift.id}/close`, { countedCash: parseAmount(counted), note: closeNote || null });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    const closed: Shift = await res.json();
    setResult(closed);
    setCounted('');
    setCloseNote('');
    toast(fmt(t.shift.closed, { id: closed.id }), 'success');
    load();
    onChanged();
  };

  const payOut = async () => {
    if (!shift) return;
    setBusy(true);
    const amount = parseAmount(payout);
    const res = await postJson(`/api/shifts/${shift.id}/payout`, { amount, reason: payoutReason });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    toast(fmt(t.shift.payoutDone, { amount: money(amount) }), 'success');
    setPayout('');
    setPayoutReason('');
    load();
  };

  return (
    <>
      <div className="page-header"><span className="page-title">{t.shift.title}</span></div>

      {result && (
        <div className={`section-card shift-result ${result.variance && result.variance < 0 ? 'negative' : ''}`}>
          <div className="section-card-title">{fmt(t.shift.closed, { id: result.id })}</div>
          <div className="cost-box">
            {fmt(t.shift.result, { expected: money(result.expectedCash), counted: money(result.countedCash), variance: signedMoney(result.variance) })}
          </div>
          <a className="btn btn-primary" href={zReportUrl(result.id)} target="_blank" rel="noopener">{t.shift.zreport}</a>
        </div>
      )}

      {current && !shift && (
        <div className="section-card">
          <div className="section-card-title">{t.shift.openTitle}</div>
          <div className="auth-sub">{t.shift.noneHint}</div>
          <OpenShiftForm previous={current} toast={toast} onDone={() => { setResult(null); load(); onChanged(); }} />
        </div>
      )}

      {shift && (
        <div className="section-card">
          <div className="section-card-header">
            <span className="section-card-title">{fmt(t.shift.current, { id: shift.id })}</span>
            <span className="user-chip">{fmt(t.shift.since, { time: dateTime(shift.openedUtc), who: shift.openedBy ?? t.none })}</span>
          </div>
          <div className="stats-grid" style={{ marginBottom: 12 }}>
            <div className="stat-card">
              <div className="stat-card-label">{t.shift.floatShort}</div>
              <div className="stat-card-value">{money(shift.openingFloat)} {t.currency}</div>
            </div>
            <div className="stat-card">
              <div className="stat-card-label">{t.shift.expected}</div>
              <div className="stat-card-value" style={{ color: 'var(--ok)' }}>{current?.expectedCash != null ? `${money(current.expectedCash)} ${t.currency}` : '🙈'}</div>
              {current?.blind && <div className="stat-card-sub">{t.shift.blind}</div>}
            </div>
          </div>
          {can.management && <a className="btn btn-sm" href={zReportUrl(shift.id)} target="_blank" rel="noopener" style={{ marginBottom: 12, display: 'inline-block' }}>{t.shift.zreport}</a>}

          <div className="form-label">{fmt(t.shift.closeTitle, { id: shift.id })}</div>
          <div className="auth-sub">{t.shift.countHint}</div>
          <form className="staff-form" onSubmit={e => { e.preventDefault(); close(); }}>
            <input id="counted-cash" className="form-input" type="number" min="0" step="0.01" placeholder={t.shift.counted} value={counted} onChange={e => setCounted(e.target.value)} />
            <input className="form-input" placeholder={t.note} value={closeNote} onChange={e => setCloseNote(e.target.value)} />
            <button className="btn btn-danger" type="submit" disabled={busy || counted === '' || !(parseAmount(counted) >= 0)}>{t.shift.close}</button>
          </form>

          {can.management && (
            <>
              <div className="form-label" style={{ marginTop: 12 }}>{t.shift.payoutTitle}</div>
              <div className="auth-sub">{t.shift.payoutHint}</div>
              <form className="staff-form" onSubmit={e => { e.preventDefault(); payOut(); }}>
                <input className="form-input" type="number" min="0.01" step="0.01" placeholder={t.player.amount} value={payout} onChange={e => setPayout(e.target.value)} />
                <input className="form-input" placeholder={t.reason} value={payoutReason} onChange={e => setPayoutReason(e.target.value)} />
                <button className="btn" type="submit" disabled={busy || !(parseAmount(payout) > 0) || payoutReason.trim().length < 3}>{t.shift.payout}</button>
              </form>
            </>
          )}
        </div>
      )}

      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">{t.shift.list}</span>
          <button className="btn btn-sm" onClick={load}>↻</button>
        </div>
        <div className="table-wrapper">
          <table className="data-table shifts-table">
            <thead><tr>
              <th>{t.shift.col.id}</th><th>{t.shift.col.opened}</th><th>{t.shift.col.closed}</th><th>{t.shift.col.by}</th>
              <th>{t.shift.col.float}</th><th>{t.shift.col.counted}</th><th>{t.shift.col.expected}</th><th>{t.shift.col.variance}</th><th></th>
            </tr></thead>
            <tbody>
              {shifts.map(s => (
                <tr key={s.id}>
                  <td>#{s.id}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>{dateTime(s.openedUtc)}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>{s.closedUtc ? dateTime(s.closedUtc) : <span className="badge badge-green">{t.shift.openNow}</span>}</td>
                  <td className="user-chip">{s.openedBy}{s.closedBy && s.closedBy !== s.openedBy ? ` → ${s.closedBy}` : ''}</td>
                  <td>{money(s.openingFloat)}</td>
                  <td>{s.countedCash != null ? money(s.countedCash) : t.none}</td>
                  <td>{s.expectedCash != null ? money(s.expectedCash) : t.none}</td>
                  <td className={s.variance == null || s.variance === 0 ? '' : s.variance < 0 ? 'money-minus' : 'money-plus'} style={{ fontWeight: 600 }}>
                    {signedMoney(s.variance)}{s.closeNote && <div className="audit-details">{s.closeNote}</div>}
                  </td>
                  <td>{(s.status === 'closed' || can.management) && <a className="btn btn-sm" href={zReportUrl(s.id)} target="_blank" rel="noopener">{t.shift.zreport}</a>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {can.management && (
        <div className="section-card">
          <div className="section-card-title">{t.shift.export}</div>
          <div className="auth-sub">{t.shift.exportHint}</div>
          <div className="staff-form">
            <label className="form-label" htmlFor="export-from">{t.shift.from}</label>
            <input id="export-from" className="form-input" type="date" value={from} onChange={e => setFrom(e.target.value)} />
            <label className="form-label" htmlFor="export-to">{t.shift.to}</label>
            <input id="export-to" className="form-input" type="date" value={to} onChange={e => setTo(e.target.value)} />
            <a className="btn btn-primary" href={`${API}/api/ledger/export.csv?from=${from}&to=${to}&${csvParams()}`} download>{t.shift.download}</a>
          </div>
        </div>
      )}
    </>
  );
}
