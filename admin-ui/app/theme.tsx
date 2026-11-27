'use client';
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { API } from './api';
import { t } from './i18n';

// The club theme (Owner setting, GET /api/public/theme) on this page: <html data-theme data-mode> select a token block of
// app/themes.css. The provider asks the server on start, every 10 s and when the window gets the focus, so an Owner change
// reaches every open page without a reload. Staff may switch only their own admin panel between dark and light (ccms.mode);
// the public booking page always shows the default mode. A copy of the last answer (ccms.theme) lets the <head> script
// (app/theme-script.ts) set the theme before the first paint.

export const THEMES = ['cyber', 'pro-dark', 'light', 'arcade'] as const;
export type ThemeId = (typeof THEMES)[number];
export type Mode = 'dark' | 'light';
export const DEFAULT_MODE: Record<ThemeId, Mode> = { cyber: 'dark', 'pro-dark': 'dark', light: 'light', arcade: 'dark' };
export type ThemeView = { theme: string; accent: string | null; logoSha256: string | null };

const THEME_KEY = 'ccms.theme';
const MODE_KEY = 'ccms.mode';
const ACCENT_VARS = ['--accent', '--accent-contrast', '--accent-text'] as const;

export const normalizeTheme = (t: unknown): ThemeId => ((THEMES as readonly string[]).includes(t as string) ? (t as ThemeId) : 'cyber');
export const isHex = (s: unknown): s is string => typeof s === 'string' && /^#[0-9A-Fa-f]{6}$/.test(s);
const clean = (v: Partial<ThemeView> | null | undefined): ThemeView => ({
  theme: normalizeTheme(v?.theme),
  accent: isHex(v?.accent) ? v!.accent!.toUpperCase() : null,
  logoSha256: typeof v?.logoSha256 === 'string' && /^[0-9a-f]{64}$/.test(v.logoSha256) ? v.logoSha256 : null,
});

// ── WCAG 2.x contrast (the same formula as Cms.Shared.ColorContrast) ──
const channel = (c: number) => { const s = c / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
const channels = (hex: string) => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
const luminance = (hex: string) => { const [r, g, b] = channels(hex).map(channel); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
export const contrast = (a: string, b: string) => { const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const mix = (a: string, b: string, w: number) =>
  '#' + channels(a).map((c, i) => Math.round(c * (1 - w) + channels(b)[i] * w).toString(16).padStart(2, '0')).join('').toUpperCase();

/**
 * The shades of an owner accent in the current mode: the text colour on accent fills (black or white, whichever reads
 * better), and the accent for text, moved towards the text colour until it reaches 4.5:1 on every surface. The server
 * accepted the accent at 3:1 against both surfaces (a colour for borders, fills and icons); text needs more.
 */
function accentVars(accent: string): Record<string, string> {
  const cs = getComputedStyle(document.documentElement);
  const surfaces = ['--bg-abyss', '--bg-dark', '--bg-panel', '--bg-card', '--bg-elevated'].map(v => cs.getPropertyValue(v).trim()).filter(isHex);
  const text = cs.getPropertyValue('--text-primary').trim();
  // Black or white for text on accent fills (--ink and --paper of app/themes.css)
  const ink = cs.getPropertyValue('--ink').trim(), paper = cs.getPropertyValue('--paper').trim();
  const onAccent = isHex(ink) && isHex(paper) && contrast(paper, accent) > contrast(ink, accent) ? paper : ink;
  // Text in the accent colour stands on the surfaces and on soft fills of the accent (selected chips, buttons: up to 20 %)
  const worst = (c: string) => Math.min(...surfaces.flatMap(s => [contrast(c, s), contrast(c, mix(s, c, 0.2))]));
  let forText = accent.toUpperCase();
  for (let i = 1; i <= 50 && isHex(text) && surfaces.length && worst(forText) < 4.5; i++)
    forText = mix(accent, text, i / 50);
  return { '--accent': accent.toUpperCase(), '--accent-contrast': onAccent, '--accent-text': forText };
}

function readCache(): ThemeView & { vars?: Record<string, Record<string, string>> } {
  try {
    const raw = JSON.parse(localStorage.getItem(THEME_KEY) || 'null');
    return { ...clean(raw), vars: raw && typeof raw.vars === 'object' ? raw.vars : undefined };
  } catch {
    return clean(null);
  }
}

function readStaffMode(): Mode | null {
  try {
    const m = localStorage.getItem(MODE_KEY);
    return m === 'dark' || m === 'light' ? m : null;
  } catch {
    return null;
  }
}

/** Sets the attributes and the accent on <html> and keeps a copy for the next page load. */
function apply(view: ThemeView, mode: Mode) {
  const d = document.documentElement;
  const theme = normalizeTheme(view.theme);
  if (d.dataset.theme !== theme) d.dataset.theme = theme;
  if (d.dataset.mode !== mode) d.dataset.mode = mode;
  ACCENT_VARS.forEach(k => d.style.removeProperty(k));
  const vars = view.accent ? accentVars(view.accent) : {};
  Object.entries(vars).forEach(([k, v]) => d.style.setProperty(k, v));
  try {
    const old = readCache();
    const keep = old.accent === view.accent && old.theme === theme ? old.vars ?? {} : {};
    localStorage.setItem(THEME_KEY, JSON.stringify({ ...view, theme, vars: { ...keep, [mode]: vars } }));
  } catch { /* private mode: no copy, the server answer still applies */ }
}

type ClubTheme = {
  theme: ThemeId; mode: Mode; accent: string | null; logoUrl: string | null; logoSha256: string | null;
  /** The staff member's own mode (admin UI only); null = the default mode of the club theme. */
  setStaffMode: (m: Mode | null) => void;
  /** Asks the server again (after the Owner saved a change). */
  refresh: () => Promise<void>;
};

const Ctx = createContext<ClubTheme>({
  theme: 'cyber', mode: 'dark', accent: null, logoUrl: null, logoSha256: null, setStaffMode: () => undefined, refresh: async () => undefined,
});

export function ClubThemeProvider({ staff, children }: { staff: boolean; children: React.ReactNode }) {
  const [view, setView] = useState<ThemeView>(clean(null));
  const [staffMode, setStaffModeState] = useState<Mode | null>(null);
  const [ready, setReady] = useState(false);

  // The copy first (the <head> script already applied it), then the server
  useEffect(() => {
    setView(clean(readCache()));
    setStaffModeState(staff ? readStaffMode() : null);
    setReady(true);
  }, [staff]);

  const theme = normalizeTheme(view.theme);
  const mode: Mode = staffMode ?? DEFAULT_MODE[theme];
  useEffect(() => { if (ready) apply(view, mode); }, [ready, view, mode]);

  const refresh = useCallback(async () => {
    try {
      const res = await fetch(`${API}/api/public/theme`, { cache: 'no-store' });
      if (!res.ok) return; // 429 or the server is down: keep the last theme
      const next = clean(await res.json());
      setView(prev => (prev.theme === next.theme && prev.accent === next.accent && prev.logoSha256 === next.logoSha256 ? prev : next));
    } catch { /* offline: keep the last theme */ }
  }, []);

  useEffect(() => {
    refresh();
    const id = window.setInterval(refresh, 10_000);
    const onFocus = () => { refresh(); };
    window.addEventListener('focus', onFocus);
    return () => { window.clearInterval(id); window.removeEventListener('focus', onFocus); };
  }, [refresh]);

  const setStaffMode = useCallback((m: Mode | null) => {
    if (!staff) return;
    try { if (m) localStorage.setItem(MODE_KEY, m); else localStorage.removeItem(MODE_KEY); } catch { /* this page load only */ }
    setStaffModeState(m);
  }, [staff]);

  const value = useMemo<ClubTheme>(() => ({
    theme, mode, accent: view.accent, logoSha256: view.logoSha256,
    logoUrl: view.logoSha256 ? `${API}/api/public/theme/logo?v=${view.logoSha256.slice(0, 12)}` : null,
    setStaffMode, refresh,
  }), [theme, mode, view.accent, view.logoSha256, setStaffMode, refresh]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const useClubTheme = () => useContext(Ctx);

/** The club logo when the Owner uploaded one, else the default icon. The image is checked by the server (PNG or JPEG). */
export function ClubLogo({ alt }: { alt: string }) {
  const { logoUrl } = useClubTheme();
  const [broken, setBroken] = useState<string | null>(null);
  if (!logoUrl || broken === logoUrl) return <span aria-hidden="true">🎮</span>;
  // eslint-disable-next-line @next/next/no-img-element
  return <img className="club-logo" src={logoUrl} alt={alt} onError={() => setBroken(logoUrl)} />;
}

/** Top bar: this staff member's own dark or light variant of the club theme (saved in this browser only). */
export function ModeToggle() {
  const { mode, setStaffMode } = useClubTheme();
  const next: Mode = mode === 'dark' ? 'light' : 'dark';
  const label = next === 'light' ? t.theme.toLight : t.theme.toDark;
  return (
    <button type="button" className="btn btn-sm mode-toggle" aria-label={label} title={label} onClick={() => setStaffMode(next)}>
      <span aria-hidden="true">{mode === 'dark' ? '☀️' : '🌙'}</span>
    </button>
  );
}
