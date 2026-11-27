'use client';
import { useState } from 'react';
import { errorText, postJson } from './api';
import { fmt, t } from './i18n';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
export type PowerAction = 'shutdown' | 'reboot' | 'wake';
export type PowerTarget = {
  id: string; hostname: string; macAddress?: string | null; online: boolean;
  /** Player or guest of the active session (the session goes on after a shutdown or reboot). */
  sessionOf?: string | null;
};

/**
 * Confirmation of a power action (Owner, Manager, Tech). Shutdown and reboot go through the command queue
 * of the PC; Wake-on-LAN is sent by the server to the MAC address the agent reported. All audited.
 */
export function PowerDialog({ target, action, toast, onClose, onDone }: {
  target: PowerTarget; action: PowerAction; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const texts = t.power[action];

  const confirm = async () => {
    setBusy(true);
    const res = await postJson(`/api/devices/${target.id}/power`, { action });
    setBusy(false);
    if (!res.ok) { toast(`${t.common.errorPrefix}: ${await errorText(res)}`, 'error'); return; }
    const view = await res.json().catch(() => null) as { simulated?: boolean } | null;
    toast(fmt(view?.simulated ? t.power.wake.simulated : texts.done, { host: target.hostname }), 'success');
    onDone();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby="power-title" onClick={e => e.stopPropagation()}>
        <div className="modal-title" id="power-title">{fmt(texts.title, { host: target.hostname })}</div>
        <p className="auth-sub">{texts.hint}</p>
        {action !== 'wake' && target.sessionOf && (
          <div className="power-warning" role="alert">⚠ {fmt(t.power.sessionWarning, { who: target.sessionOf })}</div>
        )}
        {action === 'shutdown' && (
          <p className="auth-sub">{target.macAddress ? fmt(t.power.wakeLater, { mac: target.macAddress }) : t.power.noWakeLater}</p>
        )}
        {action === 'wake' && (
          <dl className="power-facts">
            <dt>{t.power.mac}</dt><dd className="mono">{target.macAddress ?? t.none}</dd>
          </dl>
        )}
        {action !== 'wake' && !target.online && <p className="auth-sub">{t.power.offline}</p>}
        <div className="modal-actions">
          <button className="btn" onClick={onClose} autoFocus>{t.cancel}</button>
          <button className={`btn ${action === 'wake' ? 'btn-primary' : 'btn-danger'}`} disabled={busy} onClick={confirm}>{texts.confirm}</button>
        </div>
      </div>
    </div>
  );
}
