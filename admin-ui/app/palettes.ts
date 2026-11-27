// Colours that are DATA, not the look of a theme: staff pick them, the server stores them, and they mean the same
// zone or game in every theme (scripts/check-tokens.mjs does not check this file).

/** Game tiles: the same palette as the server (Cms.Shared.GameTile.Palette); new entries get a colour from the name. */
export const GAME_PALETTE = ['#e11d48', '#f97316', '#eab308', '#22c55e', '#14b8a6', '#06b6d4', '#3b82f6', '#6366f1', '#a855f7', '#d946ef', '#64748b', '#84cc16'];

/** Zone colours offered in the new-zone dialog; the first one is the default. */
export const ZONE_COLORS = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#3b82f6', '#a855f7', '#06b6d4', '#ec4899'];

/** The first colour of the accent picker in Settings: a middle tone that reaches 3:1 on both surfaces of every theme. */
export const ACCENT_PICKER_START = '#e0457b';
