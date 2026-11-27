// Types of the billing API (tariffs with rules, packages, ledger, transfer) used by the admin UI.

export type TariffRule = { name: string; days: number[]; start: string; end: string; pricePerHour: number };
export type Tariff = {
  id: string; name: string; pricePerHour: number; isDefault: boolean; zoneId: string | null; zoneName: string | null;
  billingStepMinutes: number; minimumCharge: number; rules: TariffRule[]; priceNow: number; ruleNow: string | null;
};
export type Package = {
  id: string; name: string; zoneId: string | null; zoneName: string | null; price: number; durationMinutes: number | null;
  windowStart: string | null; windowEnd: string | null; days: number[]; isActive: boolean; sortOrder: number;
  availableNow: boolean; endsAtIfStartedNow: string | null; minutesIfStartedNow: number | null;
  unavailableCode: string | null; unavailableReason: string | null;
};
export type BonusTier = { minAmount: number; percent: number };
export type BillingSettings = { timeZone: string; clubTimeNow: string; currency: string; bonusTopUpRules: BonusTier[] };
export type QuoteSlice = { startUtc: string; endUtc: string; pricePerHour: number; ruleName: string | null; minutes: number; amount: number };
export type Quote = { tariffId: string; tariffName: string; total: number; minutes: number; minimumApplied: boolean; slices: QuoteSlice[] };
export type TransferQuote = {
  sessionId: string; targetDeviceId: string; targetHostname: string; targetZoneId: string | null;
  targetTariffId: string | null; targetTariffName: string | null; targetPackageId: string | null; targetPackageName: string | null;
  surcharge: number; remainingMinutes: number; basis: 'none' | 'tariff' | 'package' | 'segments';
};
export type LedgerEntry = {
  id: number; createdUtc: string; userId: string | null; type: string; method: string; amount: number;
  balanceDelta: number; bonusDelta: number; deskAmount: number; balanceAfter: number | null; bonusAfter: number | null;
  sessionId: string | null; packageId: string | null; deviceId: string | null; description: string | null; reason: string | null;
  actorUsername: string | null; shiftId?: number | null; saleId?: number | null; debtDelta?: number; debtAfter?: number | null;
};
export type LedgerCheck = {
  balance: number; ledgerBalance: number; bonus: number; ledgerBonus: number; ok: boolean; brokenRowId: number | null; debt?: number; ledgerDebt?: number;
};

/** Cashback of a top-up by the tiers (same rule as the server: highest reached tier). */
export function cashbackFor(tiers: BonusTier[], amount: number): number {
  const tier = tiers.filter(x => x.percent > 0 && amount >= x.minAmount).sort((a, b) => b.minAmount - a.minAmount)[0];
  return tier && amount > 0 ? Math.round(amount * tier.percent) / 100 : 0;
}

export { clock as clockTime } from './i18n';
