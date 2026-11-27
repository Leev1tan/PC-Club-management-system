'use client';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Permissions, errorText, getJson, postJson } from './api';
import { clock, fmt, methodLabel, money, t } from './i18n';
import { CurrentShift, Product, Sale } from './pos';
import { OpenShiftForm } from './shift-tab';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type PlayerLite = { id: string; username: string; displayName: string; balance: number; bonusPoints: number };

/**
 * Bar checkout (front desk): tap products into the receipt, then pay in cash, by card (both need an
 * open shift) or from a player's real balance. Receipts of the shift below; Owner and Manager refund.
 */
export function PosTab({ can, players, toast, onChanged }: { can: Permissions; players: PlayerLite[]; toast: ToastFn; onChanged: () => void }) {
  const [products, setProducts] = useState<Product[]>([]);
  const [current, setCurrent] = useState<CurrentShift | null>(null);
  const [receipts, setReceipts] = useState<Sale[]>([]);
  const [category, setCategory] = useState<string | null>(null);
  const [cart, setCart] = useState<Record<string, number>>({});
  const [playerId, setPlayerId] = useState('');
  const [busy, setBusy] = useState(false);
  const [refundTarget, setRefundTarget] = useState<Sale | null>(null);

  const load = useCallback(async () => {
    const [p, c] = await Promise.all([getJson<Product[]>('/api/products'), getJson<CurrentShift>('/api/shifts/current')]);
    if (p) setProducts(p);
    setCurrent(c);
    const id = c?.shift?.id;
    setReceipts(await getJson<Sale[]>(id ? `/api/sales?shiftId=${id}&take=30` : '/api/sales?take=30') ?? []);
  }, []);
  useEffect(() => { load(); }, [load]);

  const categories = useMemo(() => [...new Set(products.map(p => p.category))], [products]);
  const shown = category ? products.filter(p => p.category === category) : products;
  const lines = Object.entries(cart).map(([id, quantity]) => ({ product: products.find(p => p.id === id)!, quantity })).filter(l => l.product);
  const total = lines.reduce((sum, l) => sum + l.product.price * l.quantity, 0);
  const player = players.find(p => p.id === playerId) ?? null;
  const shiftOpen = !!current?.shift;
  const deskBlocked = !!current?.requireOpenShift && !shiftOpen;
  const lowCount = products.filter(p => p.lowStock).length;

  const add = (p: Product, delta = 1) => setCart(c => {
    const next = Math.max(0, Math.min(100, (c[p.id] ?? 0) + delta));
    if (p.trackStock && next > p.stockQuantity) return c;
    const copy = { ...c };
    if (next === 0) delete copy[p.id]; else copy[p.id] = next;
    return copy;
  });

  const pay = async (method: 'cash' | 'card' | 'balance') => {
    setBusy(true);
    const res = await postJson('/api/sales', {
      items: lines.map(l => ({ productId: l.product.id, quantity: l.quantity })), paymentMethod: method, userId: method === 'balance' ? playerId : null,
    });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); load(); return; }
    const sale: Sale = await res.json();
    toast(fmt(t.pos.sold, { id: sale.id, amount: money(sale.total) }), 'success');
    setCart({});
    load();
    onChanged();
  };

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.pos.title}</span>
        <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {lowCount > 0 && <span className="badge badge-amber">⚠ {fmt(t.stock.lowCount, { n: lowCount })}</span>}
          {current?.shift
            ? <span className="badge badge-green">{fmt(t.shift.current, { id: current.shift.id })} · {fmt(t.shift.since, { time: clock(current.shift.openedUtc), who: current.shift.openedBy ?? t.none })}</span>
            : <span className="badge badge-red">{t.shift.none}</span>}
        </span>
      </div>

      {current && !current.shift && (
        <div className="section-card shift-banner">
          <div className="section-card-title">{t.shift.none}</div>
          <div className="auth-sub">{t.shift.noneHint}</div>
          <OpenShiftForm previous={current} toast={toast} onDone={() => { load(); onChanged(); }} />
        </div>
      )}

      <div className="pos-layout">
        <div>
          <div className="filter-bar">
            <button className={`filter-chip ${!category ? 'active' : ''}`} onClick={() => setCategory(null)}>{t.pos.all}</button>
            {categories.map(c => (
              <button key={c} className={`filter-chip ${category === c ? 'active' : ''}`} onClick={() => setCategory(category === c ? null : c)}>{c}</button>
            ))}
          </div>
          <div className="product-grid">
            {shown.map(p => {
              const out = p.trackStock && p.stockQuantity <= (cart[p.id] ?? 0);
              return (
                <button key={p.id} type="button" className={`product-tile ${p.lowStock ? 'low' : ''} ${out ? 'out' : ''}`} disabled={out} onClick={() => add(p)}>
                  <span className="product-name">{p.name}</span>
                  <span className="product-price">{money(p.price)} {t.currency}</span>
                  <span className="product-stock">
                    {!p.trackStock ? ' ' : p.stockQuantity === 0 ? t.pos.out : `${fmt(t.pos.left, { n: p.stockQuantity })}${p.lowStock ? ` · ${t.pos.low}` : ''}`}
                  </span>
                  {cart[p.id] ? <span className="product-count">{cart[p.id]}</span> : null}
                </button>
              );
            })}
          </div>
        </div>

        <div className="section-card pos-cart">
          <div className="section-card-title">🧾 {t.pos.cart}</div>
          {lines.length === 0 ? <div className="auth-sub">{t.pos.empty}</div> : (
            <div className="cart-lines">
              {lines.map(l => (
                <div key={l.product.id} className="cart-line">
                  <span className="cart-name">{l.product.name}</span>
                  <button className="btn btn-sm" aria-label="-" onClick={() => add(l.product, -1)}>−</button>
                  <span className="cart-qty">{l.quantity}</span>
                  <button className="btn btn-sm" aria-label="+" onClick={() => add(l.product, 1)}>+</button>
                  <span className="cart-sum">{money(l.product.price * l.quantity)}</span>
                </div>
              ))}
            </div>
          )}
          <div className="cart-total">{t.total}: <strong>{money(total)} {t.currency}</strong></div>
          <div className="form-group">
            <label className="form-label">{t.pos.player}</label>
            <select className="form-select" value={playerId} onChange={e => setPlayerId(e.target.value)}>
              <option value="">{t.guest}</option>
              {players.map(u => <option key={u.id} value={u.id}>{u.displayName} (@{u.username}) — {money(u.balance)} {t.currency}</option>)}
            </select>
            {player && <div className="auth-sub">{t.pos.noBonus}</div>}
          </div>
          {deskBlocked && <div className="auth-error">{t.pos.noShift}</div>}
          <div className="pay-buttons">
            <button className="btn btn-success pay-button" disabled={busy || lines.length === 0 || deskBlocked} onClick={() => pay('cash')}>{t.pos.payCash}</button>
            <button className="btn btn-primary pay-button" disabled={busy || lines.length === 0 || deskBlocked} onClick={() => pay('card')}>{t.pos.payCard}</button>
            <button className="btn pay-button" disabled={busy || lines.length === 0 || !player || player.balance < total} onClick={() => pay('balance')}>{t.pos.payBalance}</button>
          </div>
        </div>
      </div>

      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">{t.pos.receipts}</span>
          <button className="btn btn-sm" onClick={load}>↻</button>
        </div>
        <div className="table-wrapper">
          <table className="data-table receipts-table">
            <thead><tr>
              <th>{t.pos.col.id}</th><th>{t.pos.col.time}</th><th>{t.pos.col.items}</th><th>{t.pos.col.method}</th><th>{t.pos.col.total}</th><th>{t.pos.col.who}</th><th></th>
            </tr></thead>
            <tbody>
              {receipts.length === 0 ? (
                <tr><td colSpan={7} style={{ textAlign: 'center', padding: 24, color: 'var(--text-muted)' }}>{t.pos.noReceipts}</td></tr>
              ) : receipts.map(s => (
                <tr key={s.id}>
                  <td>#{s.id}</td>
                  <td>{clock(s.createdUtc)}</td>
                  <td>
                    {s.items.map(i => `${i.name}${i.quantity > 1 ? ` ×${i.quantity}` : ''}`).join(', ')}
                    {s.kind === 'refund' && <div className="audit-details">{fmt(t.pos.refundOf, { id: s.refundOfSaleId ?? '' })}: {s.reason}</div>}
                  </td>
                  <td>{methodLabel(s.method)}{s.username && <div className="user-chip">@{s.username}</div>}</td>
                  <td className={s.kind === 'refund' ? 'money-minus' : ''} style={{ fontWeight: 600 }}>{s.kind === 'refund' ? '−' : ''}{money(s.total)} {t.currency}</td>
                  <td className="user-chip">{s.actorUsername ?? t.none}</td>
                  <td>
                    {s.kind === 'sale' && (s.refundedBySaleId
                      ? <span className="badge badge-amber">{t.pos.refunded}</span>
                      : can.management && <button className="btn btn-danger btn-sm" onClick={() => setRefundTarget(s)}>{t.pos.refund}</button>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {refundTarget && (
        <RefundDialog sale={refundTarget} toast={toast} onClose={() => setRefundTarget(null)}
          onDone={() => { setRefundTarget(null); load(); onChanged(); }} />
      )}
    </>
  );
}

function RefundDialog({ sale, toast, onClose, onDone }: { sale: Sale; toast: ToastFn; onClose: () => void; onDone: () => void }) {
  const [reason, setReason] = useState('');
  const [returnToStock, setReturnToStock] = useState(true);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    const res = await postJson(`/api/sales/${sale.id}/refund`, { reason, returnToStock });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    toast(fmt(t.pos.refundDone, { id: sale.id }), 'success');
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">↩️ {fmt(t.pos.refundTitle, { id: sale.id })}</div>
        <div className="cost-box">
          {sale.items.map(i => `${i.name} ×${i.quantity}`).join(', ')} — <strong>{money(sale.total)} {t.currency}</strong> · {methodLabel(sale.method)}
        </div>
        <div className="auth-sub">{t.pos.refundHint}</div>
        <input className="form-input" placeholder={t.reason} value={reason} onChange={e => setReason(e.target.value)} autoFocus />
        <label className="check-row"><input type="checkbox" checked={returnToStock} onChange={e => setReturnToStock(e.target.checked)} /> {t.pos.returnToStock}</label>
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-danger" disabled={busy || reason.trim().length < 3} onClick={submit}>{t.pos.refund}</button>
        </div>
      </div>
    </div>
  );
}
