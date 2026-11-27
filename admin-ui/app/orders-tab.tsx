'use client';
import { useCallback, useEffect, useState } from 'react';
import { errorText, getJson, postJson } from './api';
import { cash, clubClock, fmt, methodLabel, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
export type BarOrder = {
  id: number; status: 'new' | 'preparing' | 'delivered' | 'cancelled'; payment: 'balance' | 'desk'; deskMethod: string | null; total: number;
  deviceId: string; hostname: string; username: string | null; createdUtc: string; updatedUtc: string; deliveredUtc: string | null;
  saleId: number | null; cancelReason: string | null; handledBy: string | null;
  items: { name: string; quantity: number; unitPrice: number; lineTotal: number }[];
};
export type OrderQueue = { newCount: number; open: BarOrder[]; recent: BarOrder[] };
type OrderSettings = { enabled: boolean; maxAmount: number };

const failed = async (res: Response) => `${t.common.errorPrefix}: ${await errorText(res)}`;
const statusBadge = (s: BarOrder['status']) => s === 'new' ? 'badge-amber' : s === 'preparing' ? 'badge-blue' : s === 'delivered' ? 'badge-green' : 'badge-red';
const minutesAgo = (iso: string) => Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 60000));

/** A short tone for a new order (Web Audio, no sound file); silent when the browser does not allow it yet. */
export function orderChime() {
  try {
    const Ctx = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    const ctx = new Ctx();
    const tone = (freq: number, at: number) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.frequency.value = freq;
      gain.gain.setValueAtTime(0.0001, ctx.currentTime + at);
      gain.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + at + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + at + 0.18);
      osc.connect(gain).connect(ctx.destination);
      osc.start(ctx.currentTime + at);
      osc.stop(ctx.currentTime + at + 0.2);
    };
    tone(880, 0);
    tone(1320, 0.2);
    setTimeout(() => ctx.close(), 800);
  } catch { /* no audio */ }
}

/** Orders from the lock screens (front desk): New → Preparing → Delivered (the bar sale), or Cancel (a prepaid one goes back). */
export function OrdersTab({ toast, management, onChanged }: { toast: ToastFn; management: boolean; onChanged: () => void }) {
  const [queue, setQueue] = useState<OrderQueue | null>(null);
  const [busy, setBusy] = useState<number | null>(null);

  const load = useCallback(async () => setQueue(await getJson<OrderQueue>('/api/orders')), []);
  useEffect(() => {
    load();
    const iv = setInterval(load, 5000);
    return () => clearInterval(iv);
  }, [load]);

  const act = async (o: BarOrder, action: 'preparing' | 'deliver' | 'cancel', body: object = {}) => {
    if (action === 'cancel') {
      const reason = window.prompt(o.payment === 'balance' ? t.orders.cancelPromptRefund : t.orders.cancelPrompt, '');
      if (reason === null) return;
      body = { reason };
    }
    setBusy(o.id);
    const res = await postJson(`/api/orders/${o.id}/${action}`, body);
    setBusy(null);
    toast(res.ok ? fmt(action === 'deliver' ? t.orders.delivered : action === 'cancel' ? t.orders.cancelled : t.orders.preparing, { id: o.id }) : await failed(res),
      res.ok ? 'success' : 'error');
    load();
    onChanged();
  };

  const card = (o: BarOrder, open: boolean) => (
    <div key={o.id} className={`order-card status-${o.status}`}>
      <div className="order-head">
        <strong>#{o.id} · {o.hostname}</strong>
        <span className={`badge ${statusBadge(o.status)}`}>{(t.orders.status as Record<string, string>)[o.status]}</span>
      </div>
      <div className="order-sub">{o.username ? '@' + o.username : t.guest} · {clubClock(o.createdUtc)}{open ? ` · ${fmt(t.orders.minutesAgo, { n: minutesAgo(o.createdUtc) })}` : ''}</div>
      <ul className="order-items">
        {o.items.map((i, k) => <li key={k}><span>{i.quantity > 1 ? `${i.quantity} × ` : ''}{i.name}</span><span>{cash(i.lineTotal)}</span></li>)}
      </ul>
      <div className="order-total">
        <span>{o.payment === 'balance' ? t.orders.prepaid : o.deskMethod ? `${t.orders.atDesk}: ${methodLabel(o.deskMethod)}` : t.orders.atDesk}</span>
        <strong>{cash(o.total)}</strong>
      </div>
      {o.cancelReason && <div className="order-sub">{t.reason}: {o.cancelReason}</div>}
      {open && (
        <div className="order-actions">
          {o.status === 'new' && <button className="btn btn-sm" disabled={busy === o.id} onClick={() => act(o, 'preparing')}>{t.orders.toPreparing}</button>}
          {o.payment === 'balance'
            ? <button className="btn btn-success btn-sm" disabled={busy === o.id} onClick={() => act(o, 'deliver')}>{t.orders.deliver}</button>
            : <>
                <button className="btn btn-success btn-sm" disabled={busy === o.id} onClick={() => act(o, 'deliver', { paymentMethod: 'cash' })}>{t.orders.deliverCash}</button>
                <button className="btn btn-success btn-sm" disabled={busy === o.id} onClick={() => act(o, 'deliver', { paymentMethod: 'card' })}>{t.orders.deliverCard}</button>
              </>}
          <button className="btn btn-danger btn-sm" disabled={busy === o.id} onClick={() => act(o, 'cancel')}>{t.orders.cancel}</button>
        </div>
      )}
    </div>
  );

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.orders.title}</span>
        {queue && <span className="badge badge-amber">{fmt(t.orders.newCount, { n: queue.newCount })}</span>}
      </div>
      <div className="form-hint" style={{ marginBottom: 12 }}>{t.orders.hint}</div>
      {!queue ? <div className="form-hint">{t.loading}</div> : queue.open.length === 0
        ? <div className="empty-state"><div className="empty-state-icon">🥤</div><div className="empty-state-title">{t.orders.empty}</div></div>
        : <div className="order-grid">{queue.open.map(o => card(o, true))}</div>}
      {queue && queue.recent.length > 0 && (
        <div className="section-card" style={{ marginTop: 16 }}>
          <div className="section-card-header"><span className="section-card-title">{t.orders.recent}</span></div>
          <div className="order-grid">{queue.recent.map(o => card(o, false))}</div>
        </div>
      )}
      {management && <OrderSettingsCard toast={toast} />}
    </>
  );
}

function OrderSettingsCard({ toast }: { toast: ToastFn }) {
  const [s, setS] = useState<OrderSettings | null>(null);
  useEffect(() => { getJson<OrderSettings>('/api/orders/settings').then(setS); }, []);
  if (!s) return null;
  const save = async () => {
    const res = await postJson('/api/orders/settings', s, 'PUT');
    toast(res.ok ? t.orders.settingsSaved : await failed(res), res.ok ? 'success' : 'error');
  };
  return (
    <div className="section-card" style={{ marginTop: 16 }}>
      <div className="section-card-header"><span className="section-card-title">{t.orders.settings}</span></div>
      <label className="check-row"><input type="checkbox" checked={s.enabled} onChange={e => setS({ ...s, enabled: e.target.checked })} /> {t.orders.enabled}</label>
      <div className="form-group" style={{ maxWidth: 260 }}>
        <label className="form-label">{t.orders.maxAmount}</label>
        <input className="form-input" type="number" min={10} max={10000} step={10} value={s.maxAmount} onChange={e => setS({ ...s, maxAmount: Number(e.target.value) })} />
      </div>
      <button className="btn btn-primary btn-sm" onClick={save}>{t.save}</button>
    </div>
  );
}
