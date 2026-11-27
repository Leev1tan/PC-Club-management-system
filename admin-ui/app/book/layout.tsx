import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Бронювання місця · Book a seat",
  description: "Book a PC in the club: zone, date, time and duration; the staff confirm the request by phone.",
};

export default function BookLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return children;
}
