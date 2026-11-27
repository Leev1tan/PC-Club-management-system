'use client';
import { useCallback, useEffect, useState } from 'react';
import { Permissions, api, errorText, getJson, postJson } from './api';
import { BillingSettings, BonusTier, Package, Tariff, TariffRule } from './billing';
import { daysLabel, fmt, money, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Zone = { id: string; name: string; color: string; sortOrder: number };
type DeviceLite = { id: string; zoneId: string | null };

/** Tariffs (with time-of-day rules), package catalog and cashback. Owner and Manager edit; Cashier reads. */
export function TariffsTab({ can, zones, devices, tariffs, toast, onChanged, onAddZone, onDeleteZone }: {
  can: Permissions; zones: Zone[]; devices: DeviceLite[]; tariffs: Tariff[]; toast: ToastFn; onChanged: () => void;
  onAddZone: () => void; onDeleteZone: (id: string) => void;
}) {
  const [editTariff, setEditTariff] = useState<Tariff | 'new' | null>(null);
  const [editPackage, setEditPackage] = useState<Package | 'new' | null>(null);
  const [packages, setPackages] = useState<Package[]>([]);
  const [settings, setSettings] = useState<BillingSettings | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  const loadPackages = useCallback(async () => setPackages(await getJson<Package[]>('/api/packages') ?? []), []);
  const loadSettings = useCallback(async () => setSettings(await getJson<BillingSettings>('/api/billing/settings')), []);
  useEffect(() => { loadPackages(); loadSettings(); }, [loadPackages, loadSettings]);

  const deleteTariff = async (tariff: Tariff) => {
    if (!window.confirm(fmt(t.tariffs.confirmDelete, { name: tariff.name }))) return;
    const res = await api(`/api/tariffs/${tariff.id}`, { method: 'DELETE' });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(t.tariffs.deleted, 'info');
    onChanged();
  };

  const deletePackage = async (p: Package) => {
    if (!window.confirm(fmt(t.packages.confirmDelete, { name: p.name }))) return;
    const res = await api(`/api/packages/${p.id}`, { method: 'DELETE' });
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(t.packages.deleted, 'info');
    loadPackages();
  };

  const toggleActive = async (p: Package) => {
    const res = await postJson(`/api/packages/${p.id}`, toPackageRequest(p, { isActive: !p.isActive }), 'PUT');
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    loadPackages();
  };

  return (
    <>
      {/* Zones */}
      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">🗺️ Зони</span>
          {can.management && <button className="btn btn-primary btn-sm" onClick={onAddZone}>+ Додати зону</button>}
        </div>
        {zones.length === 0 ? (
          <div className="empty-state" style={{ padding: 30 }}>
            <div className="empty-state-sub">Зони дозволяють групувати ПК за локацією та ціною</div>
          </div>
        ) : (
          <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
            {zones.map(z => (
              <div key={z.id} className="stat-card" style={{ minWidth: 160, flex: '1 1 160px', borderLeftColor: z.color, borderLeftWidth: 3 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <span style={{ fontWeight: 600, fontSize: 14 }}>{z.name}</span>
                  {can.management && <button className="btn btn-sm btn-danger" onClick={() => onDeleteZone(z.id)} style={{ padding: '2px 6px', fontSize: 11 }}>✕</button>}
                </div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 4 }}>
                  {devices.filter(d => d.zoneId === z.id).length} ПК · {packages.filter(p => p.zoneId === z.id && p.isActive).length} пакетів
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Tariffs */}
      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">{t.tariffs.title}</span>
          {can.management
            ? <button className="btn btn-primary btn-sm" onClick={() => setEditTariff('new')}>{t.tariffs.add}</button>
            : <span className="user-chip">{t.tariffs.readOnly}</span>}
        </div>
        {settings && <div className="auth-sub">{fmt(t.bonus.timeZone, { zone: settings.timeZone })}</div>}
        {tariffs.length === 0 ? (
          <div className="empty-state" style={{ padding: 30 }}><div className="empty-state-sub">{t.tariffs.empty}</div></div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>{t.tariffs.name}</th><th>{t.tariffs.zone}</th><th>{t.tariffs.base}</th><th>{t.tariffs.nowPrice}</th>
                <th>{t.tariffs.rules}</th><th title={t.tariffs.step}>{t.tariffs.stepShort}</th><th>{t.tariffs.minimumShort}</th><th></th>
              </tr></thead>
              <tbody>
                {tariffs.map(tariff => (
                  <TariffRow key={tariff.id} tariff={tariff} open={open === tariff.id} onToggle={() => setOpen(open === tariff.id ? null : tariff.id)}
                    canEdit={can.management} onEdit={() => setEditTariff(tariff)} onDelete={() => deleteTariff(tariff)} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Packages */}
      <div className="section-card">
        <div className="section-card-header">
          <span className="section-card-title">{t.packages.title}</span>
          {can.management && <button className="btn btn-primary btn-sm" disabled={zones.length === 0} onClick={() => setEditPackage('new')}>{t.packages.add}</button>}
        </div>
        {packages.length === 0 ? (
          <div className="empty-state" style={{ padding: 30 }}><div className="empty-state-sub">{t.packages.empty}</div></div>
        ) : (
          <div className="table-wrapper">
            <table className="data-table">
              <thead><tr>
                <th>{t.packages.name}</th><th>{t.packages.zone}</th><th>{t.packages.price}</th><th>{t.packages.durationShort}</th>
                <th>{t.packages.window}</th><th>{t.packages.days}</th><th>{t.packages.availableNow}</th><th></th>
              </tr></thead>
              <tbody>
                {packages.map(p => (
                  <tr key={p.id} style={p.isActive ? undefined : { opacity: 0.5 }}>
                    <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{p.name}</td>
                    <td>{p.zoneName ?? t.none}</td>
                    <td style={{ fontWeight: 600, color: 'var(--green)' }}>{money(p.price)} {t.currency}</td>
                    <td>{p.durationMinutes ? `${p.durationMinutes / 60 >= 1 && p.durationMinutes % 60 === 0 ? `${p.durationMinutes / 60} ${t.hoursShort}` : `${p.durationMinutes} ${t.minutesShort}`}` : t.none}</td>
                    <td>{p.windowStart && p.windowEnd ? `${p.windowStart}–${p.windowEnd}` : t.packages.anyTime}</td>
                    <td>{daysLabel(p.days)}</td>
                    <td>
                      {!p.isActive ? <span className="badge badge-red">{t.packages.inactive}</span>
                        : p.availableNow ? <span className="badge badge-green">{p.endsAtIfStartedNow ? fmt(t.packages.until, { time: clock(p.endsAtIfStartedNow) }) : t.packages.availableNow}</span>
                        : <span className="badge badge-amber" title={p.unavailableReason ?? ''}>{t.packages.notNow}</span>}
                    </td>
                    <td>
                      {can.management && (
                        <div style={{ display: 'flex', gap: 4 }}>
                          <button className="btn btn-sm" onClick={() => setEditPackage(p)}>{t.edit}</button>
                          <button className="btn btn-sm" onClick={() => toggleActive(p)}>{p.isActive ? t.packages.takeOff : t.packages.putOn}</button>
                          <button className="btn btn-sm btn-danger" onClick={() => deletePackage(p)}>✕</button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Cashback */}
      <BonusSection settings={settings} canEdit={can.management} toast={toast} onSaved={loadSettings} />

      {editTariff && (
        <TariffEditor tariff={editTariff === 'new' ? null : editTariff} zones={zones} toast={toast}
          onClose={() => setEditTariff(null)} onSaved={() => { setEditTariff(null); onChanged(); }} />
      )}
      {editPackage && (
        <PackageEditor pkg={editPackage === 'new' ? null : editPackage} zones={zones} toast={toast}
          onClose={() => setEditPackage(null)} onSaved={() => { setEditPackage(null); loadPackages(); }} />
      )}
    </>
  );
}

const clock = (iso: string) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });

function TariffRow({ tariff, open, onToggle, canEdit, onEdit, onDelete }: {
  tariff: Tariff; open: boolean; onToggle: () => void; canEdit: boolean; onEdit: () => void; onDelete: () => void;
}) {
  return (
    <>
      <tr>
        <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>
          {tariff.name} {tariff.isDefault && <span className="badge badge-green">{t.tariffs.isDefault}</span>}
        </td>
        <td>{tariff.zoneName || t.tariffs.allZones}</td>
        <td>{money(tariff.pricePerHour)} {t.perHour}</td>
        <td style={{ fontWeight: 600, color: 'var(--green)' }} title={tariff.ruleNow ?? ''}>{money(tariff.priceNow)} {t.perHour}</td>
        <td>
          <button className="btn btn-sm" onClick={onToggle}>{tariff.rules.length} {open ? '▲' : '▼'}</button>
        </td>
        <td>{tariff.billingStepMinutes}</td>
        <td>{tariff.minimumCharge > 0 ? `${money(tariff.minimumCharge)} ${t.currency}` : t.none}</td>
        <td>
          {canEdit && (
            <div style={{ display: 'flex', gap: 4 }}>
              <button className="btn btn-sm" onClick={onEdit}>{t.edit}</button>
              <button className="btn btn-sm btn-danger" onClick={onDelete}>✕</button>
            </div>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={8}>
            {tariff.rules.length === 0 ? <span className="user-chip">{t.tariffs.noRules}</span> : (
              <div className="rule-list">
                {tariff.rules.map((r, i) => (
                  <div key={i} className="rule-line">
                    <span className="badge badge-blue">{i + 1}</span>
                    <strong>{r.name}</strong>
                    <span>{daysLabel(r.days)}</span>
                    <span>{r.start}–{r.end}</span>
                    <span style={{ color: 'var(--green)' }}>{money(r.pricePerHour)} {t.perHour}</span>
                  </div>
                ))}
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}

function DayPicker({ days, onChange }: { days: number[]; onChange: (days: number[]) => void }) {
  return (
    <div className="day-chips">
      {t.days.map((label, i) => {
        const day = i + 1;
        const on = days.includes(day);
        return (
          <button key={day} type="button" className={`filter-chip ${on ? 'active' : ''}`}
            onClick={() => onChange(on ? days.filter(d => d !== day) : [...days, day].sort())}>{label}</button>
        );
      })}
    </div>
  );
}

/** Tariff with its rule list; Save sends the whole list (it replaces the rules on the server). */
function TariffEditor({ tariff, zones, toast, onClose, onSaved }: {
  tariff: Tariff | null; zones: Zone[]; toast: ToastFn; onClose: () => void; onSaved: () => void;
}) {
  const [name, setName] = useState(tariff?.name ?? '');
  const [price, setPrice] = useState(String(tariff?.pricePerHour ?? 60));
  const [zoneId, setZoneId] = useState(tariff?.zoneId ?? '');
  const [isDefault, setIsDefault] = useState(tariff?.isDefault ?? false);
  const [step, setStep] = useState(String(tariff?.billingStepMinutes ?? 1));
  const [minimum, setMinimum] = useState(String(tariff?.minimumCharge ?? 0));
  const [rules, setRules] = useState<TariffRule[]>(tariff?.rules ?? []);
  const [busy, setBusy] = useState(false);

  const setRule = (i: number, change: Partial<TariffRule>) => setRules(rules.map((r, j) => (j === i ? { ...r, ...change } : r)));
  const move = (i: number, by: number) => {
    const next = [...rules];
    const [r] = next.splice(i, 1);
    next.splice(Math.max(0, Math.min(next.length, i + by)), 0, r);
    setRules(next);
  };

  const save = async () => {
    setBusy(true);
    const body = {
      name: name.trim(), pricePerHour: parseFloat(price.replace(',', '.')) || 0, isDefault, zoneId: zoneId || null,
      billingStepMinutes: parseInt(step, 10) || 1, minimumCharge: parseFloat(minimum.replace(',', '.')) || 0,
      rules: rules.map(r => ({ ...r, name: r.name.trim(), pricePerHour: Number(r.pricePerHour) || 0 })),
    };
    const res = tariff ? await postJson(`/api/tariffs/${tariff.id}`, body, 'PUT') : await postJson('/api/tariffs', body);
    setBusy(false);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(t.tariffs.saved, 'success');
    onSaved();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal modal-wide" onClick={e => e.stopPropagation()}>
        <div className="modal-title">💰 {tariff ? `${t.tariffs.editTitle}: ${tariff.name}` : t.tariffs.newTitle}</div>
        <div className="form-grid">
          <div className="form-group">
            <label className="form-label">{t.tariffs.name}</label>
            <input className="form-input" value={name} onChange={e => setName(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.tariffs.zone}</label>
            <select className="form-select" value={zoneId} onChange={e => setZoneId(e.target.value)}>
              <option value="">{t.tariffs.allZones}</option>
              {zones.map(z => <option key={z.id} value={z.id}>{z.name}</option>)}
            </select>
          </div>
          <div className="form-group">
            <label className="form-label">{t.tariffs.basePrice}</label>
            <input className="form-input" type="number" min="0" step="5" value={price} onChange={e => setPrice(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label" title={t.tariffs.stepHint}>{t.tariffs.step}</label>
            <input className="form-input" type="number" min="1" max="60" value={step} onChange={e => setStep(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.tariffs.minimum}</label>
            <input className="form-input" type="number" min="0" step="5" value={minimum} onChange={e => setMinimum(e.target.value)} />
          </div>
          <label className="check-row">
            <input type="checkbox" checked={isDefault} onChange={e => setIsDefault(e.target.checked)} /> {t.tariffs.isDefault}
          </label>
        </div>
        <div className="auth-sub">{t.tariffs.stepHint}</div>

        <div className="section-card-header" style={{ marginTop: 8 }}>
          <span className="form-label">{t.tariffs.rules}</span>
          <button className="btn btn-sm" type="button" disabled={rules.length >= 20}
            onClick={() => setRules([...rules, { name: '', days: [1, 2, 3, 4, 5, 6, 7], start: '08:00', end: '17:00', pricePerHour: Number(price) || 0 }])}>
            {t.tariffs.addRule}
          </button>
        </div>
        <div className="auth-sub">{t.tariffs.rulesHint}</div>
        {rules.length === 0 && <div className="user-chip">{t.tariffs.noRules}</div>}
        {rules.map((r, i) => (
          <div key={i} className="rule-row">
            <input className="form-input" placeholder={t.tariffs.ruleName} value={r.name} onChange={e => setRule(i, { name: e.target.value })} />
            <DayPicker days={r.days} onChange={days => setRule(i, { days })} />
            <span className="rule-times">
              {t.tariffs.from} <input className="form-input" value={r.start} placeholder="HH:mm" onChange={e => setRule(i, { start: e.target.value })} />
              {t.tariffs.to} <input className="form-input" value={r.end} placeholder="HH:mm" onChange={e => setRule(i, { end: e.target.value })} />
            </span>
            <input className="form-input" type="number" min="0" step="5" value={r.pricePerHour} title={t.perHour}
              onChange={e => setRule(i, { pricePerHour: e.target.value === '' ? 0 : Number(e.target.value) })} />
            <span style={{ display: 'flex', gap: 2 }}>
              <button className="btn btn-sm" type="button" disabled={i === 0} onClick={() => move(i, -1)}>↑</button>
              <button className="btn btn-sm" type="button" disabled={i === rules.length - 1} onClick={() => move(i, 1)}>↓</button>
              <button className="btn btn-sm btn-danger" type="button" onClick={() => setRules(rules.filter((_, j) => j !== i))}>✕</button>
            </span>
          </div>
        ))}

        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !name.trim()} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}

function toPackageRequest(p: Package, change: Partial<{ isActive: boolean }> = {}) {
  return {
    name: p.name, zoneId: p.zoneId, price: p.price, durationMinutes: p.durationMinutes, windowStart: p.windowStart,
    windowEnd: p.windowEnd, days: p.days, isActive: change.isActive ?? p.isActive, sortOrder: p.sortOrder,
  };
}

function PackageEditor({ pkg, zones, toast, onClose, onSaved }: {
  pkg: Package | null; zones: Zone[]; toast: ToastFn; onClose: () => void; onSaved: () => void;
}) {
  const [name, setName] = useState(pkg?.name ?? '');
  const [zoneId, setZoneId] = useState(pkg?.zoneId ?? zones[0]?.id ?? '');
  const [price, setPrice] = useState(String(pkg?.price ?? 150));
  const [duration, setDuration] = useState(pkg?.durationMinutes ? String(pkg.durationMinutes) : '180');
  const [windowStart, setWindowStart] = useState(pkg?.windowStart ?? '');
  const [windowEnd, setWindowEnd] = useState(pkg?.windowEnd ?? '');
  const [days, setDays] = useState<number[]>(pkg?.days ?? [1, 2, 3, 4, 5, 6, 7]);
  const [isActive, setIsActive] = useState(pkg?.isActive ?? true);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    const body = {
      name: name.trim(), zoneId: zoneId || null, price: parseFloat(price.replace(',', '.')) || 0,
      durationMinutes: duration.trim() ? parseInt(duration, 10) : null,
      windowStart: windowStart.trim() || null, windowEnd: windowEnd.trim() || null, days, isActive, sortOrder: pkg?.sortOrder ?? 0,
    };
    const res = pkg ? await postJson(`/api/packages/${pkg.id}`, body, 'PUT') : await postJson('/api/packages', body);
    setBusy(false);
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(t.packages.saved, 'success');
    onSaved();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-title">🎟️ {pkg ? `${t.packages.editTitle}: ${pkg.name}` : t.packages.newTitle}</div>
        <div className="form-group">
          <label className="form-label">{t.packages.name}</label>
          <input className="form-input" placeholder="напр. 3 години, Нічний 22:00–08:00" value={name} onChange={e => setName(e.target.value)} />
        </div>
        <div className="form-grid">
          <div className="form-group">
            <label className="form-label">{t.packages.zone}</label>
            <select className="form-select" value={zoneId} onChange={e => setZoneId(e.target.value)}>
              {zones.map(z => <option key={z.id} value={z.id}>{z.name}</option>)}
            </select>
          </div>
          <div className="form-group">
            <label className="form-label">{t.packages.price}</label>
            <input className="form-input" type="number" min="1" step="10" value={price} onChange={e => setPrice(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label" title={t.packages.durationHint}>{t.packages.duration}</label>
            <input className="form-input" type="number" min="1" max="1440" placeholder={t.packages.durationHint} value={duration} onChange={e => setDuration(e.target.value)} />
          </div>
          <div className="form-group">
            <label className="form-label">{t.packages.window}</label>
            <span className="rule-times">
              <input className="form-input" placeholder="22:00" value={windowStart} onChange={e => setWindowStart(e.target.value)} />
              –
              <input className="form-input" placeholder="08:00" value={windowEnd} onChange={e => setWindowEnd(e.target.value)} />
            </span>
          </div>
        </div>
        <div className="auth-sub">{t.packages.windowHint} {t.packages.durationHint}.</div>
        <div className="form-group">
          <label className="form-label">{t.packages.days}</label>
          <DayPicker days={days} onChange={setDays} />
        </div>
        <label className="check-row">
          <input type="checkbox" checked={isActive} onChange={e => setIsActive(e.target.checked)} /> {t.packages.active}
        </label>
        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !name.trim() || !zoneId} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}

function BonusSection({ settings, canEdit, toast, onSaved }: { settings: BillingSettings | null; canEdit: boolean; toast: ToastFn; onSaved: () => void }) {
  const [tiers, setTiers] = useState<BonusTier[] | null>(null);
  const shown = tiers ?? settings?.bonusTopUpRules ?? [];

  const save = async () => {
    const res = await postJson('/api/billing/settings', { bonusTopUpRules: shown }, 'PUT');
    if (!res.ok) { toast(`Помилка: ${await errorText(res)}`, 'error'); return; }
    toast(t.bonus.saved, 'success');
    setTiers(null);
    onSaved();
  };

  return (
    <div className="section-card">
      <div className="section-card-header">
        <span className="section-card-title">{t.bonus.title}</span>
        {canEdit && <button className="btn btn-sm" onClick={() => setTiers([...shown, { minAmount: 500, percent: 5 }])}>{t.bonus.addTier}</button>}
      </div>
      <div className="auth-sub">{t.bonus.hint}</div>
      {shown.length === 0 && <div className="user-chip">{t.bonus.off}</div>}
      {canEdit ? (
        <>
          {shown.map((tier, i) => (
            <div key={i} className="tier-row">
              <label className="rule-times">{t.bonus.from}
                <input className="form-input" type="number" min="0" step="50" value={tier.minAmount}
                  onChange={e => setTiers(shown.map((x, j) => (j === i ? { ...x, minAmount: Number(e.target.value) } : x)))} />
              </label>
              <label className="rule-times">{t.bonus.percent}
                <input className="form-input" type="number" min="0" max="100" step="1" value={tier.percent}
                  onChange={e => setTiers(shown.map((x, j) => (j === i ? { ...x, percent: Number(e.target.value) } : x)))} />
              </label>
              <button className="btn btn-sm btn-danger" onClick={() => setTiers(shown.filter((_, j) => j !== i))}>✕</button>
            </div>
          ))}
          {tiers && <button className="btn btn-primary btn-sm" onClick={save}>{t.save}</button>}
        </>
      ) : (
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {shown.map((tier, i) => <span key={i} className="badge badge-green">{fmt(t.bonus.tierText, { min: tier.minAmount, percent: tier.percent })}</span>)}
        </div>
      )}
    </div>
  );
}
