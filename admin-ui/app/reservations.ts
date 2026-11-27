// Types of the reservation API (club times come from the server as strings, so the browser zone never matters).

export type ReservationStatus = 'requested' | 'confirmed' | 'checked_in' | 'cancelled' | 'declined' | 'no_show' | 'expired';
export type DepositStatus = 'none' | 'held' | 'applied' | 'returned' | 'kept';

export type Reservation = {
  id: number; status: ReservationStatus; source: 'staff' | 'public';
  deviceId: string | null; deviceHostname: string | null; zoneId: string | null; zoneName: string | null;
  assignedDeviceId: string | null; assignedHostname: string | null;
  startUtc: string; endUtc: string; date: string; startLocal: string; endLocal: string; startMinute: number; durationMinutes: number;
  userId: string | null; username: string | null; guestName: string | null; phone: string | null;
  deposit: number; depositStatus: DepositStatus; sessionId: string | null; note: string | null;
  createdByUsername: string | null; createdUtc: string; cancelReason: string | null;
  held: boolean; canCheckIn: boolean; holdFromLocal: string; noShowAtLocal: string; erased: boolean;
};

export type ZoneLane = { id: string | null; name: string; color: string; pcs: { id: string; hostname: string }[] };
export type ReservationDay = {
  date: string; timeZone: string; holdMinutes: number; graceMinutes: number; noShowKeepsDeposit: boolean;
  zones: ZoneLane[]; reservations: Reservation[]; requests: Reservation[]; nowMinute: number | null;
};
export type ReservationSettings = {
  holdMinutes: number; graceMinutes: number; noShowKeepsDeposit: boolean;
  publicBooking: boolean; eraseAfterDays: number; maxOpenPerPhone: number;
};
/** The next reservation of a PC on the PC map. */
export type PcReservation = { id: number; name: string; startUtc: string; startLocal: string; endLocal: string; held: boolean; isEvent?: boolean };

export const durationsMinutes = [30, 45, 60, 90, 120, 150, 180, 240, 300, 360, 480, 600, 720];

/** "YYYY-MM-DD" plus days. */
export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** The PC row of a reservation on the timeline (its PC, or the PC held / used for an any-PC reservation). */
export const pcOf = (r: Reservation) => r.deviceId ?? r.assignedDeviceId;

/** Shown on the timeline and in the list: the player, or the name (staff only). */
export const whoOf = (r: Reservation, erased: string) => r.username ? '@' + r.username : r.guestName ?? erased;
