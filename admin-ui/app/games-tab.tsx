'use client';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, errorText, getJson, postJson } from './api';
import { fmt, t } from './i18n';
import { accountPlatforms, platformName } from './accounts-tab';
import { GAME_PALETTE } from './palettes';

type ToastFn = (message: string, type?: 'success' | 'error' | 'info') => void;
type Zone = { id: string; name: string; color: string };
type Kind = 'exe' | 'steam' | 'url';
export type Game = {
  id: string; name: string; category: string; kind: Kind; exePath: string | null; arguments: string | null; workingDirectory: string | null;
  steamAppId: number | null; launchUrl: string | null; processName: string | null; allZones: boolean; zoneIds: string[];
  color: string; initials: string; sortOrder: number; isActive: boolean; updatedUtc: string; plays: number; accountPlatform?: string | null;
};

const URL_SCHEMES = 'steam, com.epicgames.launcher, uplay, battlenet, origin2, eadesktop, goggalaxy, riotclient, roblox-player';

/** A generated tile: initials on the entry colour (the lock screen draws the same). */
export function GameTile({ game, size = 40 }: { game: Pick<Game, 'initials' | 'color' | 'name'>; size?: number }) {
  return (
    <span className="game-tile" style={{ background: game.color, width: size, height: size, fontSize: Math.round(size * 0.36) }} title={game.name} aria-hidden="true">
      {game.initials}
    </span>
  );
}

function launchText(g: Game): string {
  if (g.kind === 'steam') return `Steam ${g.steamAppId}`;
  if (g.kind === 'url') return g.launchUrl ?? '';
  return [g.exePath, g.arguments].filter(Boolean).join(' ');
}

/**
 * Game catalog (Owner, Manager): what the lock screens show during a session, per zone. A game with
 * play history cannot be deleted; it is taken off the catalog (inactive) instead.
 */
export function GamesTab({ zones, toast }: { zones: Zone[]; toast: ToastFn }) {
  const [games, setGames] = useState<Game[]>([]);
  const [dialog, setDialog] = useState<{ game: Game | null } | null>(null);

  const load = useCallback(async () => setGames((await getJson<Game[]>('/api/games')) ?? []), []);
  useEffect(() => { load(); }, [load]);
  const categories = useMemo(() => [...new Set(games.map(g => g.category))], [games]);
  const zoneNames = (g: Game) => g.allZones ? t.games.allZones : g.zoneIds.map(id => zones.find(z => z.id === id)?.name ?? '?').join(', ');

  const remove = async (g: Game) => {
    if (!window.confirm(fmt(t.games.confirmDelete, { name: g.name }))) return;
    const res = await api(`/api/games/${g.id}`, { method: 'DELETE' });
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    toast(t.games.deleted, 'info');
    load();
  };

  return (
    <>
      <div className="page-header">
        <span className="page-title">{t.games.title}</span>
        <button className="btn btn-primary btn-sm" onClick={() => setDialog({ game: null })}>{t.games.add}</button>
      </div>
      <p className="auth-sub">{t.games.hint}</p>

      {games.length === 0 ? (
        <div className="empty-state">
          <div className="empty-state-icon">🎮</div>
          <div className="empty-state-title">{t.games.empty}</div>
        </div>
      ) : (
        <div className="table-wrapper">
          <table className="data-table games-table">
            <thead><tr>
              <th aria-label={t.games.col.tile}></th><th>{t.games.col.name}</th><th>{t.games.col.launch}</th><th>{t.games.col.zones}</th>
              <th>{t.games.col.order}</th><th>{t.games.col.plays}</th><th>{t.games.col.active}</th><th></th>
            </tr></thead>
            <tbody>
              {games.map(g => (
                <tr key={g.id} className={g.isActive ? '' : 'row-muted'}>
                  <td><GameTile game={g} /></td>
                  <td style={{ fontWeight: 600, color: 'var(--text-primary)' }}>{g.name}<div className="user-chip">{g.category}{g.accountPlatform ? ` · 🔑 ${platformName(g.accountPlatform)}` : ''}</div></td>
                  <td><span className="badge badge-blue">{t.games.kinds[g.kind]}</span><div className="game-launch" title={launchText(g)}>{launchText(g)}</div></td>
                  <td>{zoneNames(g)}</td>
                  <td>{g.sortOrder}</td>
                  <td>{g.plays}</td>
                  <td>{g.isActive ? t.yes : <span className="badge badge-red">{t.games.inactive}</span>}</td>
                  <td>
                    <div style={{ display: 'flex', gap: 4 }}>
                      <button className="btn btn-sm" onClick={() => setDialog({ game: g })}>{t.edit}</button>
                      {g.plays === 0 && <button className="btn btn-danger btn-sm" onClick={() => remove(g)}>{t.delete}</button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {dialog && <GameDialog game={dialog.game} zones={zones} categories={categories} toast={toast}
        onClose={() => setDialog(null)} onDone={() => { toast(t.games.saved, 'success'); setDialog(null); load(); }} />}
    </>
  );
}

function GameDialog({ game, zones, categories, toast, onClose, onDone }: {
  game: Game | null; zones: Zone[]; categories: string[]; toast: ToastFn; onClose: () => void; onDone: () => void;
}) {
  const [name, setName] = useState(game?.name ?? '');
  const [category, setCategory] = useState(game?.category ?? categories[0] ?? '');
  const [kind, setKind] = useState<Kind>(game?.kind ?? 'steam');
  const [exePath, setExePath] = useState(game?.exePath ?? '');
  const [args, setArgs] = useState(game?.arguments ?? '');
  const [workdir, setWorkdir] = useState(game?.workingDirectory ?? '');
  const [steamAppId, setSteamAppId] = useState(game?.steamAppId != null ? String(game.steamAppId) : '');
  const [launchUrl, setLaunchUrl] = useState(game?.launchUrl ?? '');
  const [processName, setProcessName] = useState(game?.processName ?? '');
  const [allZones, setAllZones] = useState(game?.allZones ?? true);
  const [zoneIds, setZoneIds] = useState<string[]>(game?.zoneIds ?? []);
  const [color, setColor] = useState<string | null>(game?.color ?? null);
  const [sortOrder, setSortOrder] = useState(String(game?.sortOrder ?? 0));
  const [isActive, setIsActive] = useState(game?.isActive ?? true);
  const [accountPlatform, setAccountPlatform] = useState(game?.accountPlatform ?? '');
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    const body = {
      name, category, kind, exePath, arguments: args, workingDirectory: workdir,
      steamAppId: steamAppId === '' ? null : parseInt(steamAppId, 10), launchUrl, processName,
      allZones, zoneIds: allZones ? [] : zoneIds, color, sortOrder: parseInt(sortOrder, 10) || 0, isActive,
      accountPlatform: accountPlatform || null,
    };
    const res = await postJson(game ? `/api/games/${game.id}` : '/api/games', body, game ? 'PUT' : 'POST');
    setBusy(false);
    if (!res.ok) { toast(await errorText(res), 'error'); return; }
    onDone();
  };

  const toggleZone = (id: string) => setZoneIds(z => z.includes(id) ? z.filter(x => x !== id) : [...z, id]);
  const ready = name.trim() && category.trim() && (allZones || zoneIds.length > 0) &&
    (kind === 'exe' ? exePath.trim() : kind === 'steam' ? parseInt(steamAppId, 10) > 0 : launchUrl.trim());

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal modal-wide" onClick={e => e.stopPropagation()}>
        <div className="modal-title">{game ? t.games.editTitle : t.games.newTitle}</div>
        <div className="form-grid">
          <div className="form-group"><label className="form-label" htmlFor="game-name">{t.games.name}</label>
            <input id="game-name" className="form-input" value={name} onChange={e => setName(e.target.value)} maxLength={100} autoFocus /></div>
          <div className="form-group"><label className="form-label" htmlFor="game-category">{t.games.category}</label>
            <input id="game-category" className="form-input" list="game-categories" value={category} onChange={e => setCategory(e.target.value)} maxLength={50} />
            <datalist id="game-categories">{categories.map(c => <option key={c} value={c} />)}</datalist></div>
        </div>
        <div className="form-group"><label className="form-label" htmlFor="game-kind">{t.games.kind}</label>
          <select id="game-kind" className="form-select" value={kind} onChange={e => setKind(e.target.value as Kind)}>
            {(['exe', 'steam', 'url'] as const).map(k => <option key={k} value={k}>{t.games.kinds[k]}</option>)}
          </select></div>
        {kind === 'exe' && <>
          <div className="form-group"><label className="form-label" htmlFor="game-exe">{t.games.exePath}</label>
            <input id="game-exe" className="form-input mono" value={exePath} onChange={e => setExePath(e.target.value)} placeholder="C:\Games\Game\game.exe" />
            <div className="form-hint">{t.games.exePathHint}</div></div>
          <div className="form-grid">
            <div className="form-group"><label className="form-label" htmlFor="game-args">{t.games.arguments}</label>
              <input id="game-args" className="form-input mono" value={args} onChange={e => setArgs(e.target.value)} maxLength={500} /></div>
            <div className="form-group"><label className="form-label" htmlFor="game-dir">{t.games.workingDirectory}</label>
              <input id="game-dir" className="form-input mono" value={workdir} onChange={e => setWorkdir(e.target.value)} /></div>
          </div>
        </>}
        {kind === 'steam' && (
          <div className="form-group"><label className="form-label" htmlFor="game-steam">{t.games.steamAppId}</label>
            <input id="game-steam" className="form-input mono" type="number" min="1" value={steamAppId} onChange={e => setSteamAppId(e.target.value)} />
            <div className="form-hint">{t.games.steamHint}</div></div>
        )}
        {kind === 'url' && (
          <div className="form-group"><label className="form-label" htmlFor="game-url">{t.games.launchUrl}</label>
            <input id="game-url" className="form-input mono" value={launchUrl} onChange={e => setLaunchUrl(e.target.value)} maxLength={500} placeholder="com.epicgames.launcher://apps/…?action=launch" />
            <div className="form-hint">{fmt(t.games.urlHint, { schemes: URL_SCHEMES })}</div></div>
        )}
        <div className="form-group"><label className="form-label" htmlFor="game-process">{t.games.processName}</label>
          <input id="game-process" className="form-input mono" value={processName} onChange={e => setProcessName(e.target.value)} maxLength={100} />
          <div className="form-hint">{t.games.processHint}</div></div>

        <div className="form-group"><span className="form-label">{t.games.zones}</span>
          <label className="check-row"><input type="checkbox" checked={allZones} onChange={e => setAllZones(e.target.checked)} /> {t.games.allZones}</label>
          {!allZones && <div className="game-zones">
            {zones.map(z => (
              <label key={z.id} className="check-row"><input type="checkbox" checked={zoneIds.includes(z.id)} onChange={() => toggleZone(z.id)} />
                <span className="zone-dot" style={{ background: z.color }} /> {z.name}</label>
            ))}
          </div>}
        </div>

        <div className="form-grid">
          <div className="form-group"><span className="form-label">{t.games.color}</span>
            <div className="game-palette" role="radiogroup" aria-label={t.games.color}>
              {GAME_PALETTE.map(c => (
                <button key={c} type="button" role="radio" aria-checked={color === c} aria-label={c} className={`game-swatch ${color === c ? 'active' : ''}`}
                  style={{ background: c }} onClick={() => setColor(c)} />
              ))}
            </div>
            {!game && color === null && <div className="form-hint">{t.games.colorAuto}</div>}
          </div>
          <div className="form-group"><label className="form-label" htmlFor="game-order">{t.games.sortOrder}</label>
            <input id="game-order" className="form-input" type="number" value={sortOrder} onChange={e => setSortOrder(e.target.value)} /></div>
        </div>
        <div className="form-group">
          <label className="form-label">{t.accounts.gamePlatform}</label>
          <select className="form-select" value={accountPlatform} onChange={e => setAccountPlatform(e.target.value)}>
            <option value="">{t.accounts.gamePlatformNone}</option>
            {accountPlatforms.map(p => <option key={p} value={p}>{platformName(p)}</option>)}
          </select>
          <div className="form-hint">{t.accounts.gamePlatformHint}</div>
        </div>
        <label className="check-row"><input type="checkbox" checked={isActive} onChange={e => setIsActive(e.target.checked)} /> {t.games.active}</label>

        <div className="modal-actions">
          <button className="btn" onClick={onClose}>{t.cancel}</button>
          <button className="btn btn-primary" disabled={busy || !ready} onClick={save}>{t.save}</button>
        </div>
      </div>
    </div>
  );
}
