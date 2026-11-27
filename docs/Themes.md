# Club Themes

Project authorship: created and maintained by Volodymyr Shabat.

This page tells how the club themes work. A theme sets the colours, the fonts and the shapes of the admin UI, of the
public booking page `/book` and of the lock screens on the club PCs. The club Owner selects one theme for the whole
club. The layout and the texts are the same in every theme.

## The four themes

| Theme | Id | Look | Fonts | Use it when |
| --- | --- | --- | --- | --- |
| Cyber (default) | `cyber` | Neon cyan and magenta on near black, cut corners, a soft glow | Exo 2, JetBrains Mono (Orbitron only for the Latin "PC Club" mark) | The club has a gaming, anime or cyberpunk style |
| Pro Dark | `pro-dark` | Graphite surfaces, one blue accent, 1 px borders, no glow | Inter, JetBrains Mono | The staff work long shifts and want a calm screen |
| Light | `light` | Warm paper, deep teal accent, clear borders, rounder corners | IBM Plex Sans, IBM Plex Mono | The club is bright or the owner wants an office look |
| Arcade | `arcade` | Night purple, coin yellow and pink, pixel headings, square corners, hard shadows | Press Start 2P (headings only), Rubik, JetBrains Mono | The club has a retro or family style |

Every theme has a dark mode and a light mode. The default mode is dark for Cyber, Pro Dark and Arcade, and light for
Light.

## Who changes what

| Person | Can change | Where |
| --- | --- | --- |
| Owner | The theme, the accent colour and the logo of the club | Admin UI → Settings → "Club theme" |
| Every staff member | Only the own admin panel: dark or light mode | The sun or moon button in the top bar. The choice stays in this browser only. |
| Visitor of `/book` | Nothing | `/book` always shows the default mode of the club theme |
| Player on a club PC | Nothing | The lock screen shows the default mode of the club theme |

In the public demo the demo owner can change the theme and the accent. The logo upload is off in the demo, because every
visitor can sign in there. A demo reset sets Cyber again, without an accent and without a logo.

Each change is in the audit log: `settings.theme_changed` (the old and the new theme and accent),
`settings.logo_changed` and `settings.logo_removed`.

## How a change reaches the screens

1. The Owner saves the theme. The server keeps it in the club settings `club.theme` and `club.accent`.
2. Every open admin UI and `/book` page asks `GET /api/public/theme` every 10 seconds and when the window gets the
   focus. The page changes without a reload, usually within 10 seconds.
3. Every approved PC gets the theme, the accent and the SHA-256 of the logo with the next heartbeat (every 5 seconds).
   The agent downloads a new logo first, then writes `client-settings.json`. The lock screen reads the file every
   2 seconds and changes the theme without a restart.
4. If the server is off, the PCs keep the last theme and logo. A PC that never reached the server shows Cyber.

A page keeps a copy of the last theme in the browser. A script in the page head sets the theme from this copy before the
first paint, so a page load does not show another theme first.

## The accent colour

The Owner can replace the accent of the theme with an own colour, written as `#RRGGBB`. The accent colours buttons,
borders, selected items and important numbers.

- The accent must have a contrast of at least 3:1 with the panel background of BOTH modes of the theme. Every staff
  member can switch the mode, so one mode is not sufficient. The server refuses a colour that does not pass with the code
  `accent_low_contrast` and says the mode and the ratio. It does not change the colour itself.
- Middle tones pass in every theme, for example `#E0457B`. A very bright colour (`#00F0FF`) or a very dark colour does not
  pass.
- Text needs more contrast than a border (4.5:1). For text in the accent colour, the pages and the lock screen move the
  shade towards the text colour until it reaches 4.5:1. Text on an accent fill is black or white, whichever reads better.
- "Theme colour" in Settings removes the own accent.

## The logo

| Rule | Value |
| --- | --- |
| Formats | PNG or JPEG only. SVG, GIF, WebP and all other files are refused. |
| How the type is found | From the bytes, never from the file name or the declared type. The server also checks the structure: a PNG must be a correct chain of chunks from IHDR to IEND, with nothing after it; a JPEG must have a frame header and end with its end marker. |
| Size | At most 512 KB |
| Pixels | At most 1024 × 1024 (read from the PNG or JPEG header) |
| Served as | `GET /api/public/theme/logo` with the found type, `X-Content-Type-Options: nosniff` and a 5-minute cache |
| Audit | The type, the size, the pixels and the first 12 hex characters of the SHA-256. Never the bytes. |

On the PCs the agent writes the logo to `%ProgramData%\ClubAgent\club-logo.img` (HTTP 200 only, at most 512 KB, the
SHA-256 must be the hash of the heartbeat, a temporary file and a move). The lock screen shows the logo only when the
bytes have this hash and Windows can decode them. In all other cases it shows the "PC CLUB" text. If the club removes the
logo, the agent deletes the file.

## Fonts and licences

All fonts are self-hosted. The browser never asks Google: next/font downloads the fonts when the admin UI is built, and
the club server serves them. The lock screen has the fonts inside `Cms.Launcher.exe`. Every font has the Latin letters
and the Cyrillic letters of Ukrainian.

| Family | Licence | Lock screen file | Note |
| --- | --- | --- | --- |
| Exo 2 | SIL OFL 1.1 | `Exo2-Regular.ttf`, `Exo2-Bold.ttf` | Has no ₴ sign: money on the lock screen uses the mono font |
| JetBrains Mono | SIL OFL 1.1 | `JetBrainsMono-Regular.ttf`, `-Bold.ttf` | |
| Inter | SIL OFL 1.1 | `Inter-Regular.ttf`, `Inter-Bold.ttf` | |
| IBM Plex Sans, IBM Plex Mono | SIL OFL 1.1 (Reserved Font Name "Plex") | `IBMPlexSans-*.ttf`, `IBMPlexMono-*.ttf` | Unchanged official files |
| Press Start 2P | SIL OFL 1.1 (Reserved Font Name "Press Start 2P") | `PressStart2P-Regular.ttf` | Unchanged official file; headings only |
| Rubik | SIL OFL 1.1 | `Rubik-Regular.ttf`, `Rubik-Bold.ttf` | Static instances of the variable font (fontTools) |
| Orbitron | SIL OFL 1.1 | (admin UI only) | Latin only: only the "PC Club" mark of Cyber |

The licence of each family is in `Cms.Launcher/Fonts/<Family>-OFL.txt`, and the release zip of the launcher has it in
`launcher\Fonts\`. `Cms.Launcher/Fonts/SOURCES.txt` tells where each file comes from.

## Readability rules

- Every text colour has a contrast of at least 4.5:1 on every surface of its theme and mode, also on a soft fill of
  itself (badges, banners, selected chips). Icons and borders that carry meaning have at least 3:1.
- Status colours (free, busy, paused, offline, debt, warning) are the same tokens in every theme. An own accent never
  changes a status colour.
- Money, timers, balances and figures use tabular digits.
- No horizontal page scroll at 390 px. Ukrainian and English texts do not mix.

## For developers

- `admin-ui/app/themes.css` has one token block per theme and mode (`[data-theme][data-mode]`) and is the only file with
  literal colours. `node scripts/check-tokens.mjs` (in `admin-ui`) fails on a literal colour anywhere else. Zone and
  game colours are data and live in `admin-ui/app/palettes.ts`.
- `admin-ui/app/theme.tsx` is the provider (`useClubTheme()`), `admin-ui/app/theme-script.ts` the script for the page
  head. The browser keeps `ccms.theme` (the copy) and `ccms.mode` (the staff mode).
- `Cms.Shared/ClubThemes.cs` has the theme ids, the default modes, the panel surfaces and the contrast formula.
  `tests/Cms.Client.Tests/ThemeTokensParityTests.cs` checks that every block defines the same tokens, that `--bg-panel`
  equals the server surface and that the text colours pass.
- `Cms.Launcher/Themes/*.xaml` hold the default-mode colours of each theme with the same values
  (`LauncherThemeTests`). `ThemeManager` swaps the dictionary; the window uses `{DynamicResource}` keys only.
- A new theme needs: a dark and a light block in `themes.css`, the id and the two surfaces in `ClubThemes`, a XAML
  dictionary, the UA and EN names in `i18n.ts`, and a run of the parity tests and of the browser contrast check.
