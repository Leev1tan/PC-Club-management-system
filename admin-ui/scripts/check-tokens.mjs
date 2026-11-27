// Design-token check of the admin UI: every colour lives in app/themes.css (one block per theme and mode).
// Fails (exit 1) and lists file:line for a literal colour (#hex, rgb(), rgba(), hsl(), hsla()) in any other
// app/**/*.css, *.tsx or *.ts file. Allowed everywhere: transparent, currentColor, var(--…) and
// color-mix(in srgb, var(--…) …, transparent). app/palettes.ts holds colours that are data (zone and game colours
// that staff pick and the server stores), not the look of a theme, so it is not checked.
//   node scripts/check-tokens.mjs            (from admin-ui/)
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const app = join(root, 'app');
const allowed = new Set(['app/themes.css', 'app/palettes.ts']);
const literal = /(?<![\w&$])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![\w-])|\b(?:rgba?|hsla?)\(/g;

function* files(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) yield* files(path);
    else if (/\.(css|tsx|ts)$/.test(name)) yield path;
  }
}

const problems = [];
let checked = 0;
for (const path of files(app)) {
  const rel = relative(root, path).split(sep).join('/');
  if (allowed.has(rel)) continue;
  checked++;
  readFileSync(path, 'utf8').split('\n').forEach((line, i) => {
    const found = line.match(literal);
    if (found) problems.push(`${rel}:${i + 1}: ${found.join(' ')}  | ${line.trim().slice(0, 120)}`);
  });
}
if (problems.length) {
  console.log(problems.join('\n'));
  console.log(`TOKENS_FAIL: ${problems.length} line(s) with a literal colour outside app/themes.css (${checked} files checked)`);
  process.exit(1);
}
console.log(`TOKENS_OK: no literal colour outside app/themes.css (${checked} files checked)`);
