import type { Metadata } from "next";
import { Exo_2, IBM_Plex_Mono, IBM_Plex_Sans, Inter, JetBrains_Mono, Orbitron, Press_Start_2P, Rubik } from "next/font/google";
import { themeBootScript } from "./theme-script";
import "./themes.css";
import "./globals.css";

// next/font downloads the fonts at build time and serves them from this app, so the browser never contacts Google at
// runtime (offline club LAN, public demo). Every family is under the SIL Open Font License and has Latin and Cyrillic
// letters, except Orbitron, which only draws the Latin wordmark "PC Club" of the Cyber theme. Only the fonts of the
// default theme are preloaded; the others load when a theme uses them (app/themes.css).
const exo2 = Exo_2({ variable: "--font-exo2", subsets: ["latin", "cyrillic"], display: "swap" });
const jetbrains = JetBrains_Mono({ variable: "--font-jetbrains", subsets: ["latin", "cyrillic"], display: "swap" });
const orbitron = Orbitron({ variable: "--font-orbitron", subsets: ["latin"], display: "swap", preload: false });
const inter = Inter({ variable: "--font-inter", subsets: ["latin", "cyrillic"], display: "swap", preload: false });
const plexSans = IBM_Plex_Sans({ variable: "--font-plex-sans", subsets: ["latin", "cyrillic"], weight: ["400", "500", "600", "700"], display: "swap", preload: false });
const plexMono = IBM_Plex_Mono({ variable: "--font-plex-mono", subsets: ["latin", "cyrillic"], weight: ["400", "500", "600"], display: "swap", preload: false });
const pressStart = Press_Start_2P({ variable: "--font-press-start", subsets: ["latin", "cyrillic"], weight: "400", display: "swap", preload: false });
const rubik = Rubik({ variable: "--font-rubik", subsets: ["latin", "cyrillic"], display: "swap", preload: false });

const fontVariables = [exo2, jetbrains, orbitron, inter, plexSans, plexMono, pressStart, rubik].map(f => f.variable).join(" ");

export const metadata: Metadata = {
  title: "Club Management System",
  description: "Admin dashboard for PC club device management, sessions, and monitoring",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  // data-theme and data-mode are set by the <head> script before the first paint (React must not undo them)
  return (
    <html lang="uk" className={fontVariables} suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeBootScript }} />
      </head>
      <body className="antialiased">
        {children}
      </body>
    </html>
  );
}
