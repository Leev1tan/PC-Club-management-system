// UI strings of the billing screens (tariffs, packages, sessions, player money), Ukrainian.
// Phase 6 adds an English dictionary with the same keys and a language switch; until then `t` is `uk`.
// Server errors carry a stable `code` (see Cms.Server/Billing/BillingResult.cs); errors.<code> is the text.

export const uk = {
  currency: '₴',
  perHour: '₴/год',
  minutesShort: 'хв',
  hoursShort: 'год',
  guest: 'Гість',
  none: '—',
  cancel: 'Скасувати',
  save: 'Зберегти',
  close: 'Закрити',
  delete: 'Видалити',
  edit: 'Змінити',
  add: 'Додати',
  more: 'Ще',
  loading: 'Завантаження...',
  yes: 'Так',

  days: ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Нд'],
  allDays: 'Щодня',

  pay: {
    method: 'Оплата',
    balance: 'З балансу',
    cash: 'Готівка',
    card: 'Картка',
    none: '—',
    useBonus: 'Спершу списати бонуси (лише за час на ПК)',
    guestAtDesk: 'Гість платить на касі: готівкою або карткою',
  },

  tariffs: {
    title: '💰 Тарифи',
    add: '+ Додати тариф',
    empty: 'Створіть тариф, щоб почати виставляти рахунки',
    name: 'Назва',
    zone: 'Зона',
    allZones: 'Всі зони',
    base: 'Базова ціна',
    basePrice: 'Базова ціна за годину (коли не діє жодне правило)',
    nowPrice: 'Зараз',
    rules: 'Правила часу',
    rulesHint: 'Час клубу. Діє перше правило списку, що підходить; кінець ≤ початку означає «через північ» (22:00–08:00). Дні — день, коли вікно починається.',
    noRules: 'Без правил: діє базова ціна',
    addRule: '+ Правило',
    ruleName: 'Назва правила',
    from: 'з',
    to: 'до',
    step: 'Округлення пост-оплати, хв',
    stepShort: 'Округл., хв',
    stepHint: '1 = похвилинно; 15 = кожні розпочаті 15 хв оплачуються повністю (перші 10 с — безкоштовно)',
    minimum: 'Мінімальна оплата, ₴',
    minimumShort: 'Мін. оплата',
    isDefault: 'За замовчуванням',
    editTitle: 'Тариф',
    newTitle: 'Новий тариф',
    confirmDelete: 'Видалити тариф «{name}»?',
    saved: 'Тариф збережено ✓',
    deleted: 'Тариф видалено',
    readOnly: 'Змінювати тарифи можуть лише власник і менеджер',
  },

  packages: {
    title: '🎟️ Пакети',
    add: '+ Додати пакет',
    empty: 'Немає пакетів. Пакет — фіксована ціна за 3 год, 5 год, ранок або ніч у зоні.',
    name: 'Назва',
    zone: 'Зона',
    price: 'Ціна, ₴',
    duration: 'Тривалість, хв',
    durationShort: 'Тривалість',
    durationHint: 'Порожньо = до кінця вікна (нічний пакет)',
    window: 'Вікно продажу',
    windowHint: 'Порожньо = будь-коли. Пакет закінчується не пізніше кінця вікна (напр. нічний — о 08:00).',
    days: 'Дні',
    active: 'У продажу',
    takeOff: 'Зняти',
    putOn: 'У продаж',
    inactive: 'Знято з продажу',
    availableNow: 'Можна зараз',
    notNow: 'Не зараз',
    until: 'до {time}',
    newTitle: 'Новий пакет',
    editTitle: 'Пакет',
    saved: 'Пакет збережено ✓',
    deleted: 'Пакет видалено',
    confirmDelete: 'Видалити пакет «{name}»? Історія сесій залишиться.',
    anyTime: 'будь-коли',
  },

  bonus: {
    title: '🎁 Кешбек на поповнення',
    hint: 'Бонуси нараховуються на поповнення готівкою або карткою. Бонуси оплачують лише час на ПК (ніколи бар) і не повертаються грошима. Повернення коштів забирає кешбек за повернену суму.',
    from: 'Від, ₴',
    percent: '%',
    addTier: '+ Рівень',
    off: 'Кешбек вимкнено',
    saved: 'Кешбек збережено ✓',
    preview: 'Кешбек: +{bonus} ₴ бонусами',
    tierText: 'від {min} ₴ — {percent}%',
    timeZone: 'Часовий пояс клубу: {zone}',
  },

  session: {
    startTitle: '🎮 Нова сесія — {host}',
    modeTariff: 'Тариф',
    modePackage: 'Пакет',
    tariff: 'Тариф',
    duration: 'Тривалість',
    open: 'Відкрита',
    openHint: 'Відкрита сесія: оплата в кінці, за кожен відрізок часу за його ціною.',
    player: 'Гравець (необов\'язково)',
    cost: 'Вартість',
    quoteSlices: 'Розбивка за правилами',
    noPackages: 'Для зони цього ПК немає пакетів у продажу',
    endsAt: 'Закінчиться о {time}',
    cutoff: 'кінець вікна о {time}',
    start: 'Розпочати',
    started: 'Сесію розпочато ✓',
    pause: '⏸️ Пауза',
    resume: '▶️ Продовжити',
    paused: 'Пауза',
    pausedToast: 'Сесію поставлено на паузу: час зупинено, ПК заблоковано',
    resumedToast: 'Сесію продовжено ✓',
    transfer: '🔀 Перенести',
    end: 'Завершити',
    extend30: '➕ Продовжити +30 хв',
    extended: 'Сесію продовжено на {minutes} хв ✓',
    ended: 'Сесію завершено ✓',
    endedDue: 'Сесію завершено. До сплати на касі: {amount} ₴',
    endTitle: 'Завершити сесію — {host}',
    endPrepaid: 'Час оплачено наперед; невикористаний час не повертається.',
    endOpenCost: 'Вартість зараз: {amount} ₴',
    endOpenPlayer: 'Спершу списується з бонусів і балансу гравця; решта — на касі.',
    deskMethod: 'Решта на касі',
    remaining: 'Залишилось',
    current: 'Зараз',
    packageBadge: 'Пакет',
  },

  transfer: {
    title: '🔀 Перенести сесію з {host}',
    target: 'Вільний ПК',
    noFree: 'Немає вільних ПК онлайн',
    sameZone: 'та сама зона — без доплати',
    surcharge: 'Доплата: {amount} ₴',
    noSurcharge: 'Без доплати',
    basisTariff: 'різниця ціни тарифів за залишок часу ({minutes} хв)',
    basisPackage: 'різниця ціни пакетів за залишок часу ({minutes} хв), новий пакет: {name}',
    basisSegments: 'відкрита сесія: далі оплата за тарифом нової зони ({tariff})',
    basisNone: 'та сама ціна',
    noRefund: 'Перехід у дешевшу зону різницю не повертає.',
    confirm: 'Перенести',
    done: 'Сесію перенесено на {host} ✓',
  },

  player: {
    card: 'Картка',
    cardTitle: '👤 @{username}',
    balance: 'Баланс',
    bonus: 'Бонуси',
    bonusHint: 'лише за час на ПК',
    history: 'Історія операцій',
    noHistory: 'Операцій ще немає',
    topUp: '💳 Поповнити',
    refund: '↩️ Повернення',
    adjust: '± Коригування',
    topUpTitle: 'Поповнення',
    refundTitle: 'Повернення коштів',
    refundHint: 'Лише з реального балансу (бонуси не повертаються). Причина записується в журнал.',
    adjustTitle: 'Коригування',
    adjustHint: 'Додатна або від\'ємна сума. Причина обов\'язкова і записується в журнал дій.',
    target: 'Що коригуємо',
    targetBalance: 'Баланс',
    targetBonus: 'Бонуси',
    amount: 'Сума, ₴',
    reason: 'Причина (обов\'язково)',
    topUpDone: 'Баланс поповнено на {amount} ₴ ✓',
    bonusAdded: ' (+{bonus} ₴ бонусів)',
    refundDone: 'Повернено {amount} ₴ ✓',
    adjustDone: 'Скориговано ✓',
    reconciled: 'Баланс збігається з журналом ✓',
    notReconciled: 'Баланс НЕ збігається з журналом!',
    managerOnly: 'Повернення та коригування — лише для менеджера й власника',
    col: { time: 'Час', type: 'Операція', amount: 'Сума', balance: 'Баланс', bonus: 'Бонус', method: 'Оплата', description: 'Опис', who: 'Хто' },
  },

  ledgerTypes: {
    topup: 'Поповнення',
    refund: 'Повернення',
    adjustment: 'Коригування',
    session_charge: 'Оплата часу',
    package_purchase: 'Пакет',
    bonus_accrual: 'Кешбек',
    bonus_reversal: 'Скасування кешбеку',
    opening_balance: 'Початковий залишок',
    legacy: 'Стара операція',
  } as Record<string, string>,

  audit: {
    packages: 'Пакети / Packages',
    billing: 'Кешбек / Cashback',
  },

  errors: {
    device_not_found: 'ПК не знайдено',
    device_not_approved: 'ПК не схвалено',
    device_busy: 'На цьому ПК вже є активна сесія',
    same_device: 'Сесія вже на цьому ПК',
    session_not_found: 'Сесію не знайдено',
    session_ended: 'Сесію вже завершено',
    session_not_active: 'Сесія не активна',
    session_paused: 'Сесія на паузі — спершу продовжте її',
    session_not_paused: 'Сесія не на паузі',
    session_expired: 'Час сесії вже минув',
    session_changed: 'Сесію щойно змінив хтось інший. Оновіть і спробуйте ще раз',
    open_session_no_end: 'Відкрита сесія не має часу закінчення',
    past_cutoff: 'Пакет закінчується о {0}; сесія не може тривати довше',
    bad_duration: 'Тривалість від 1 до 1440 хв',
    bad_minutes: 'Хвилини від 1 до 1440',
    no_tariff: 'Немає тарифу. Спершу створіть тариф',
    tariff_not_found: 'Тариф не знайдено',
    tariff_other_zone: 'Цей тариф належить іншій зоні',
    user_not_found: 'Гравця не знайдено',
    insufficient_balance: 'Недостатньо коштів: потрібно {0} ₴, на балансі {1} ₴, бонусів {2} ₴',
    balance_too_low: 'Баланс замалий для цієї операції',
    bad_payment_method: 'Спосіб оплати: баланс, готівка або картка',
    guest_needs_desk: 'Гість платить на касі: готівкою або карткою',
    package_not_found: 'Пакет не знайдено',
    package_inactive: 'Пакет знято з продажу',
    package_invalid: 'Пакет без тривалості й без вікна',
    package_other_zone: 'Пакет для іншої зони',
    package_outside_window: 'Цей пакет продається лише з {0} до {1}',
    package_not_today: 'Сьогодні цей пакет не продається',
    package_window_ends: 'Вікно пакета вже закінчується',
    package_no_match_in_zone: 'У зоні {0} немає такого пакета — переносьте в межах зони',
    package_in_use: 'Пакет використовує активна сесія. Зніміть його з продажу',
    bad_amount: 'Сума має бути додатною (до 100000)',
    reason_required: 'Потрібна причина (мін. 3 символи)',
    bad_target: 'Коригувати можна баланс або бонуси',
    bad_tariff: 'Перевірте поля тарифу',
    bad_package: 'Перевірте поля пакета',
    bad_bonus_rules: 'Перевірте рівні кешбеку',
  } as Record<string, string>,
};

export const t = uk;

/** Text of a server error code with its arguments ({0}, {1} ...), or null for an unknown code. */
export function errorMessage(code: string, args?: string[] | null): string | null {
  const text = t.errors[code];
  if (!text) return null;
  return text.replace(/\{(\d+)\}/g, (_, i) => args?.[Number(i)] ?? '');
}

/** Replaces {name} placeholders. */
export function fmt(text: string, values: Record<string, string | number>): string {
  return text.replace(/\{(\w+)\}/g, (_, key) => (key in values ? String(values[key]) : `{${key}}`));
}

export const money = (n: number | null | undefined) => (n ?? 0).toFixed(2);

export function methodLabel(method: string | null | undefined): string {
  if (!method) return t.none;
  return method.split('+').map(m => (t.pay as Record<string, string>)[m] ?? m).join(' + ');
}

export function ledgerTypeLabel(type: string): string {
  return t.ledgerTypes[type] ?? type;
}

/** ISO weekdays (1 = Monday) as text; consecutive days become ranges: [1,2,3,4,5,7] -> "Пн–Пт, Нд". */
export function daysLabel(days: number[]): string {
  if (days.length === 7) return t.allDays;
  const sorted = [...days].sort((a, b) => a - b);
  const parts: string[] = [];
  for (let i = 0; i < sorted.length; i++) {
    let j = i;
    while (j + 1 < sorted.length && sorted[j + 1] === sorted[j] + 1) j++;
    parts.push(j - i >= 2 ? `${t.days[sorted[i] - 1]}–${t.days[sorted[j] - 1]}` : sorted.slice(i, j + 1).map(d => t.days[d - 1]).join(', '));
    i = j;
  }
  return parts.join(', ');
}
