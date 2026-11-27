// Types of the bar, stock, shift and Z-report API (Cms.Server phase 4) used by the admin UI.

export type Product = {
  id: string; name: string; category: string; price: number; isActive: boolean; trackStock: boolean; stockQuantity: number;
  lowStockThreshold: number | null; lowStock: boolean; sortOrder: number;
};
export type SaleItem = { productId: string; name: string; category: string; unitPrice: number; quantity: number; lineTotal: number };
export type Sale = {
  id: number; createdUtc: string; kind: 'sale' | 'refund'; refundOfSaleId: number | null; refundedBySaleId: number | null; shiftId: number | null;
  userId: string | null; username: string | null; method: string; total: number; reason: string | null; actorUsername: string | null; items: SaleItem[];
};
export type StockMovement = {
  id: number; createdUtc: string; productId: string; productName: string; type: string; delta: number; quantityAfter: number;
  saleId: number | null; shiftId: number | null; reason: string | null; unitCost: number | null; actorUsername: string | null;
};
export type StockCheck = { ok: boolean; trackedProducts: number; movements: number; mismatches: unknown[] };

export type Shift = {
  id: number; status: 'open' | 'closed'; openedUtc: string; openedBy: string | null; openingFloat: number; openNote: string | null;
  closedUtc: string | null; closedBy: string | null; countedCash: number | null; expectedCash: number | null; variance: number | null;
  closeNote: string | null; auditHeadId: number | null; auditHeadHash: string | null;
};
export type CurrentShift = {
  shift: Shift | null; expectedCash: number | null; blind: boolean; previousCountedCash: number | null; previousShiftId: number | null;
  requireOpenShift: boolean;
};

export type MethodSums = { cash: number; card: number; balance: number; bonus: number; debt: number; total: number };
export type ZReport = {
  shift: Shift; generatedUtc: string; timeZone: string;
  time: { total: number; byMethod: MethodSums; rows: number };
  bar: { sales: MethodSums; refunds: MethodSums; net: number; receipts: number; refundReceipts: number;
    items: { name: string; category: string; quantity: number; amount: number }[] };
  topUps: { byMethod: MethodSums; count: number; total: number };
  balanceRefunds: { byMethod: MethodSums; count: number; total: number };
  bonus: { issued: number; spent: number; reversed: number; adjusted: number };
  debt: { recorded: number; paid: MethodSums; writtenOff: number };
  payouts: { total: number; rows: { ledgerId: number; createdUtc: string; amount: number; reason: string | null; by: string | null }[] };
  adjustments: { balancePlus: number; balanceMinus: number };
  cash: { in: number; out: number; net: number; opening: number | null; expected: number | null; counted: number | null; variance: number | null };
  card: { in: number; out: number; net: number };
  other: { rows: number; cash: number; card: number };
  byStaff: { username: string; rows: number; cashNet: number; cardNet: number; timeSales: number; barSales: number; barRefunds: number;
    topUps: number; refunds: number; payouts: number; debtRecorded: number }[];
  ledger: { rows: number; firstId: number | null; lastId: number | null; reconciled: boolean };
  /** Reservation deposits of the shift (a kept deposit is in the PC time too). */
  holds?: { depositsHeld: number; depositsReleased: number; depositsKept: number };
};

export type AuditVerify = {
  ok: boolean; rows: number; headId: number | null; headHash: string | null; firstBrokenId: number | null; problem: string | null; anchorsChecked: number;
};

/** Signed amount with a plus sign: +10.00 / -15.00 / 0.00. */
export const signedMoney = (n: number | null | undefined) => (n == null ? '—' : n > 0 ? `+${n.toFixed(2)}` : n.toFixed(2));
