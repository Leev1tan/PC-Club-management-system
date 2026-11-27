# Selectable design themes — design (approved 2026-11-27 12:55 Kyiv)

Status: approved by the operator in Telegram ("Так затверджую"). Goes into the club repo as
`docs/superpowers/specs/2026-11-27-themes-design.md` with the themes phase (after phase 8).

## Intent and why

Clubs differ. The current look is one strong anime-cyberpunk style (neon cyan glow, Orbitron/Rajdhani fonts).
Not every club wants it. SeNet lets a club pick the look. The operator wants several ready themes so a club
chooses its own (operator's words: "різні тематики, щоб можна було обрати який хоч. Даже як senet").

## Decisions taken by the operator

- Who chooses: the club Owner picks ONE theme for the whole club (option A). It applies to the lock screens on
  the club PCs, the public booking page `/book` and the admin panel default.
- Each staff member may switch only their own admin panel between the light and the dark variant.
- Approved plan (as sent): 4 themes Cyber (current, cleaned), Pro Dark (graphite, no neon), Light (light, business),
  Arcade (bright retro); club logo (PNG or JPEG, at most 512 KB) and an own accent colour; contrast check, 390 px
  phone, UA and EN for every theme; theme switch in the demo, demo reset returns Cyber.
- Not built: per-player themes, custom CSS uploaded by a club.
- Order: right after phase 8 (phase 8 edits the same files now).

## Approaches considered

1. Theme = a set of design tokens (picked, my call; the operator approved the plan with it).
   Admin UI: every colour, font, radius, shadow and glow becomes a CSS custom property; a theme is a
   `[data-theme="…"]` block; the page sets the attribute. Lock screen (WPF): one ResourceDictionary per theme with
   the same token names; the launcher swaps the merged dictionary at run time; the theme reaches the PC like the
   club language does now (server setting -> heartbeat -> `C:\ProgramData\ClubAgent\client-settings.json`).
2. A copy of the markup per theme: rejected, every UI change would be done 4 times.
3. Accent colour only: rejected, the anime look comes from the fonts and the neon glow, not only from the colour.

## Constraints

- Today: `admin-ui/app/globals.css` (1880 lines) has 29 tokens but about 78 hard-coded colours, plus about 42 in
  `.tsx`; fonts Orbitron, Rajdhani, JetBrains Mono via next/font (self-hosted). `Cms.Launcher/MainWindow.xaml` has
  about 70 hard-coded colours and `FontFamily="Consolas"` in place, no resource dictionaries.
- Next.js static export, no runtime requests to Google; every font must be self-hosted (next/font at build time;
  WPF: bundle font files as resources or use fonts present on Windows, for example Segoe UI, Bahnschrift, Consolas).
- No flash of the wrong theme on load (set `data-theme` before first paint: inline script reading a cached value,
  then the server value).
- Logo upload is an untrusted file: accept only PNG/JPEG by content (magic bytes), at most 512 KB, re-encode or at
  least decode-check on the server, never SVG; serve with a fixed content type and `X-Content-Type-Options: nosniff`.
  Owner only; audited without file content.
- Accent colour: a `#RRGGBB` value only, validated on the server; the UI derives hover/glow shades; refuse an accent
  that fails WCAG AA against the theme background, or auto-adjust it, and say so.
- Settings changes need Owner (theme, logo, accent); audited (`settings.theme_changed`, `settings.logo_changed`).
- Every theme: WCAG AA text contrast (4.5:1 body, 3:1 large text and UI parts), `tabular-nums` on money, timers and
  KPIs, no horizontal scroll at 390 px, UA and EN without mixed languages.
- Hermes shared clean-UI rules (one neutral surface ramp, one accent, semantic status colours only, max two font
  families, 1 px borders instead of heavy shadows, one primary button per view). Use them for Pro Dark and Light;
  Cyber keeps its neon character but loses noise; Arcade may be loud but must pass contrast.

## Goals / non-goals / anti-goals

- Goals: 4 themes that each look designed, not recoloured; owner switch takes effect on admin UI, /book and all
  lock screens within about 10 s; logo + accent; light/dark per staff member.
- Non-goals: per-player themes, custom CSS, a theme marketplace, theming the Windows installer.
- Anti-goals: a theme where a status (busy/free/debt/warning) is hard to read; any layout break between themes;
  a theme switch that needs a rebuild or a PC restart.

## Left to the builder

Exact palettes and font pairs per theme (self-hosted, licence-compatible with AGPL distribution: OFL fonts are fine),
the token names, the settings UI layout, the WPF dictionary mechanics, screenshot set for README (one per theme).
