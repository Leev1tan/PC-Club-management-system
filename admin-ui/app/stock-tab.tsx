'use client';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Permissions, errorText, getJson, postJson } from './api';
import { dateTime, fmt, money, t } from './i18n';
import { Product, StockCheck, StockMovement } from './pos';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Dialog = { kind: 'edit'; product: Product | null } | { kind: 'restock' | 'correct'; product: Product } | null;

/**
 * Bar catalog and stock: products with price, category, on-sale flag and optional stock count; a
 * delivery (front desk); a stock count with a reason and product changes (Owner, Manager); the stock log.
 */
export function StockTab({ can, toast }: { can: Permissions; toast: ToastFn }) {
  const [products, setProducts] = useState<Product[]>([]);
  const [movements, setMovements] = useState<StockMovement[]>([]);
  const [check, setCheck] = useState<StockCheck | null>(null);
  const [dialog, setDialog] = useState<Dialog>(null);

  const load = useCallback(async () => {
    const [p, m, c] = await Promise.all([
      getJson<Product[]>(`/api/products${can.management ? '?includeInactive=true' : ''}`),
      getJson<StockMovement[]>('/api/products/movements?take=100'),
      can.management ? getJson<StockCheck>('/api/products/stock/check') : Promise.resolve(null),
    ]);
    setProducts(p ?? []);
    setMovements(m ?? []);
    setCheck(c);
  }, [can.management]);
  useEffect(() => { load(); }, [load]);

  const done = (message: string) => { toast(message, 'success'); setDialog(null); load(); };
  const categories = useMemo(() => [...new Set(products.map(p => p.category))], [products]);
  const low = products.filter(p => p.lowStock && p.isActive);

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.stock.title}</span>
        <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {check && <span className={check.ok ? 'badge badge-green' : 'badge badge-red'}>{check.ok ? t.stock.check : t.stock.checkBad}</span>}
          {can.management && <button className="btn btn-primary btn-sm" onClick={() => setDialog({ kind: 'edit', product: null })}>{t.stock.add}</button>}
        </span>
      </div>
      {!can.management && <div className="auth-sub">{t.stock.readOnly}</div>}
      {low.length > 0 && (
        <div className="section-card low-stock-box">
          ⚠ <strong>{t.stock.low}:</strong> {low.map(p => `${p.name} (${p.stockQuantity})`).join(', ')}
        </div>
      )}

      <div className="table-wrapper">
        <table className="data-table stock-table">
          <thead><tr>
            <th>{t.stock.name}</th><th>{t.stock.category}</th><th>{t.stock.price}</th><th>{t.stock.active}</th><th>{t.stock.stock}</th><th>{t.stock.threshold}</th><th></th>
          </tr></thead>
          <tbody>
            {products.map(p => (
              <tr key={p.id} className={p.isActive ? '' : 'row-muted'}>
                <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{p.name}</td>
                <td>{p.category}</td>
                <td>{money(p.price)} {t.currency}</td>
                <td>{p.isActive ? t.yes : <span className="badge badge-red">{t.no}</span>}</td>
                <td>
                  {p.trackStock
                    ? <span className={`badge ${p.lowStock ? 'badge-amber' : 'badge-green'}`}>{p.stockQuantity}{p.lowStock ? ` · ${t.pos.low}` : ''}</span>
                    : <span className="user-chip">{t.stock.notTracked}</span>}
                </td>
                <td>{p.lowStockThreshold ?? t.none}</td>
                <td>
                  <div style={{ display: 'flex', gap: 4 }}>
                    {p.trackStock && <button className="btn btn-success btn-sm" onClick={() => setDialog({ kind: 'restock', product: p })}>{t.stock.restock}</button>}
                    {p.trackStock && can.management && <button className="btn btn-sm" onClick={() => setDialog({ kind: 'correct', product: p })}>{t.stock.correct}</button>}
                    {can.management && <button className="btn btn-sm" onClick={() => setDialog({ kind: 'edit', product: p })}>{t.stock.edit}</button>}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="section-card" style={{ marginTop: 16 }}>
        <div className="section-card-header">
          <span className="section-card-title">{t.stock.log}</span>
          <button className="btn btn-sm" onClick={load}>↻</button>
        </div>
        <div className="table-wrapper">
          <table className="data-table">
            <thead><tr>
              <th>{t.stock.col.time}</th><th>{t.stock.col.product}</th><th>{t.stock.col.type}</th><th>{t.stock.col.delta}</th>
              <th>{t.stock.col.after}</th><th>{t.stock.col.who}</th><th>{t.stock.col.note}</th>
            </tr></thead>
            <tbody>
              {movements.map(m => (
                <tr key={m.id}>
                  <td style={{ whiteSpace: 'nowrap' }}>{dateTime(m.createdUtc)}</td>
                  <td>{m.productName}</td>
                  <td><span className={`badge ${m.type === 'correction' ? 'badge-red' : m.type === 'restock' ? 'badge-green' : 'badge-blue'}`}>{t.stock.types[m.type] ?? m.type}</span></td>
                  <td className={m.delta < 0 ? 'money-minus' : 'money-plus'}>{m.delta > 0 ? `+${m.delta}` : m.delta}</td>
                  <td>{m.quantityAfter}</td>
                  <td className="user-chip">{m.actorUsername ?? t.none}</td>
                  <td>{m.reason}{m.saleId ? ` #${m.saleId}` : ''}{m.unitCost != null ? ` · ${money(m.unitCost)} ${t.currency}` : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {dialog?.kind === 'edit' && <ProductDialog product={dialog.product} categories={categories} toast={toast} onClose={() => setDialog(null)} onDone={() => done(t.stock.saved)} />}
      {dialog?.kind === 'restock' && <RestockDialog product={dialog.product} toast={toast} onClose={() => setDialog(null)} onDone={n => done(fmt(t.stock.restockDone, { n }))} />}
      {dialog?.kind === 'correct' && <CorrectionDialog product={dialog.product} toast={toast} onClose={() => setDialog(null)} onDone={() => done(t.stock.correctDone)} />}
    </>
  );
}

function ProductDialog({ product, categories, toast, onClose, onDone }: {
  product: Product | null; categories: string[]; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const [name, setName] = useState(product?.name ?? '');
  const [category, setCategory] = useState(product?.category ?? categories[0] ?? '');
  const [price, setPrice] = useState(product ? String(product.price) : '');
  const [isActive, setIsActive] = useState(product?.isActive ?? true);
  const [trackStock, setTrackStock] = useState(product?.trackStock ?? true);
  const [threshold, setThreshold] = useState(product?.lowStockThreshold != null ? String(product.lowStockThreshold) : '');
  const [initial, setInitial] = useState('');
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    const body = {
      name, category, price: parseFloat(price.replace(',', '.')), isActive, trackStock,
      lowStockThreshold: threshold === '' ? null : parseInt(threshold, 10), initialStock: !product && initial !== '' ? parseInt(initial, 10) : null,
    };
    const res = await postJson(product ? `/api/products/${product.id}` : '/api/products', body, product ? 'PUT' : 'POST');
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{product ? t.stock.editTitle : t.stock.newTitle}</div>
        <div className="form-group"><label className="form-label">{t.stock.name}</label>
          <input className="form-input" value={name} onChange={e => setName(e.target.value)} /></div>
        <div className="form-group"><label className="form-label">{t.stock.category}</label>
          <input className="form-input" list="product-categories" value={category} onChange={e => setCategory(e.target.value)} />
          <datalist id="product-categories">{categories.map(c => <option key={c} value={c} />)}</datalist></div>
        <div className="form-group"><label className="form-label">{t.stock.price}</label>
          <input className="form-input" type="number" min="0.01" step="0.01" value={price} onChange={e => setPrice(e.target.value)} /></div>
        <label className="check-row"><input type="checkbox" checked={isActive} onChange={e => setIsActive(e.target.checked)} /> {t.stock.active}</label>
        <label className="check-row"><input type="checkbox" checked={trackStock} onChange={e => setTrackStock(e.target.checked)} /> {t.stock.track}</label>
        {trackStock && (
          <div className="form-grid">
            <div className="form-group"><label className="form-label">{t.stock.threshold}</label>
              <input className="form-input" type="number" min="0" value={threshold} onChange={e => setThreshold(e.target.value)} /></div>
            {!product && <div className="form-group"><label className="form-label">{t.stock.initial}</label>
              <input className="form-input" type="number" min="0" value={initial} onChange={e => setInitial(e.target.value)} /></div>}
          </div>
        )}
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !name.trim() || !(parseFloat(price) > 0)} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}

function RestockDialog({ product, toast, onClose, onDone }: { product: Product; toast: ToastFn; onClose: () => void; onDone: (n: number) => void }) {
  const [quantity, setQuantity] = useState('24');
  const [unitCost, setUnitCost] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const n = parseInt(quantity, 10);

  const save = async () => {
    setBusy(true);
    const res = await postJson(`/api/products/${product.id}/restock`, { quantity: n, unitCost: unitCost === '' ? null : parseFloat(unitCost.replace(',', '.')), note: note || null });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    onDone(n);
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">📦 {fmt(t.stock.restockTitle, { name: product.name })}</div>
        <div className="auth-sub">{t.stock.stock}: {product.stockQuantity}</div>
        <div className="form-grid">
          <div className="form-group"><label className="form-label">{t.stock.quantity}</label>
            <input className="form-input" type="number" min="1" value={quantity} onChange={e => setQuantity(e.target.value)} autoFocus /></div>
          <div className="form-group"><label className="form-label">{t.stock.unitCost}</label>
            <input className="form-input" type="number" min="0" step="0.01" value={unitCost} onChange={e => setUnitCost(e.target.value)} /></div>
        </div>
        <input className="form-input" placeholder={t.note} value={note} onChange={e => setNote(e.target.value)} />
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-success" disabled={busy || !(n >= 1)} onClick={save}>{t.stock.restock}</button>
        </div>
      </div>
    </div>
  );
}

function CorrectionDialog({ product, toast, onClose, onDone }: { product: Product; toast: ToastFn; onClose: () => void; onDone: () => void }) {
  const [counted, setCounted] = useState(String(product.stockQuantity));
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const n = parseInt(counted, 10);

  const save = async () => {
    setBusy(true);
    const res = await postJson(`/api/products/${product.id}/correction`, { countedQuantity: n, reason });
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">🔎 {fmt(t.stock.correctTitle, { name: product.name })}</div>
        <div className="auth-sub">{t.stock.correctHint}</div>
        <div className="form-group"><label className="form-label">{t.stock.counted} ({t.stock.stock}: {product.stockQuantity})</label>
          <input className="form-input" type="number" min="0" value={counted} onChange={e => setCounted(e.target.value)} autoFocus /></div>
        <input className="form-input" placeholder={t.reason} value={reason} onChange={e => setReason(e.target.value)} />
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !(n >= 0) || reason.trim().length < 3} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}
