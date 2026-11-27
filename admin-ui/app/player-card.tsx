'use client';
import { useCallback, useEffect, useState } from 'react';
import { Permissions, errorText, getJson, postJson } from './api';
import { BillingSettings, LedgerCheck, LedgerEntry, cashbackFor } from './billing';
import { fmt, ledgerTypeLabel, methodLabel, money, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Player = { id: string; username: string; displayName: string; balance: number; bonusPoints: number; createdUtc: string };
type Form = 'topup' | 'refund' | 'adjust' | null;

const fmtDateTime = (iso: string) => {
  const d = new Date(iso);
  return `${d.toLocaleDateString('uk-UA')} ${d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })}`;
};

const signed = (n: number) => (n > 0 ? `+${money(n)}` : money(n));

/**
 * Player card: real balance, bonus balance, ledger history (append-only), top-up (front desk),
 * refund and corrections (Owner, Manager). The server checks every role again.
 */
export function PlayerCard({ player, can, toast, initialForm, onClose, onChanged }: {
  player: Player; can: Permissions; toast: ToastFn; initialForm?: Form; onClose: () => void; onChanged: () => void;
}) {
  const [me, setMe] = useState<Player>(player);
  const [rows, setRows] = useState<LedgerEntry[]>([]);
  const [more, setMore] = useState(false);
  const [check, setCheck] = useState<LedgerCheck | null>(null);
  const [form, setForm] = useState<Form>(initialForm ?? null);
  const [settings, setSettings] = useState<BillingSettings | null>(null);

  const load = useCallback(async () => {
    const [u, page, c] = await Promise.all([
      getJson<Player>(`/api/users/${player.id}`),
      getJson<LedgerEntry[]>(`/api/users/${player.id}/ledger?take=50`),
      getJson<LedgerCheck>(`/api/users/${player.id}/ledger/check`),
    ]);
    if (u) setMe(u);
    setRows(page ?? []);
    setMore((page?.length ?? 0) === 50);
    setCheck(c);
  }, [player.id]);

  useEffect(() => { load(); getJson<BillingSettings>('/api/billing/settings').then(setSettings); }, [load]);

  const loadMore = async () => {
    const last = rows[rows.length - 1];
    const page = await getJson<LedgerEntry[]>(`/api/users/${player.id}/ledger?take=50&beforeId=${last?.id ?? ''}`) ?? [];
    setRows([...rows, ...page]);
    setMore(page.length === 50);
  };

  const done = (message: string) => {
    toast(message, 'success');
    setForm(null);
    load();
    onChanged();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal modal-wide" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{fmt(t.player.cardTitle, { username: me.username })} · {me.displayName}</div>

        <div className="stats-grid" style={{ marginBottom: 12 }}>
          <div className="stat-card">
            <div className="stat-card-label">{t.player.balance}</div>
            <div className="stat-card-value" style={{ color: 'var(--green)' }}>{money(me.balance)} {t.currency}</div>
          </div>
          <div className="stat-card">
            <div className="stat-card-label">{t.player.bonus}</div>
            <div className="stat-card-value" style={{ color: 'var(--amber)' }}>{money(me.bonusPoints)} {t.currency}</div>
            <div className="stat-card-sub">{t.player.bonusHint}</div>
          </div>
        </div>
        {check && (
          <div className={check.ok ? 'user-chip' : 'auth-error'} style={{ marginBottom: 10 }}>
            {check.ok ? t.player.reconciled : `${t.player.notReconciled} (${money(check.ledgerBalance)} / ${money(check.ledgerBonus)})`}
          </div>
        )}

        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 12 }}>
          {can.frontDesk && <button className="btn btn-success btn-sm" onClick={() => setForm('topup')}>{t.player.topUp}</button>}
          {can.management && <button className="btn btn-sm" onClick={() => setForm('refund')}>{t.player.refund}</button>}
          {can.management && <button className="btn btn-sm" onClick={() => setForm('adjust')}>{t.player.adjust}</button>}
          {!can.management && <span className="user-chip">{t.player.managerOnly}</span>}
        </div>

        {form === 'topup' && <TopUpForm player={me} settings={settings} toast={toast} onCancel={() => setForm(null)} onDone={done} />}
        {form === 'refund' && <RefundForm player={me} toast={toast} onCancel={() => setForm(null)} onDone={done} />}
        {form === 'adjust' && <AdjustForm player={me} toast={toast} onCancel={() => setForm(null)} onDone={done} />}

        <div className="form-label" style={{ marginTop: 8 }}>{t.player.history}</div>
        <div className="table-wrapper ledger-table">
          <table className="data-table">
            <thead><tr>
              <th>{t.player.col.time}</th><th>{t.player.col.type}</th><th>{t.player.col.amount}</th><th>{t.player.col.balance}</th>
              <th>{t.player.col.bonus}</th><th>{t.player.col.method}</th><th>{t.player.col.description}</th><th>{t.player.col.who}</th>
            </tr></thead>
            <tbody>
              {rows.length === 0 ? (
                <tr><td colSpan={8} style={{ textAlign: 'center', padding: 20, color: 'var(--text-muted)' }}>{t.player.noHistory}</td></tr>
              ) : rows.map(r => (
                <tr key={r.id}>
                  <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(r.createdUtc)}</td>
                  <td><span className={`badge ${r.balanceDelta + r.bonusDelta < 0 || r.deskAmount < 0 ? 'badge-red' : r.type === 'bonus_accrual' ? 'badge-amber' : 'badge-blue'}`}>{ledgerTypeLabel(r.type)}</span></td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {r.balanceDelta !== 0 && <div className={r.balanceDelta > 0 ? 'money-plus' : 'money-minus'}>{signed(r.balanceDelta)} {t.currency}</div>}
                    {r.bonusDelta !== 0 && <div className={r.bonusDelta > 0 ? 'money-plus' : 'money-minus'}>{signed(r.bonusDelta)} бонус</div>}
                    {r.balanceDelta === 0 && r.bonusDelta === 0 && <div>{money(r.amount)} {t.currency}</div>}
                  </td>
                  <td>{r.balanceAfter !== null ? money(r.balanceAfter) : t.none}</td>
                  <td>{r.bonusAfter !== null ? money(r.bonusAfter) : t.none}</td>
                  <td>{methodLabel(r.method === 'none' ? null : r.method)}</td>
                  <td>{r.description}{r.reason && r.description?.includes(r.reason) !== true && <div className="audit-details">{r.reason}</div>}</td>
                  <td className="user-chip">{r.actorUsername ?? t.none}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {more && <div style={{ textAlign: 'center', marginTop: 8 }}><button className="btn btn-sm" onClick={loadMore}>{t.more}</button></div>}

        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.close}</button>
        </div>
      </div>
    </div>
  );
}

function TopUpForm({ player, settings, toast, onCancel, onDone }: {
  player: Player; settings: BillingSettings | null; toast: ToastFn; onCancel: () => void; onDone: (message: string) => void;
}) {
  const [amount, setAmount] = useState('100');
  const [method, setMethod] = useState<'cash' | 'card'>('cash');
  const [busy, setBusy] = useState(false);
  const value = parseFloat(amount.replace(',', '.')) || 0;
  const bonus = cashbackFor(settings?.bonusTopUpRules ?? [], value);

  const submit = async () => {
    setBusy(true);
    const res = await postJson(`/api/users/${player.id}/topup`, { amount: value, method });
    setBusy(false);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    onDone(fmt(t.player.topUpDone, { amount: money(value) }) + (bonus > 0 ? fmt(t.player.bonusAdded, { bonus: money(bonus) }) : ''));
  };

  return (
    <div className="section-card">
      <div className="section-card-title" style={{ marginBottom: 8 }}>{t.player.topUpTitle}</div>
      <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(5, 1fr)' }}>
        {[50, 100, 200, 500, 1000].map(p => (
          <div key={p} className={`quick-select-item ${amount === String(p) ? 'selected' : ''}`} onClick={() => setAmount(String(p))}>
            <div className="quick-select-label">{p} {t.currency}</div>
            {cashbackFor(settings?.bonusTopUpRules ?? [], p) > 0 && <div className="quick-select-sub">+{money(cashbackFor(settings?.bonusTopUpRules ?? [], p))}</div>}
          </div>
        ))}
      </div>
      <div className="form-grid">
        <input className="form-input" type="number" min="1" value={amount} onChange={e => setAmount(e.target.value)} />
        <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(2, 1fr)', marginBottom: 0 }}>
          {(['cash', 'card'] as const).map(m => (
            <div key={m} className={`quick-select-item ${method === m ? 'selected' : ''}`} onClick={() => setMethod(m)}>
              <div className="quick-select-label">{methodLabel(m)}</div>
            </div>
          ))}
        </div>
      </div>
      {bonus > 0 && <div className="auth-sub" style={{ color: 'var(--amber)' }}>{fmt(t.bonus.preview, { bonus: money(bonus) })}</div>}
      <div className="modal-actions">
        <button className="btn btn-sm" onClick={onCancel}>{t.cancel}</button>
        <button className="btn btn-success btn-sm" disabled={busy || value <= 0} onClick={submit}>{t.player.topUp} {money(value)} {t.currency}</button>
      </div>
    </div>
  );
}

function RefundForm({ player, toast, onCancel, onDone }: { player: Player; toast: ToastFn; onCancel: () => void; onDone: (message: string) => void }) {
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState<'cash' | 'card'>('cash');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const value = parseFloat(amount.replace(',', '.')) || 0;

  const submit = async () => {
    setBusy(true);
    const res = await postJson(`/api/users/${player.id}/refund`, { amount: value, method, reason });
    setBusy(false);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    onDone(fmt(t.player.refundDone, { amount: money(value) }));
  };

  return (
    <div className="section-card">
      <div className="section-card-title" style={{ marginBottom: 8 }}>{t.player.refundTitle}</div>
      <div className="auth-sub">{t.player.refundHint}</div>
      <div className="form-grid">
        <input className="form-input" type="number" min="1" max={player.balance} placeholder={`${t.player.amount} (≤ ${money(player.balance)})`} value={amount} onChange={e => setAmount(e.target.value)} />
        <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(2, 1fr)', marginBottom: 0 }}>
          {(['cash', 'card'] as const).map(m => (
            <div key={m} className={`quick-select-item ${method === m ? 'selected' : ''}`} onClick={() => setMethod(m)}>
              <div className="quick-select-label">{methodLabel(m)}</div>
            </div>
          ))}
        </div>
      </div>
      <input className="form-input" placeholder={t.player.reason} value={reason} onChange={e => setReason(e.target.value)} style={{ marginTop: 8 }} />
      <div className="modal-actions">
        <button className="btn btn-sm" onClick={onCancel}>{t.cancel}</button>
        <button className="btn btn-danger btn-sm" disabled={busy || value <= 0 || value > player.balance || reason.trim().length < 3} onClick={submit}>{t.player.refund}</button>
      </div>
    </div>
  );
}

function AdjustForm({ player, toast, onCancel, onDone }: { player: Player; toast: ToastFn; onCancel: () => void; onDone: (message: string) => void }) {
  const [target, setTarget] = useState<'balance' | 'bonus'>('balance');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const value = parseFloat(amount.replace(',', '.')) || 0;

  const submit = async () => {
    setBusy(true);
    const res = await postJson(`/api/users/${player.id}/adjust`, { amount: value, reason, target });
    setBusy(false);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    onDone(t.player.adjustDone);
  };

  return (
    <div className="section-card">
      <div className="section-card-title" style={{ marginBottom: 8 }}>{t.player.adjustTitle}</div>
      <div className="auth-sub">{t.player.adjustHint}</div>
      <div className="form-grid">
        <div className="quick-select-grid" style={{ gridTemplateColumns: 'repeat(2, 1fr)', marginBottom: 0 }}>
          {(['balance', 'bonus'] as const).map(x => (
            <div key={x} className={`quick-select-item ${target === x ? 'selected' : ''}`} onClick={() => setTarget(x)}>
              <div className="quick-select-label">{x === 'balance' ? t.player.targetBalance : t.player.targetBonus}</div>
            </div>
          ))}
        </div>
        <input className="form-input" type="number" placeholder={`${t.player.amount}: -50 / 100`} value={amount} onChange={e => setAmount(e.target.value)} />
      </div>
      <input className="form-input" placeholder={t.player.reason} value={reason} onChange={e => setReason(e.target.value)} style={{ marginTop: 8 }} />
      <div className="modal-actions">
        <button className="btn btn-sm" onClick={onCancel}>{t.cancel}</button>
        <button className="btn btn-primary btn-sm" disabled={busy || value === 0 || reason.trim().length < 3} onClick={submit}>{t.save}</button>
      </div>
    </div>
  );
}
