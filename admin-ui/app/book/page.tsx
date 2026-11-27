'use client';
import { useCallback, useEffect, useState } from 'react';
import { LangSwitch } from '../auth';
import { errorText, getJson, postJson } from '../api';
import { fmt, lang, locale, t } from '../i18n';
import { ClubThemeProvider, useClubTheme } from '../theme';

// The public booking page (no login). It shows only the free capacity of a zone per start time, never other bookings.
type Zone = { id: string; name: string; color: string; pcs: number; pricePerHourNow: number | null };
type Info = {
  enabled: boolean; demo: boolean; timeZone?: string; today?: string; maxDaysAhead: number; durations: number[];
  eraseAfterDays: number; currencySymbol?: string; zones: Zone[];
};
type Availability = { date: string; duration: number; zones: { id: string; slots: { time: string; free: number }[] }[] };

function addDays(date: string, days: number) {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
const weekday = (date: string) => t.days[(new Date(`${date}T12:00:00Z`).getUTCDay() + 6) % 7];
const durationText = (m: number) => `${(m / 60).toLocaleString(locale)} ${t.hoursShort}`;

// The club theme in its default mode (a visitor has no light/dark switch)
export default function BookPage() {
  return <ClubThemeProvider staff={false}><BookContent /></ClubThemeProvider>;
}

function BookContent() {
  const { logoUrl } = useClubTheme();
  const [info, setInfo] = useState<Info | null | undefined>(undefined);
  const [zone, setZone] = useState('');
  const [date, setDate] = useState('');
  const [duration, setDuration] = useState(120);
  const [avail, setAvail] = useState<Availability | null>(null);
  const [time, setTime] = useState('');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [website, setWebsite] = useState(''); // honeypot: hidden from people
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [sent, setSent] = useState(false);

  // The static page is built in Ukrainian; the browser may show English: no text before the first effect (no hydration mismatch)
  const [mounted, setMounted] = useState(false);
  useEffect(() => { document.documentElement.lang = lang; setMounted(true); }, []);
  useEffect(() => {
    getJson<Info>('/api/public/booking').then(i => {
      setInfo(i);
      if (i?.enabled) {
        setZone(i.zones[0]?.id ?? '');
        setDate(i.today ?? '');
        if (!i.durations.includes(120)) setDuration(i.durations[0] ?? 60);
      }
    });
  }, []);

  const load = useCallback(async () => {
    if (!date || !duration) return;
    setAvail(await getJson<Availability>(`/api/public/booking/availability?date=${date}&duration=${duration}`));
  }, [date, duration]);
  useEffect(() => { setTime(''); load(); }, [load]);

  const slots = avail?.zones.find(z => z.id === zone)?.slots ?? [];
  const chosen = info?.zones.find(z => z.id === zone);

  const send = async () => {
    setBusy(true);
    setError('');
    const res = await postJson('/api/public/booking', { zoneId: zone, date, time, durationMinutes: duration, name, phone, website });
    setBusy(false);
    if (res.ok) { setSent(true); return; }
    setError(res.status === 404 ? t.book.off : await errorText(res));
    load();
  };

  if (!mounted) return <div className="book-page" aria-busy="true" />;
  return (
    <div className="book-page">
      <header className="book-header">
        <div className="book-brand">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          {logoUrl && <img className="club-logo book-logo" src={logoUrl} alt={t.theme.logoAlt} />}
          <h1 className="book-title">{t.book.title}</h1>
        </div>
        <LangSwitch />
      </header>
      {info?.demo && <div className="demo-banner book-banner" role="note">{t.book.demoBanner}</div>}

      {info === undefined ? <div className="book-card book-muted" aria-busy="true">{t.loading}</div>
        : !info?.enabled ? <div className="book-card">{t.book.off}</div>
        : sent ? (
          <div className="book-card book-done" role="status">
            <div className="book-done-icon">✓</div>
            <div className="book-step-title">{t.book.sentTitle}</div>
            <p>{fmt(t.book.sentText, { zone: chosen?.name ?? '', date, time, duration: durationText(duration) })}</p>
            <button className="btn" onClick={() => { setSent(false); setTime(''); setName(''); setPhone(''); load(); }}>{t.book.again}</button>
          </div>
        ) : (
          <form className="book-card" onSubmit={e => { e.preventDefault(); if (!busy) send(); }}>
            <section>
              <h2 className="book-step-title">1 · {t.book.zone}</h2>
              <div className="book-chips">
                {info.zones.map(z => (
                  <button type="button" key={z.id} className={`book-chip ${zone === z.id ? 'active' : ''}`} style={{ borderColor: zone === z.id ? z.color : undefined }}
                    aria-pressed={zone === z.id} onClick={() => { setZone(z.id); setTime(''); }}>
                    <span className="zone-dot" style={{ background: z.color }} /> {z.name}
                    <span className="book-chip-sub">{fmt(t.book.pcs, { n: z.pcs })}{z.pricePerHourNow != null ? ` · ${fmt(t.book.priceNow, { price: z.pricePerHourNow.toFixed(0), currency: info.currencySymbol ?? '' })}` : ''}</span>
                  </button>
                ))}
              </div>
            </section>
            <section>
              <h2 className="book-step-title">2 · {t.book.date}</h2>
              <div className="book-chips book-scroll">
                {Array.from({ length: info.maxDaysAhead }, (_, i) => addDays(info.today!, i)).map((d, i) => (
                  <button type="button" key={d} className={`book-chip book-day ${date === d ? 'active' : ''}`} aria-pressed={date === d} onClick={() => setDate(d)}>
                    <span>{i === 0 ? t.book.today : i === 1 ? t.book.tomorrow : weekday(d)}</span>
                    <span className="book-chip-sub">{d.slice(8, 10)}.{d.slice(5, 7)}</span>
                  </button>
                ))}
              </div>
            </section>
            <section>
              <h2 className="book-step-title">3 · {t.book.duration}</h2>
              <div className="book-chips">
                {info.durations.map(m => (
                  <button type="button" key={m} className={`book-chip ${duration === m ? 'active' : ''}`} aria-pressed={duration === m} onClick={() => setDuration(m)}>{durationText(m)}</button>
                ))}
              </div>
            </section>
            <section>
              <h2 className="book-step-title">4 · {t.book.start}</h2>
              <div className="book-hint">{fmt(t.book.clubTime, { zone: info.timeZone ?? '' })}</div>
              {avail === null ? <div className="book-muted">{t.loading}</div> : slots.length === 0 ? <div className="book-muted">{t.book.noSlots}</div> : (
                <div className="book-slots">
                  {slots.map(s => (
                    <button type="button" key={s.time} disabled={s.free <= 0} className={`book-slot ${time === s.time ? 'active' : ''}`} aria-pressed={time === s.time}
                      onClick={() => setTime(s.time)}>
                      <strong>{s.time}</strong>
                      <span>{s.free > 0 ? fmt(t.book.free, { n: s.free }) : t.book.full}</span>
                    </button>
                  ))}
                </div>
              )}
            </section>
            <section>
              <h2 className="book-step-title">5 · {t.book.contact}</h2>
              <label className="form-label" htmlFor="book-name">{t.book.name}</label>
              <input id="book-name" className="form-input" autoComplete="given-name" maxLength={60} required value={name} onChange={e => setName(e.target.value)} />
              <label className="form-label" htmlFor="book-phone" style={{ marginTop: 10 }}>{t.book.phone}</label>
              <input id="book-phone" className="form-input" type="tel" inputMode="tel" autoComplete="tel" maxLength={24} required placeholder="+380 __ ___ __ __"
                value={phone} onChange={e => setPhone(e.target.value)} />
              <div className="book-hp" aria-hidden="true">
                <label htmlFor="book-website">Website</label>
                <input id="book-website" name="website" tabIndex={-1} autoComplete="off" value={website} onChange={e => setWebsite(e.target.value)} />
              </div>
            </section>
            <p className="book-privacy">{fmt(t.book.privacy, { days: info.eraseAfterDays })}</p>
            {error && <div className="auth-error" role="alert">{error}</div>}
            <button type="submit" className="btn btn-primary book-submit" disabled={busy || !zone || !time || name.trim().length < 2 || phone.trim().length < 9}>
              {time ? fmt(t.book.send, { time, date: `${date.slice(8, 10)}.${date.slice(5, 7)}` }) : t.book.chooseTime}
            </button>
            <p className="book-hint">{t.book.confirmHint}</p>
          </form>
        )}
    </div>
  );
}
