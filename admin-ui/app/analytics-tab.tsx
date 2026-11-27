'use client';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { API, getJson } from './api';
import { cash, dateTime, fmt, locale, methodLabel, t } from './i18n';
import { MethodSums } from './pos';

// Owner analytics (GET /api/analytics, Owner and Manager): KPI tiles, revenue per day or hour (PC time and
// bar stacked), revenue by payment method, occupancy per zone, the weekday x hour heatmap, top bar
// products, cash variance of the shifts and the reconciliation with the ledger and the Z-reports.
// Charts are plain SVG / HTML (no chart library). Colours: a validated categorical palette for the dark
// surface (series), the zone colours for zones (the same as on the PC map), one blue ramp for the heatmap.

type Point = { startUtc: string; label: string; time: number; bar: number; total: number; byMethod: MethodSums; occupancy: number | null };
type ZoneOccupancy = { zoneId: string | null; name: string; color: string; pcs: number; busyHours: number; capacityHours: number; percent: number | null };
type Sums = { time: number; bar: number; cashNet: number; cardNet: number; rows: number };
export type Analytics = {
  period: string; from: string; to: string; timeZone: string; currency: { code: string; symbol: string }; generatedUtc: string;
  revenue: { total: number; time: number; bar: number; byMethod: MethodSums };
  time: { total: number; rows: number };
  bar: { sales: MethodSums; refunds: MethodSums; net: number; receipts: number; refundReceipts: number;
    items: { name: string; category: string; quantity: number; amount: number }[] };
  topUps: { count: number; total: number };
  refunds: { bar: number; barReceipts: number; balance: number; balanceRefunds: number; total: number };
  bonus: { issued: number; spent: number; reversed: number; adjusted: number };
  payouts: number;
  averageTicket: { session: number | null; sessions: number; receipt: number | null; receipts: number };
  cash: { net: number }; card: { net: number };
  granularity: 'day' | 'hour'; series: Point[];
  occupancy: { percent: number | null; pcs: number; busyHours: number; capacityHours: number; zones: ZoneOccupancy[] };
  heatmap: { percent: (number | null)[][]; max: number | null };
  shifts: { closed: number; variance: number; shortage: number; surplus: number;
    withVariance: { id: number; closedUtc: string; closedBy: string | null; variance: number; note: string | null }[] };
  check: { ledgerRows: number; reconciled: boolean; shiftsInside: number; zReports: Sums; outsideShifts: Sums; shiftsAddUp: boolean };
  topGames?: GameStat[];
  holds?: { depositsHeld: number; depositsReleased: number; depositsKept: number; eventFeesHeld?: number; eventFeesReleased?: number; eventFeesKept?: number };
};
type GameStat = { gameId: string; name: string; category: string; color: string; plays: number; hours: number; share: number | null };

type Period = 'today' | '7d' | '30d';

// Categorical slots and the sequential occupancy ramp (low -> high) are design tokens of every theme and mode
// (app/themes.css, checked on --bg-card)
const SERIES = { time: 'var(--chart-time)', bar: 'var(--chart-bar)' };
const METHOD_COLORS: Record<string, string> = { cash: 'var(--chart-cash)', card: 'var(--chart-card)', balance: 'var(--chart-balance)', bonus: 'var(--chart-bonus)', debt: 'var(--chart-debt)' };
const RAMP = Array.from({ length: 10 }, (_, i) => `var(--ramp-${i})`);
const rampColor = (percent: number) => RAMP[Math.min(RAMP.length - 1, Math.floor(percent / 10))];

/** Amount with grouped digits in the UI language: 514 771,96 / 514,771.96. */
const amount = (n: number | null | undefined, digits = 2) =>
  (n ?? 0).toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
const cur = (n: number | null | undefined, digits = 2) => `${amount(n, digits)} ${t.currency}`;
const pct = (n: number | null | undefined) => (n == null ? t.none : `${n.toLocaleString(locale, { maximumFractionDigits: 1 })}%`);
const compact = (n: number) => (n >= 1000 ? `${(n / 1000).toLocaleString(locale, { maximumFractionDigits: n >= 10000 ? 0 : 1 })}k` : String(Math.round(n)));

function niceMax(value: number) {
  if (value <= 0) return 1;
  const power = Math.pow(10, Math.floor(Math.log10(value)));
  for (const step of [1, 2, 2.5, 5, 10]) if (value <= step * power) return step * power;
  return 10 * power;
}

/** Width of an element, kept up to date (charts are drawn in pixels so text never stretches). */
function useWidth<T extends HTMLElement>(initial = 640) {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(initial);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const update = () => setWidth(Math.max(240, Math.floor(el.clientWidth)));
    update();
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

/** A bar segment with rounded top corners (the data end) and a square base. */
function topRounded(x: number, y: number, w: number, h: number, r: number) {
  const rr = Math.max(0, Math.min(r, w / 2, h));
  return `M${x},${y + h} V${y + rr} Q${x},${y} ${x + rr},${y} H${x + w - rr} Q${x + w},${y} ${x + w},${y + rr} V${y + h} Z`;
}

function pointTitle(p: Point, granularity: 'day' | 'hour') {
  if (granularity === 'hour') return p.label;
  const d = new Date(`${p.label}T12:00:00`);
  return d.toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' });
}

function RevenueChart({ points, granularity }: { points: Point[]; granularity: 'day' | 'hour' }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [selected, setSelected] = useState<number | null>(null);
  const height = 230;
  const m = { left: 44, right: 8, top: 10, bottom: 26 };
  const plotW = width - m.left - m.right;
  const plotH = height - m.top - m.bottom;
  const max = niceMax(Math.max(0, ...points.map(p => p.time + p.bar)));
  const band = plotW / Math.max(1, points.length);
  const barW = Math.max(2, Math.min(28, band * 0.72));
  const y = (v: number) => m.top + plotH - (v / max) * plotH;
  const every = Math.max(1, Math.ceil(points.length / Math.max(1, Math.floor(plotW / (granularity === 'hour' ? 34 : 46)))));
  const active = selected ?? (points.length ? points.length - 1 : null);
  const p = active != null ? points[active] : null;

  return (
    <div ref={ref} className="viz-chart">
      <svg width={width} height={height} role="img" aria-label={granularity === 'hour' ? t.analytics.chartByHour : t.analytics.chartByDay}>
        {[0, 0.2, 0.4, 0.6, 0.8, 1].map(f => (
          <g key={f}>
            <line x1={m.left} x2={width - m.right} y1={y(max * f)} y2={y(max * f)} className="viz-grid" />
            <text x={m.left - 6} y={y(max * f) + 4} textAnchor="end" className="viz-axis">{compact(max * f)}</text>
          </g>
        ))}
        {points.map((pt, i) => {
          const x = m.left + i * band + (band - barW) / 2;
          const timeH = (pt.time / max) * plotH;
          const barH = (pt.bar / max) * plotH;
          const gap = timeH > 0 && barH > 0 ? 2 : 0;
          const base = m.top + plotH;
          return (
            <g key={pt.label}>
              {active === i && <rect x={m.left + i * band} y={m.top} width={band} height={plotH} className="viz-hover-band" />}
              {pt.time > 0 && (barH > 0
                ? <rect x={x} y={base - timeH} width={barW} height={timeH} style={{ fill: SERIES.time }} />
                : <path d={topRounded(x, base - timeH, barW, timeH, 4)} style={{ fill: SERIES.time }} />)}
              {barH > 0 && <path d={topRounded(x, base - timeH - gap - barH, barW, barH, 4)} style={{ fill: SERIES.bar }} />}
              {i % every === 0 && (
                <text x={m.left + i * band + band / 2} y={height - 8} textAnchor="middle" className="viz-axis">
                  {granularity === 'hour' ? pt.label.slice(0, 2) : pt.label.slice(8, 10) + '.' + pt.label.slice(5, 7)}
                </text>
              )}
              {/* hit target: the whole band, bigger than the bar */}
              <rect x={m.left + i * band} y={m.top} width={band} height={plotH + m.bottom} fill="transparent"
                onMouseEnter={() => setSelected(i)} onClick={() => setSelected(i)}>
                <title>{`${pointTitle(pt, granularity)}: ${cur(pt.total, 0)}`}</title>
              </rect>
            </g>
          );
        })}
        <line x1={m.left} x2={width - m.right} y1={m.top + plotH} y2={m.top + plotH} className="viz-baseline" />
      </svg>
      <div className="viz-inspector" aria-live="polite">
        {p ? (
          <>
            <strong>{pointTitle(p, granularity)}</strong>
            <span><i className="viz-swatch" style={{ background: SERIES.time }} />{t.analytics.time}: {cur(p.time, 0)}</span>
            <span><i className="viz-swatch" style={{ background: SERIES.bar }} />{t.analytics.barShort}: {cur(p.bar, 0)}</span>
            <span>{t.total}: <strong>{cur(p.total, 0)}</strong></span>
            <span>{t.analytics.occupancy}: {pct(p.occupancy)}</span>
          </>
        ) : t.analytics.noData}
      </div>
    </div>
  );
}

function PaymentSplit({ sums }: { sums: MethodSums }) {
  const parts = (['cash', 'card', 'balance', 'bonus', 'debt'] as const)
    .map(k => ({ key: k, value: sums[k] ?? 0 }))
    .filter(part => part.value > 0);
  const total = parts.reduce((s, part) => s + part.value, 0);
  if (total <= 0) return <div className="auth-sub">{t.analytics.noData}</div>;
  return (
    <>
      <div className="viz-split" role="img" aria-label={t.analytics.methods}>
        {parts.map(part => (
          <div key={part.key} style={{ flexGrow: part.value, background: METHOD_COLORS[part.key] }}
            title={`${methodLabel(part.key)}: ${cur(part.value, 0)}`} />
        ))}
      </div>
      <table className="viz-legend-table">
        <tbody>
          {parts.map(part => (
            <tr key={part.key}>
              <td><i className="viz-swatch" style={{ background: METHOD_COLORS[part.key] }} />{part.key === 'bonus' ? t.player.bonus : methodLabel(part.key)}</td>
              <td className="z-num">{cur(part.value, 0)}</td>
              <td className="z-num viz-muted">{pct(Math.round(part.value / total * 1000) / 10)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function ZoneBars({ a }: { a: Analytics }) {
  const rows = [
    { key: 'all', name: t.analytics.allPcs, color: 'var(--text-primary)', pcs: a.occupancy.pcs, percent: a.occupancy.percent, busy: a.occupancy.busyHours },
    ...a.occupancy.zones.map(z => ({ key: z.zoneId ?? 'none', name: z.zoneId ? z.name : t.analytics.noZone, color: z.color, pcs: z.pcs, percent: z.percent, busy: z.busyHours })),
  ];
  return (
    <div className="viz-zones">
      {rows.map(r => (
        <div key={r.key} className="viz-zone-row">
          <div className="viz-zone-label">
            <span className="zone-dot" style={{ background: r.color }} />
            <span>{r.name}</span>
            <span className="viz-muted">{fmt(t.analytics.pcs, { n: r.pcs })}</span>
          </div>
          <div className="viz-zone-track" title={fmt(t.analytics.busyHours, { hours: amount(r.busy, 1) })}>
            <div className="viz-zone-fill" style={{ width: `${Math.min(100, r.percent ?? 0)}%`, background: r.color }} />
          </div>
          <div className="viz-zone-value">{pct(r.percent)}</div>
        </div>
      ))}
    </div>
  );
}

/** Most played games: hours in the period as bars in the tile colour of the game, with the plays and the share. */
function TopGames({ games }: { games: GameStat[] }) {
  if (games.length === 0) return <div className="auth-sub">{t.analytics.noGames}</div>;
  const max = Math.max(...games.map(g => g.hours), 0.1);
  return (
    <div className="viz-zones viz-games">
      {games.map(g => (
        <div key={g.gameId} className="viz-zone-row">
          <div className="viz-zone-label">
            <span className="zone-dot" style={{ background: g.color }} />
            <span title={g.category}>{g.name}</span>
            <span className="viz-muted">{fmt(t.analytics.gamePlays, { n: g.plays })}</span>
          </div>
          <div className="viz-zone-track" title={g.share != null ? `${g.share}%` : ''}>
            <div className="viz-zone-fill" style={{ width: `${Math.max(2, g.hours / max * 100)}%`, background: g.color }} />
          </div>
          <div className="viz-zone-value">{fmt(t.analytics.gameHours, { hours: amount(g.hours, 1) })}</div>
        </div>
      ))}
    </div>
  );
}

function Heatmap({ a }: { a: Analytics }) {
  const [numbers, setNumbers] = useState(false);
  const hours = Array.from({ length: 24 }, (_, h) => h);
  return (
    <>
      <div className="viz-heat-scroll">
        <div className="viz-heat" role="table" aria-label={t.analytics.heatmap}>
          <div role="row" className="viz-heat-row">
            <span className="viz-heat-day" />
            {hours.map(h => <span key={h} role="columnheader" className="viz-heat-hour">{h % 3 === 0 ? String(h).padStart(2, '0') : ''}</span>)}
          </div>
          {a.heatmap.percent.map((row, d) => (
            <div key={d} role="row" className="viz-heat-row">
              <span role="rowheader" className="viz-heat-day">{t.days[d]}</span>
              {row.map((v, h) => (
                <span key={h} role="cell" className={`viz-heat-cell ${v == null ? 'empty' : ''}`}
                  style={v == null ? undefined : { background: rampColor(v), color: v >= 70 ? 'var(--ramp-ink-high)' : 'var(--ramp-ink-low)' }}
                  title={fmt(t.analytics.cell, { day: t.days[d], hour: String(h).padStart(2, '0'), value: pct(v) })}>
                  {numbers && v != null ? Math.round(v) : ''}
                </span>
              ))}
            </div>
          ))}
        </div>
      </div>
      <div className="viz-heat-legend">
        <span className="viz-muted">0%</span>
        {RAMP.map(c => <i key={c} style={{ background: c }} />)}
        <span className="viz-muted">100%</span>
        <span className="viz-heat-empty-key"><i className="viz-heat-cell empty" /> {t.analytics.notInPeriod}</span>
        <label className="viz-toggle"><input type="checkbox" checked={numbers} onChange={e => setNumbers(e.target.checked)} /> {t.analytics.showNumbers}</label>
      </div>
    </>
  );
}

function Tile({ icon, label, value, sub, glow }: { icon: string; label: string; value: string; sub?: string; glow: string }) {
  return (
    <div className="stat-card">
      <div className="stat-card-header">
        <div className="stat-card-icon" style={{ background: glow }}>{icon}</div>
        <span className="stat-card-label">{label}</span>
      </div>
      <div className="stat-card-value">{value}</div>
      {sub && <div className="stat-card-sub">{sub}</div>}
    </div>
  );
}

export function AnalyticsTab() {
  const [period, setPeriod] = useState<Period>('7d');
  const [data, setData] = useState<Analytics | null>(null);
  const [failed, setFailed] = useState(false);

  const load = useCallback(async () => {
    const a = await getJson<Analytics>(`/api/analytics?period=${period}`);
    setFailed(!a);
    if (a) setData(a);
  }, [period]);

  useEffect(() => {
    setData(null);
    load();
    const iv = setInterval(load, 60_000);
    return () => clearInterval(iv);
  }, [load]);

  const a = data;
  const csv = (lang: 'uk' | 'en') => `${API}/api/analytics/export.csv?period=${period}&lang=${lang}&sep=${lang === 'uk' ? 'semicolon' : 'comma'}`;
  const range = a ? (a.from === a.to ? a.from : `${a.from} — ${a.to}`) : '';

  return (
    <div className="analytics">
      <div className="page-header">
        <span className="page-title">{t.analytics.title}</span>
        <div className="filter-bar" style={{ marginBottom: 0 }}>
          {(['today', '7d', '30d'] as const).map(p => (
            <button key={p} className={`filter-chip ${period === p ? 'active' : ''}`} aria-pressed={period === p} onClick={() => setPeriod(p)}>
              {p === 'today' ? t.analytics.today : p === '7d' ? t.analytics.d7 : t.analytics.d30}
            </button>
          ))}
          <a className="btn btn-sm" href={csv('uk')} download>{t.analytics.csv}</a>
          <a className="btn btn-sm" href={csv('en')} download>{t.analytics.csvEn}</a>
        </div>
      </div>
      {!a ? (
        <div className="empty-state"><div className="empty-state-sub">{failed ? t.analytics.failed : t.loading}</div></div>
      ) : (
        <>
          <div className="auth-sub">{fmt(t.analytics.periodLine, { range, zone: a.timeZone })}</div>
          <div className="stats-grid analytics-tiles">
            <Tile icon="💰" glow="var(--ok-soft)" label={t.analytics.revenue} value={cur(a.revenue.total, 0)} sub={t.analytics.revenueHint} />
            <Tile icon="🎮" glow="var(--info-soft)" label={t.analytics.time} value={cur(a.revenue.time, 0)}
              sub={fmt(t.analytics.sessions, { n: a.averageTicket.sessions })} />
            <Tile icon="🥤" glow="var(--warn-soft)" label={t.analytics.bar} value={cur(a.revenue.bar, 0)}
              sub={fmt(t.analytics.receipts, { n: a.bar.receipts })} />
            <Tile icon="🧾" glow="var(--accent-soft)" label={t.analytics.avgTicket} value={a.averageTicket.session != null ? cur(a.averageTicket.session) : t.none}
              sub={fmt(t.analytics.avgReceipt, { amount: a.averageTicket.receipt != null ? cur(a.averageTicket.receipt) : t.none })} />
            <Tile icon="🖥️" glow="var(--info-soft)" label={t.analytics.occupancy} value={pct(a.occupancy.percent)}
              sub={fmt(t.analytics.occupancyHint, { busy: amount(a.occupancy.busyHours, 0), capacity: amount(a.occupancy.capacityHours, 0) })} />
            <Tile icon="↩️" glow="var(--warn-soft)" label={t.analytics.refunds} value={cur(a.refunds.total, 0)}
              sub={fmt(t.analytics.refundsHint, { bar: cur(a.refunds.bar, 0), balance: cur(a.refunds.balance, 0) })} />
            <Tile icon="🎁" glow="var(--accent-soft)" label={t.analytics.bonus} value={cur(a.bonus.issued, 0)}
              sub={fmt(t.analytics.bonusHint, { spent: cur(a.bonus.spent, 0) })} />
            <Tile icon="💳" glow="var(--ok-soft)" label={t.analytics.topUps} value={cur(a.topUps.total, 0)}
              sub={fmt(t.analytics.cashDesk, { cash: cur(a.cash.net, 0), card: cur(a.card.net, 0) })} />
          </div>

          <div className="analytics-grid">
            <div className="section-card analytics-wide">
              <div className="section-card-header">
                <span className="section-card-title">{a.granularity === 'hour' ? t.analytics.chartByHour : t.analytics.chartByDay}</span>
                <span className="viz-legend">
                  <span><i className="viz-swatch" style={{ background: SERIES.time }} />{t.analytics.time}</span>
                  <span><i className="viz-swatch" style={{ background: SERIES.bar }} />{t.analytics.barShort}</span>
                </span>
              </div>
              <RevenueChart points={a.series} granularity={a.granularity} />
            </div>
            <div className="section-card">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.methods}</span></div>
              <PaymentSplit sums={a.revenue.byMethod} />
            </div>
            <div className="section-card">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.zones}</span></div>
              <ZoneBars a={a} />
            </div>
            <div className="section-card analytics-wide">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.heatmap}</span></div>
              <div className="auth-sub">{t.analytics.heatmapHint}</div>
              <Heatmap a={a} />
            </div>
            <div className="section-card">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.topProducts}</span></div>
              {a.bar.items.length === 0 ? <div className="auth-sub">{t.analytics.noData}</div> : (
                <div className="table-wrapper">
                  <table className="data-table">
                    <thead><tr><th>{t.analytics.product}</th><th>{t.analytics.qty}</th><th>{t.analytics.amount}</th></tr></thead>
                    <tbody>
                      {a.bar.items.map(i => (
                        <tr key={i.name + i.category}><td>{i.name}<div className="user-chip">{i.category}</div></td><td>{i.quantity}</td><td>{cur(i.amount, 0)}</td></tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
            <div className="section-card">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.topGames}</span></div>
              <TopGames games={a.topGames ?? []} />
            </div>
            <div className="section-card">
              <div className="section-card-header"><span className="section-card-title">{t.analytics.shifts}</span></div>
              <div className="auth-sub">
                {fmt(t.analytics.shiftsClosed, { n: a.shifts.closed })} · {fmt(t.analytics.varianceTotal, { amount: cur(a.shifts.variance) })}
                {' '}({fmt(t.analytics.shortage, { amount: cur(a.shifts.shortage) })}, {fmt(t.analytics.surplus, { amount: cur(a.shifts.surplus) })})
              </div>
              {a.shifts.withVariance.length === 0 ? <div className="auth-sub">{t.analytics.noVariance}</div> : (
                <ul className="viz-variance">
                  {a.shifts.withVariance.map(s => (
                    <li key={s.id}>
                      <span className={s.variance < 0 ? 'money-minus' : 'money-plus'}>{s.variance > 0 ? '+' : ''}{cur(s.variance)}</span>
                      {' '}{fmt(t.analytics.shiftLine, { id: s.id, time: dateTime(s.closedUtc), who: s.closedBy ?? t.none })}
                      {s.note && <div className="viz-muted">{s.note}</div>}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </div>

          {a.holds && (a.holds.depositsHeld > 0 || a.holds.depositsKept > 0) && (
            <div className="auth-sub" style={{ marginBottom: 8 }}>
              {fmt(t.analytics.deposits, { held: cash(a.holds.depositsHeld), released: cash(a.holds.depositsReleased), kept: cash(a.holds.depositsKept) })}
            </div>
          )}
          {a.holds && ((a.holds.eventFeesHeld ?? 0) > 0 || (a.holds.eventFeesKept ?? 0) > 0) && (
            <div className="auth-sub" style={{ marginBottom: 8 }}>
              {fmt(t.analytics.eventFees, { held: cash(a.holds.eventFeesHeld), released: cash(a.holds.eventFeesReleased), kept: cash(a.holds.eventFeesKept) })}
            </div>
          )}
          <div className={a.check.reconciled && a.check.shiftsAddUp ? 'chain-ok' : 'auth-error'} role="status">
            {a.check.reconciled && a.check.shiftsAddUp
              ? fmt(t.analytics.checkOk, { rows: a.check.ledgerRows, shifts: a.check.shiftsInside })
              : t.analytics.checkBad}
          </div>
        </>
      )}
    </div>
  );
}
