'use client';
import { useEffect, useState } from 'react';
import { API, Me, api, getJson } from '../api';
import { dateTime, fmt, lang, methodLabel, money, setCurrency, t } from '../i18n';
import { MethodSums, ZReport, signedMoney } from '../pos';

/**
 * Printable Z-report of a shift (zreport.html?id=N): every section of the server report, the variance,
 * the staff table, the check against the ledger and the audit anchor; Print and CSV (UTF-8 with BOM).
 */
export default function ZReportPage() {
  const [id, setId] = useState<number | null>(null);
  const [report, setReport] = useState<ZReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    document.documentElement.lang = lang;
    const n = Number(new URLSearchParams(window.location.search).get('id'));
    if (!Number.isInteger(n) || n <= 0) { setError('?id='); return; }
    setId(n);
    (async () => {
      // The club currency of the signed-in staff member (amounts are shown with its symbol)
      const me = await getJson<Me>('/api/auth/me');
      if (me) setCurrency(me.currency);
      const res = await api(`/api/shifts/${n}/report`);
      if (res.status === 401) { window.location.href = '/'; return; }
      if (res.status === 403) { setError(t.z.notAllowed); return; }
      if (!res.ok) { setError(`HTTP ${res.status}`); return; }
      setReport(await res.json());
    })().catch(() => setError(t.common.unreachable));
  }, []);

  if (error) return <div className="zreport"><p className="auth-error">{error}</p><a className="btn" href="/">{t.back}</a></div>;
  // No text while loading: the static page is rendered in Ukrainian, the browser may show English (no mismatch)
  if (!report || id == null) return <div className="zreport"><p aria-busy="true">…</p></div>;

  const z = report;
  const s = z.shift;
  const Row = ({ label, value, strong, cls }: { label: string; value: string; strong?: boolean; cls?: string }) => (
    <tr className={strong ? 'z-strong' : ''}><td>{label}</td><td className={`z-num ${cls ?? ''}`}>{value}</td></tr>
  );
  const Methods = ({ m, bonusAndDebt }: { m: MethodSums; bonusAndDebt?: boolean }) => (
    <>
      <Row label={`— ${methodLabel('cash')}`} value={money(m.cash)} />
      <Row label={`— ${methodLabel('card')}`} value={money(m.card)} />
      <Row label={`— ${methodLabel('balance')}`} value={money(m.balance)} />
      {bonusAndDebt && <Row label={`— ${t.player.bonus}`} value={money(m.bonus)} />}
      {bonusAndDebt && <Row label={`— ${methodLabel('debt')}`} value={money(m.debt)} />}
    </>
  );

  return (
    <div className="zreport">
      <div className="z-actions no-print">
        <button className="btn btn-primary" onClick={() => window.print()}>🖨️ {t.print}</button>
        <a className="btn" href={`${API}/api/shifts/${id}/report.csv?lang=uk&sep=semicolon`} download>{t.z.csv}</a>
        <a className="btn" href={`${API}/api/shifts/${id}/report.csv?lang=en&sep=comma`} download>{t.z.csvEn}</a>
        <a className="btn" href="/">{t.back}</a>
      </div>

      <h1>{fmt(s.status === 'closed' ? t.z.title : t.z.xTitle, { id: s.id })}</h1>
      <div className="z-sub">{fmt(t.z.generated, { time: dateTime(z.generatedUtc), zone: z.timeZone })}</div>

      <div className="z-grid">
        <section>
          <h2>{t.z.shift}</h2>
          <table><tbody>
            <Row label={t.z.opened} value={`${dateTime(s.openedUtc)} · ${s.openedBy ?? t.none}`} />
            <Row label={t.z.closed} value={s.closedUtc ? `${dateTime(s.closedUtc)} · ${s.closedBy ?? t.none}` : t.shift.openNow} />
            {s.openNote && <Row label={t.note} value={s.openNote} />}
            {s.closeNote && <Row label={t.note} value={s.closeNote} />}
          </tbody></table>
        </section>

        <section className="z-cash">
          <h2>{t.z.cash}</h2>
          <table><tbody>
            <Row label={t.z.float} value={money(z.cash.opening)} />
            <Row label={t.z.in} value={money(z.cash.in)} />
            <Row label={t.z.out} value={money(z.cash.out)} />
            <Row label={t.z.expected} value={money(z.cash.expected)} strong />
            <Row label={t.z.counted} value={z.cash.counted != null ? money(z.cash.counted) : t.none} strong />
            <Row label={t.z.variance} value={signedMoney(z.cash.variance)} strong
              cls={z.cash.variance == null || z.cash.variance === 0 ? '' : z.cash.variance < 0 ? 'money-minus' : 'money-plus'} />
          </tbody></table>
          <h2>{t.z.card}</h2>
          <table><tbody>
            <Row label={t.z.in} value={money(z.card.in)} />
            <Row label={t.z.out} value={money(z.card.out)} />
            <Row label={t.z.net} value={money(z.card.net)} strong />
          </tbody></table>
        </section>

        <section>
          <h2>{t.z.time}</h2>
          <table><tbody>
            <Row label={t.z.timeRevenue} value={money(z.time.total)} strong />
            <Methods m={z.time.byMethod} bonusAndDebt />
          </tbody></table>
          <h2>{t.z.bar}</h2>
          <table><tbody>
            <Row label={`${t.z.barSales} (${z.bar.receipts})`} value={money(z.bar.sales.total)} />
            <Methods m={z.bar.sales} />
            <Row label={`${t.z.barRefunds} (${z.bar.refundReceipts})`} value={money(z.bar.refunds.total)} />
            <Row label={t.z.barNet} value={money(z.bar.net)} strong />
          </tbody></table>
        </section>

        <section>
          <h2>{t.z.topUps} ({z.topUps.count})</h2>
          <table><tbody><Row label={t.total} value={money(z.topUps.total)} strong /><Methods m={z.topUps.byMethod} /></tbody></table>
          <h2>{t.z.refunds} ({z.balanceRefunds.count})</h2>
          <table><tbody><Row label={t.total} value={money(z.balanceRefunds.total)} strong /></tbody></table>
          <h2>{t.z.bonus}</h2>
          <table><tbody>
            <Row label={t.z.bonusIssued} value={money(z.bonus.issued)} />
            <Row label={t.z.bonusSpent} value={money(z.bonus.spent)} />
            <Row label={t.z.bonusReversed} value={money(z.bonus.reversed)} />
            <Row label={t.z.bonusAdjusted} value={money(z.bonus.adjusted)} />
          </tbody></table>
          <h2>{t.z.debt}</h2>
          <table><tbody>
            <Row label={t.z.debtRecorded} value={money(z.debt.recorded)} />
            <Row label={t.z.debtPaid} value={money(z.debt.paid.total)} />
            <Row label={t.z.debtWrittenOff} value={money(z.debt.writtenOff)} />
          </tbody></table>
          <h2>{t.z.payouts}</h2>
          <table><tbody>
            <Row label={t.total} value={money(z.payouts.total)} strong />
            {z.payouts.rows.map(p => <Row key={p.ledgerId} label={`${dateTime(p.createdUtc)} ${p.by ?? ''}: ${p.reason ?? ''}`} value={money(p.amount)} />)}
          </tbody></table>
          <h2>{t.z.adjustments}</h2>
          <table><tbody>
            <Row label="+" value={money(z.adjustments.balancePlus)} />
            <Row label="−" value={money(z.adjustments.balanceMinus)} />
          </tbody></table>
          {z.holds && (z.holds.depositsHeld > 0 || z.holds.depositsReleased > 0 || z.holds.depositsKept > 0) && <>
            <h2>{t.z.deposits}</h2>
            <table><tbody>
              <Row label={t.z.depositsHeld} value={money(z.holds.depositsHeld)} />
              <Row label={t.z.depositsReleased} value={money(z.holds.depositsReleased)} />
              <Row label={t.z.depositsKept} value={money(z.holds.depositsKept)} />
            </tbody></table>
          </>}
          {z.holds && (z.holds.ordersHeld > 0 || z.holds.ordersReleased > 0) && <>
            <h2>{t.z.orders}</h2>
            <table><tbody>
              <Row label={t.z.ordersHeld} value={money(z.holds.ordersHeld)} />
              <Row label={t.z.ordersReleased} value={money(z.holds.ordersReleased)} />
            </tbody></table>
          </>}
          {z.other.rows > 0 && <><h2>{t.z.other}</h2><table><tbody><Row label={`${methodLabel('cash')} / ${methodLabel('card')} (${z.other.rows})`} value={money(z.other.cash + z.other.card)} /></tbody></table></>}
        </section>
      </div>

      {z.bar.items.length > 0 && (
        <section>
          <h2>{t.z.items}</h2>
          <table className="z-table">
            <thead><tr><th>{t.stock.name}</th><th>{t.stock.category}</th><th className="z-num">{t.z.count}</th><th className="z-num">{t.total}</th></tr></thead>
            <tbody>{z.bar.items.map(i => (
              <tr key={i.name + i.category}><td>{i.name}</td><td>{i.category}</td><td className="z-num">{i.quantity}</td><td className="z-num">{money(i.amount)}</td></tr>
            ))}</tbody>
          </table>
        </section>
      )}

      <section>
        <h2>{t.z.byStaff}</h2>
        <table className="z-table">
          <thead><tr>
            <th>{t.z.staffCols.who}</th><th className="z-num">{t.z.staffCols.rows}</th><th className="z-num">{t.z.staffCols.cash}</th><th className="z-num">{t.z.staffCols.card}</th>
            <th className="z-num">{t.z.staffCols.time}</th><th className="z-num">{t.z.staffCols.bar}</th><th className="z-num">{t.z.staffCols.topUps}</th>
            <th className="z-num">{t.z.staffCols.refunds}</th><th className="z-num">{t.z.staffCols.payouts}</th><th className="z-num">{t.z.staffCols.debt}</th>
          </tr></thead>
          <tbody>{z.byStaff.map(p => (
            <tr key={p.username}>
              <td>{p.username}</td><td className="z-num">{p.rows}</td><td className="z-num">{money(p.cashNet)}</td><td className="z-num">{money(p.cardNet)}</td>
              <td className="z-num">{money(p.timeSales)}</td><td className="z-num">{money(p.barSales)}</td><td className="z-num">{money(p.topUps)}</td>
              <td className="z-num">{money(p.refunds + p.barRefunds)}</td><td className="z-num">{money(p.payouts)}</td><td className="z-num">{money(p.debtRecorded)}</td>
            </tr>
          ))}</tbody>
        </table>
      </section>

      <div className={`z-check ${z.ledger.reconciled ? 'ok' : 'bad'}`}>
        {z.ledger.reconciled
          ? fmt(t.z.reconciled, { rows: z.ledger.rows, first: z.ledger.firstId ?? t.none, last: z.ledger.lastId ?? t.none })
          : t.z.notReconciled}
      </div>
      {s.auditHeadHash && <div className="z-sub z-hash">{fmt(t.z.anchor, { id: s.auditHeadId ?? '', hash: s.auditHeadHash })}</div>}
    </div>
  );
}
