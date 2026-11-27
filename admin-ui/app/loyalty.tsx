'use client';
import { useCallback, useEffect, useState } from 'react';
import { errorText, getJson, postJson } from './api';
import { cash, fmt, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
export type LoyaltyTier = { name: string; minSpend: number; topUpBonusPercent: number; pcDiscountPercent: number };
type LoyaltyView = { periodDays: number; tiers: LoyaltyTier[]; players: number[] };
type PlayerLoyalty = { enabled: boolean; periodDays: number; spend: number; tier: LoyaltyTier | null; next: LoyaltyTier | null; toNext: number };

/**
 * Loyalty tiers (Tariffs tab): the tiers by real money spent in a rolling period, each with a bonus percent on
 * top-ups and a discount on PC time. Only the Owner edits them (the server checks it); others see them read-only.
 */
export function LoyaltySection({ canEdit, toast }: { canEdit: boolean; toast: ToastFn }) {
  const [view, setView] = useState<LoyaltyView | null>(null);
  const [draft, setDraft] = useState<{ periodDays: number; tiers: LoyaltyTier[] } | null>(null);
  const load = useCallback(async () => setView(await getJson<LoyaltyView>('/api/loyalty')), []);
  useEffect(() => { load(); }, [load]);
  if (!view) return null;
  const shown = draft ?? { periodDays: view.periodDays, tiers: view.tiers };
  const edit = (i: number, patch: Partial<LoyaltyTier>) => setDraft({ ...shown, tiers: shown.tiers.map((x, j) => j === i ? { ...x, ...patch } : x) });

  const save = async () => {
    const res = await postJson('/api/loyalty', shown, 'PUT');
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    toast(t.loyalty.saved, 'success');
    setDraft(null);
    setView(await res.json());
  };

  return (
    <div className="section-card">
      <div className="section-card-header">
        <span className="section-card-title">{t.loyalty.title}</span>
        {canEdit && shown.tiers.length < 6 && (
          <button className="btn btn-sm" onClick={() => setDraft({ ...shown, tiers: [...shown.tiers, { name: '', minSpend: (shown.tiers.at(-1)?.minSpend ?? 0) + 1000, topUpBonusPercent: 0, pcDiscountPercent: 0 }] })}>
            {t.loyalty.addTier}
          </button>
        )}
      </div>
      <div className="auth-sub">{fmt(t.loyalty.hint, { days: shown.periodDays })}</div>
      {!canEdit && <div className="form-hint">{t.loyalty.ownerOnly}</div>}
      {shown.tiers.length === 0 && <div className="user-chip">{t.loyalty.off}</div>}
      {canEdit ? (
        <>
          <label className="rule-times">{t.loyalty.period}
            <input className="form-input" type="number" min={7} max={365} value={shown.periodDays}
              onChange={e => setDraft({ ...shown, periodDays: Number(e.target.value) })} />
          </label>
          {shown.tiers.map((tier, i) => (
            <div key={i} className="tier-row loyalty-row">
              <label className="rule-times">{t.loyalty.name}
                <input className="form-input" maxLength={20} value={tier.name} onChange={e => edit(i, { name: e.target.value })} />
              </label>
              <label className="rule-times">{t.loyalty.minSpend}
                <input className="form-input" type="number" min={0} step={100} value={tier.minSpend} onChange={e => edit(i, { minSpend: Number(e.target.value) })} />
              </label>
              <label className="rule-times">{t.loyalty.bonus}
                <input className="form-input" type="number" min={0} max={50} step={1} value={tier.topUpBonusPercent} onChange={e => edit(i, { topUpBonusPercent: Number(e.target.value) })} />
              </label>
              <label className="rule-times">{t.loyalty.discount}
                <input className="form-input" type="number" min={0} max={50} step={1} value={tier.pcDiscountPercent} onChange={e => edit(i, { pcDiscountPercent: Number(e.target.value) })} />
              </label>
              <button className="btn btn-sm btn-danger" aria-label={t.delete} onClick={() => setDraft({ ...shown, tiers: shown.tiers.filter((_, j) => j !== i) })}>✕</button>
            </div>
          ))}
          {draft && <button className="btn btn-primary btn-sm" onClick={save}>{t.save}</button>}
        </>
      ) : null}
      {!draft && view.tiers.length > 0 && (
        <div className="table-wrapper" style={{ marginTop: 8 }}>
          <table className="data-table">
            <thead><tr><th>{t.loyalty.name}</th><th>{t.loyalty.minSpend}</th><th>{t.loyalty.bonus}</th><th>{t.loyalty.discount}</th><th>{t.loyalty.players}</th></tr></thead>
            <tbody>
              {view.tiers.map((tier, i) => (
                <tr key={tier.name}>
                  <td><span className="badge badge-purple data">{tier.name}</span></td>
                  <td>{cash(tier.minSpend)}</td>
                  <td>{tier.topUpBonusPercent}%</td>
                  <td>{tier.pcDiscountPercent}%</td>
                  <td>{view.players[i] ?? 0}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

/** The tier of a player on the player card: the tier, the spend of the period and what is missing for the next tier. */
export function PlayerTier({ playerId, refresh }: { playerId: string; refresh: number }) {
  const [p, setP] = useState<PlayerLoyalty | null>(null);
  useEffect(() => { getJson<PlayerLoyalty>(`/api/users/${playerId}/loyalty`).then(setP); }, [playerId, refresh]);
  if (!p?.enabled) return null;
  return (
    <div className="stat-card">
      <div className="stat-card-label">{t.loyalty.tier}</div>
      <div className="stat-card-value" style={{ color: 'var(--event)' }}>{p.tier ? <span className="data">{p.tier.name}</span> : t.loyalty.noTier}</div>
      <div className="stat-card-sub tier-line">
        {fmt(t.loyalty.spent, { amount: cash(p.spend), days: p.periodDays })}
        {p.tier && (p.tier.topUpBonusPercent > 0 || p.tier.pcDiscountPercent > 0) && <> · {fmt(t.loyalty.perks, { bonus: p.tier.topUpBonusPercent, discount: p.tier.pcDiscountPercent })}</>}
        {p.next && <> · {fmt(t.loyalty.toNext, { amount: cash(p.toNext) })} <span className="data">{p.next.name}</span></>}
      </div>
    </div>
  );
}
