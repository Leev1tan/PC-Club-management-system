using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Shared;

namespace Cms.Server.Demo;

public static partial class DemoSeeder
{
    /// <summary>A bar product of the demo club. FinalStock: the stock ends at this number (a low-stock warning in the UI).</summary>
    private sealed record ProductSpec(string Name, string Category, decimal Price, bool Track, int? Low = null, int? FinalStock = null,
        bool Active = true, int Weight = 3);

    /// <summary>Bar of a Ukrainian computer club in 2026, prices in UAH.</summary>
    private static readonly ProductSpec[] BarCatalog =
    {
        new("Monster Energy 0,5 л", "Енергетики", 75m, true, 6, Weight: 6),
        new("Red Bull 0,25 л", "Енергетики", 70m, true, 6, FinalStock: 4, Weight: 5),
        new("Burn 0,5 л", "Енергетики", 55m, true, 6, Weight: 4),
        new("Non Stop 0,5 л", "Енергетики", 45m, true, 6, Weight: 4),
        new("Pit Bull 0,25 л", "Енергетики", 40m, true, 6, Weight: 2),
        new("Еспресо", "Кава", 35m, false, Weight: 2),
        new("Американо", "Кава", 40m, false, Weight: 3),
        new("Капучино", "Кава", 55m, false, Weight: 3),
        new("Лате", "Кава", 60m, false, Weight: 2),
        new("Lay's 120 г", "Снеки", 70m, true, 5, Weight: 3),
        new("Pringles 165 г", "Снеки", 110m, true, 5, FinalStock: 2, Weight: 2),
        new("Snickers 50 г", "Снеки", 40m, true, 8, Weight: 4),
        new("Twix 50 г", "Снеки", 40m, true, 8, Weight: 3),
        new("Арахіс солоний 100 г", "Снеки", 45m, true, 5, Weight: 2),
        new("Сухарики «Флінт» 100 г", "Снеки", 30m, true, 5, Weight: 2),
        new("Хот-дог", "Снеки", 65m, false, Weight: 2),
        new("Моршинська 0,5 л", "Вода й напої", 25m, true, 10, Weight: 4),
        new("Моршинська 1,5 л", "Вода й напої", 40m, true, 4, Active: false, Weight: 0),
        new("Coca-Cola 0,5 л", "Вода й напої", 35m, true, 8, Weight: 4),
        new("Sprite 0,5 л", "Вода й напої", 35m, true, 8, Weight: 2),
    };

    /// <summary>Opening float of every demo shift.</summary>
    public const decimal ShiftFloat = 1000m;
    /// <summary>Units of one delivery (restock) of a product.</summary>
    public const int DeliveryQuantity = 24;
    private const int ShiftHistoryDays = 18;
    private const int BarHistoryDays = 9;

    private sealed partial class Seeding
    {
        private sealed record Window(DateTimeOffset Start, DateTimeOffset? End, bool Day, StaffUserEntity Opener);
        private sealed record Line(ProductEntity Product, int Quantity);

        private readonly Dictionary<string, StaffUserEntity> _staff = new();
        private readonly List<Window> _windows = new();
        private readonly List<ProductEntity> _products = new();
        private readonly Dictionary<Guid, ProductSpec> _specs = new();
        private readonly List<(DateTimeOffset At, Func<AuditLogEntity> Make)> _audit = new();
        private readonly Dictionary<int, (decimal Variance, string Note)> _variances = new();
        private Random _bar = new();

        public List<ShiftEntity> Shifts { get; } = new();
        public List<SaleEntity> Sales { get; } = new();
        public List<(SaleEntity Sale, List<SaleItemEntity> Items)> SaleItems { get; } = new();
        public List<StockMovementEntity> Movements { get; } = new();
        public Dictionary<StockMovementEntity, SaleEntity> MovementSale { get; } = new();
        public Dictionary<LedgerEntryEntity, SaleEntity> RowSale { get; } = new();
        public Dictionary<SaleEntity, SaleEntity> RefundOf { get; } = new();

        private StaffUserEntity Cashier => _staff[StaffRoles.Cashier];
        private StaffUserEntity Manager => _staff[StaffRoles.Manager];
        private StaffUserEntity Owner => _staff[StaffRoles.Owner];

        /// <summary>The shift that owns a moment (after PlanShifts).</summary>
        public ShiftEntity? ShiftAt(DateTimeOffset at) =>
            Shifts.LastOrDefault(s => s.OpenedUtc <= at && (s.ClosedUtc == null || at < s.ClosedUtc));

        private Window? WindowAt(DateTimeOffset at) => _windows.LastOrDefault(w => w.Start <= at && (w.End == null || at < w.End));

        private DateTimeOffset LocalTime(DateTime localDate, int hour, int minute = 0) =>
            ClubTimeZone.LocalToUtc(localDate.Date.AddHours(hour).AddMinutes(minute), _zone);

        private DateTime Today => TimeZoneInfo.ConvertTime(_now, _zone).Date;

        /// <summary>Back-to-back 12 h shifts at 09:00 and 21:00 club time; the last one is open (the demo cashier's).</summary>
        private void PlanWindows()
        {
            var starts = new List<(DateTimeOffset Utc, bool Day)>();
            for (var day = Today.AddDays(-ShiftHistoryDays); day <= Today; day = day.AddDays(1))
            {
                starts.Add((LocalTime(day, 9), true));
                starts.Add((LocalTime(day, 21), false));
            }
            starts = starts.Where(s => s.Utc <= _now).ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Utc : (DateTimeOffset?)null;
                _windows.Add(new Window(starts[i].Utc, end, starts[i].Day, end == null || starts[i].Day ? Cashier : Manager));
            }
            // Yesterday's day shift closed with 15 ₴ missing; an older one with 10 ₴ too much
            var yesterday = _windows.FindIndex(w => w.Day && w.End != null && TimeZoneInfo.ConvertTime(w.Start, _zone).Date == Today.AddDays(-1));
            if (yesterday >= 0) _variances[yesterday] = (-15m, "Не вистачає 15 ₴ — перевірити чеки за вечір");
            var older = _windows.FindIndex(w => w.Day && w.End != null && TimeZoneInfo.ConvertTime(w.Start, _zone).Date == Today.AddDays(-4));
            if (older >= 0) _variances[older] = (10m, "Зайві 10 ₴ — гість не забрав решту");
        }

        private void Audit(DateTimeOffset at, StaffUserEntity? actor, string action, string targetType, Func<string?> targetId, string summary, object? details = null) =>
            Audit(at, actor, action, targetType, targetId, () => summary, details);

        /// <summary>Planned audit row; target and summary are read after the ids of shifts and receipts exist.</summary>
        private void Audit(DateTimeOffset at, StaffUserEntity? actor, string action, string targetType, Func<string?> targetId, Func<string> summary, object? details = null)
        {
            if (at > _now) return;
            _audit.Add((at, () => new AuditLogEntity
            {
                CreatedUtc = at, ActorId = actor?.Id, ActorUsername = actor?.Username, ActorRole = actor?.Role, Action = action,
                TargetType = targetType, TargetId = targetId(), Summary = summary(),
                DetailsJson = details == null ? null : System.Text.Json.JsonSerializer.Serialize(details, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }),
                Ip = actor == null ? null : "10.3.3.10"
            }));
        }

        /// <summary>Audit rows of the seed (built after the ids of shifts and receipts are known).</summary>
        public IEnumerable<AuditLogEntity> AuditEntries() => _audit.OrderBy(a => a.At).Select(a => a.Make()).ToList();

        private void SeedBarAndDesk()
        {
            _bar = new Random(_rng.Next());
            PlanWindows();

            foreach (var (spec, i) in BarCatalog.Select((x, i) => (x, i)))
            {
                var product = new ProductEntity
                {
                    Id = Guid.NewGuid(), Name = spec.Name, Category = spec.Category, Price = spec.Price, IsActive = spec.Active,
                    TrackStock = spec.Track, LowStockThreshold = spec.Track ? spec.Low : null, SortOrder = i, CreatedUtc = _now.AddDays(-40)
                };
                _products.Add(product);
                _specs[product.Id] = spec;
                _db.Products.Add(product);
            }

            var receipts = PlanReceipts();
            var stock = OpeningStock(receipts);
            var correctionAt = LocalTime(Today.AddDays(-1), 10, 20);
            var corrected = false;
            SaleEntity? refundable = null;
            var yesterdayDay = _windows.FirstOrDefault(w => w.Day && w.End != null && TimeZoneInfo.ConvertTime(w.Start, _zone).Date == Today.AddDays(-1));
            var lays = _products.Single(p => p.Name.StartsWith("Lay's"));

            foreach (var r in receipts)
            {
                if (!corrected && r.At >= correctionAt && correctionAt < _now)
                {
                    // Stock count: two packs damaged
                    var delta = -Math.Min(2, stock[lays.Id]);
                    stock[lays.Id] += delta;
                    Move(lays, StockMovementTypes.Correction, delta, correctionAt, Manager, reason: "Інвентаризація: 2 пачки пошкоджені");
                    Audit(correctionAt, Manager, AuditActions.StockCorrected, "product", () => lays.Id.ToString(),
                        $"Stock of '{lays.Name}' corrected: {delta:+0;-0;0}: Інвентаризація: 2 пачки пошкоджені", new { product = lays.Name, delta, reason = "Інвентаризація" });
                    corrected = true;
                }
                var opener = WindowAt(r.At)?.Opener ?? Cashier;
                foreach (var line in r.Lines.Where(l => l.Product.TrackStock))
                {
                    if (stock[line.Product.Id] >= line.Quantity) continue;
                    var deliveredAt = r.At.AddMinutes(-_bar.Next(5, 50));
                    stock[line.Product.Id] += DeliveryQuantity;
                    Move(line.Product, StockMovementTypes.Restock, DeliveryQuantity, deliveredAt, Manager, reason: "Поставка від дистриб'ютора", unitCost: Math.Round(line.Product.Price * 0.55m, 2));
                    Audit(deliveredAt, Manager, AuditActions.StockRestocked, "product", () => line.Product.Id.ToString(),
                        $"Restock of '{line.Product.Name}': +{DeliveryQuantity}", new { product = line.Product.Name, quantity = DeliveryQuantity });
                }
                var sale = Sell(r.At, r.Lines, r.Method, r.Player, opener);
                foreach (var line in r.Lines.Where(l => l.Product.TrackStock)) stock[line.Product.Id] -= line.Quantity;
                if (refundable == null && yesterdayDay != null && r.Method == PaymentMethods.Cash && r.At >= yesterdayDay.Start.AddHours(2) &&
                    r.At < yesterdayDay.End!.Value.AddHours(-1) && r.Lines.All(l => _specs[l.Product.Id].FinalStock == null))
                    refundable = sale;
            }

            if (refundable != null) RefundSale(refundable, refundable.CreatedUtc.AddMinutes(25), "Банка пошкоджена — повернення коштів");
            SeedPayout();
            SeedDebtor();
            SeedFreeTime();
            SeedAuditOnly();

            // The stock log in date order gives the quantity after each row and the cached stock
            foreach (var product in _products.Where(p => p.TrackStock))
            {
                var running = 0;
                foreach (var m in Movements.Where(m => m.ProductId == product.Id).OrderBy(m => m.CreatedUtc))
                {
                    running += m.Delta;
                    m.QuantityAfter = running;
                }
                product.StockQuantity = running;
            }
        }

        private List<(DateTimeOffset At, List<Line> Lines, string Method, UserEntity? Player)> PlanReceipts()
        {
            var sellable = _products.Where(p => p.IsActive).ToList();
            var bag = sellable.SelectMany(p => Enumerable.Repeat(p, _specs[p.Id].Weight)).ToList();
            var list = new List<(DateTimeOffset, List<Line>, string, UserEntity?)>();
            foreach (var w in _windows.Where(w => (w.End ?? _now) > _now.AddDays(-BarHistoryDays)))
            {
                var to = w.End ?? _now.AddMinutes(-2);
                if (to <= w.Start) continue;
                var count = w.End == null ? Math.Max(5, (int)((to - w.Start).TotalHours * 1.2)) : w.Day ? _bar.Next(8, 15) : _bar.Next(4, 9);
                for (var k = 0; k < count; k++)
                {
                    var at = w.Start + TimeSpan.FromSeconds(_bar.NextDouble() * (to - w.Start).TotalSeconds);
                    var lines = new List<Line>();
                    foreach (var product in Enumerable.Range(0, _bar.Next(1, 4)).Select(_ => bag[_bar.Next(bag.Count)]).Distinct())
                        lines.Add(new Line(product, product.Category == "Кава" || product.Name == "Хот-дог" ? 1 : _bar.Next(1, 3)));
                    var roll = _bar.NextDouble();
                    var player = roll < 0.15 ? _users[_bar.Next(_users.Count)] : null;
                    var method = player != null ? PaymentMethods.Balance : roll < 0.65 ? PaymentMethods.Cash : PaymentMethods.Card;
                    list.Add((at, lines, method, player));
                }
            }
            return list.OrderBy(r => r.Item1).ToList();
        }

        /// <summary>Opening stock of each tracked product (one row per product before the history).</summary>
        private Dictionary<Guid, int> OpeningStock(List<(DateTimeOffset At, List<Line> Lines, string Method, UserEntity? Player)> receipts)
        {
            var sold = receipts.SelectMany(r => r.Lines).GroupBy(l => l.Product.Id).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            var stock = new Dictionary<Guid, int>();
            var at = _windows[0].Start.AddHours(1);
            foreach (var product in _products.Where(p => p.TrackStock))
            {
                var spec = _specs[product.Id];
                var initial = spec.FinalStock is { } final ? (sold.TryGetValue(product.Id, out var n) ? n : 0) + final
                    : product.Category is "Енергетики" or "Вода й напої" ? 48 : 30;
                stock[product.Id] = initial;
                Move(product, StockMovementTypes.Restock, initial, at, Manager, reason: "Початковий залишок");
            }
            return stock;
        }

        private StockMovementEntity Move(ProductEntity product, string type, int delta, DateTimeOffset at, StaffUserEntity actor,
            SaleEntity? sale = null, string? reason = null, decimal? unitCost = null)
        {
            var m = new StockMovementEntity
            {
                CreatedUtc = at, ProductId = product.Id, Type = type, Delta = delta, Reason = reason, UnitCost = unitCost,
                ActorId = actor.Id, ActorUsername = actor.Username
            };
            Movements.Add(m);
            if (sale != null) MovementSale[m] = sale;
            return m;
        }

        private SaleEntity Sell(DateTimeOffset at, List<Line> lines, string method, UserEntity? player, StaffUserEntity opener)
        {
            var items = lines.Select(l => new SaleItemEntity
            {
                ProductId = l.Product.Id, Name = l.Product.Name, Category = l.Product.Category, UnitPrice = l.Product.Price,
                Quantity = l.Quantity, LineTotal = l.Product.Price * l.Quantity
            }).ToList();
            var total = items.Sum(i => i.LineTotal);
            var sale = new SaleEntity
            {
                CreatedUtc = at, Kind = SaleKinds.Sale, UserId = player?.Id, Method = method, Total = total,
                ActorId = opener.Id, ActorUsername = opener.Username
            };
            Sales.Add(sale);
            SaleItems.Add((sale, items));
            foreach (var line in lines.Where(l => l.Product.TrackStock))
                Move(line.Product, StockMovementTypes.Sale, -line.Quantity, at, opener, sale);
            var text = Pos.SaleService.ItemsText(items);
            if (player == null)
            {
                var row = new LedgerEntryEntity
                {
                    CreatedUtc = at, Type = LedgerTypes.BarSale, Method = method, Amount = total, DeskAmount = total,
                    Description = $"Бар: {text} — {total} ₴", ActorId = opener.Id, ActorUsername = opener.Username
                };
                LedgerRows.Add(row);
                RowSale[row] = sale;
            }
            else
            {
                _playerEvents.Add((at, 1, () =>
                {
                    if (_wallets[player.Id].Balance < total)
                        TopUp(player, Math.Ceiling(total / 100m) * 100m + 100m, PaymentMethods.Cash, at.AddMilliseconds(-10));
                    RowSale[Post(player, at, LedgerTypes.BarSale, PaymentMethods.Balance, total, -total, 0m, 0m, $"Бар: {text} — {total} ₴", actor: opener.Username)] = sale;
                }));
            }
            return sale;
        }

        private void RefundSale(SaleEntity sale, DateTimeOffset at, string reason)
        {
            if (at > _now) return;
            var items = SaleItems.Single(x => x.Sale == sale).Items;
            var refund = new SaleEntity
            {
                CreatedUtc = at, Kind = SaleKinds.Refund, UserId = sale.UserId, Method = sale.Method, Total = sale.Total, Reason = reason,
                ActorId = Manager.Id, ActorUsername = Manager.Username
            };
            Sales.Add(refund);
            RefundOf[refund] = sale;
            SaleItems.Add((refund, items.Select(i => new SaleItemEntity
            {
                ProductId = i.ProductId, Name = i.Name, Category = i.Category, UnitPrice = i.UnitPrice, Quantity = i.Quantity, LineTotal = i.LineTotal
            }).ToList()));
            foreach (var item in items.Where(i => _products.Single(p => p.Id == i.ProductId).TrackStock))
                Move(_products.Single(p => p.Id == item.ProductId), StockMovementTypes.Refund, item.Quantity, at, Manager, refund, reason);
            var row = new LedgerEntryEntity
            {
                CreatedUtc = at, Type = LedgerTypes.BarRefund, Method = sale.Method, Amount = sale.Total, DeskAmount = -sale.Total, Reason = reason,
                Description = $"Повернення чека ({Pos.SaleService.ItemsText(items)}) — {sale.Total} ₴: {reason}", ActorId = Manager.Id, ActorUsername = Manager.Username
            };
            LedgerRows.Add(row);
            RowSale[row] = refund;
            Audit(at, Manager, AuditActions.SaleRefunded, "sale", () => sale.Id.ToString(), () => $"Bar receipt #{sale.Id} refunded: {sale.Total} ₴ (cash): {reason}",
                new { total = sale.Total, method = sale.Method, reason, items = items.Select(i => $"{i.Name} x{i.Quantity}").ToList() });
        }

        /// <summary>Cash taken out of the drawer two days ago (supplies for the bar).</summary>
        private void SeedPayout()
        {
            var at = LocalTime(Today.AddDays(-2), 14, 35);
            if (at > _now) return;
            const string reason = "Закупівля води й снеків для бару (чек магазину)";
            LedgerRows.Add(new LedgerEntryEntity
            {
                CreatedUtc = at, Type = LedgerTypes.CashPayout, Method = PaymentMethods.Cash, Amount = 300m, DeskAmount = -300m, Reason = reason,
                Description = $"Виплата з каси 300 ₴: {reason}", ActorId = Manager.Id, ActorUsername = Manager.Username
            });
            Audit(at, Manager, AuditActions.ShiftCashPayout, "shift", () => ShiftAt(at)?.Id.ToString(), $"Cash payout 300 ₴: {reason}", new { amount = 300m, reason });
        }

        /// <summary>A player who could not pay a post-paid session yesterday; part of the debt is paid today.</summary>
        private void SeedDebtor()
        {
            var ghost = new UserEntity
            {
                Id = Guid.NewGuid(), Username = "ghost_ua", DisplayName = "Привид", PasswordHash = Passwords.Hash(DemoOptions.PlayerPassword),
                CreatedUtc = _now.AddDays(-3)
            };
            _db.Users.Add(ghost);
            _users.Add(ghost);
            _wallets[ghost.Id] = (0m, 0m);

            var pc = _pcs.First(p => p.Device.Hostname == "STD-03");
            var start = LocalTime(Today.AddDays(-1), 18, 40);
            var stop = start.AddMinutes(145).AddSeconds(20);
            if (stop > _now) return;
            var cost = TariffCalculator.QuotePlayed(new[] { (pc.Pricing, start, stop) }, pc.Pricing, _zone).Total;
            var session = AddSession(pc, start, stop, cost, false, null, null, ghost, "ended");
            session.PaymentMethod = PaymentMethods.Debt;
            var opener = WindowAt(stop)?.Opener ?? Cashier;
            _playerEvents.Add((stop, 1, () => Post(ghost, stop, LedgerTypes.SessionCharge, PaymentMethods.Debt, cost, 0m, 0m, 0m,
                $"Сесія на {pc.Device.Hostname} (145 хв) — {cost} ₴ у борг", session, actor: opener.Username, debtDelta: cost)));
            Audit(stop, opener, AuditActions.PlayerDebtRecorded, "player", () => ghost.Id.ToString(),
                $"Debt {cost} ₴ recorded: post-paid session on {pc.Device.Hostname} not paid", new { amount = cost, device = pc.Device.Hostname });

            var open = _windows[^1];
            var payAt = open.Start + (_now - open.Start) / 2;
            _playerEvents.Add((payAt, 1, () => Post(ghost, payAt, LedgerTypes.DebtPayment, PaymentMethods.Cash, 60m, 0m, 0m, 60m,
                "Сплата боргу 60 ₴ (готівкою)", actor: Cashier.Username, debtDelta: -60m)));
            Audit(payAt, Cashier, AuditActions.PlayerDebtPaid, "player", () => ghost.Id.ToString(), $"Debt paid: 60 ₴ (cash), {cost - 60m} ₴ left",
                new { amount = 60m, method = "cash", debtLeft = cost - 60m });
        }

        /// <summary>Two hours of VIP time given by the manager to a tournament winner (no money, audited).</summary>
        private void SeedFreeTime()
        {
            var pc = _pcs.First(p => p.Device.Hostname == "VIP-02");
            var start = LocalTime(Today.AddDays(-2), 19, 0);
            if (start.AddMinutes(120) > _now) return;
            var winner = _users.Single(u => u.Username == "vika_gg");
            var value = TariffCalculator.QuoteFixed(pc.Pricing, start, 120, _zone).Total;
            var session = AddSession(pc, start, start.AddMinutes(120), 0m, true, null, 120, winner, "ended");
            session.PaymentMethod = PaymentMethods.Free;
            const string reason = "Переможниця турніру CS2 2×2: 2 години VIP";
            Audit(start, Manager, AuditActions.SessionFreeTime, "session", () => session.Id.ToString(),
                $"Free time on {pc.Device.Hostname}: 120 min, worth {value} ₴: {reason}", new { pc.Device.Hostname, minutes = 120, value, reason, player = winner.Username });
        }

        /// <summary>Audit rows without money: a price change, shift handovers, an emergency PIN unlock last night.</summary>
        private void SeedAuditOnly()
        {
            var monster = _products.First();
            var priceAt = LocalTime(Today.AddDays(-5), 11, 5);
            Audit(priceAt, Owner, AuditActions.ProductPriceChanged, "product", () => monster.Id.ToString(),
                $"Price of '{monster.Name}' changed: 70.00 -> 75.00 ₴", new { before = new { monster.Name, Price = 70m }, after = new { monster.Name, monster.Price } });

            foreach (var (w, i) in _windows.Select((w, i) => (w, i)).TakeLast(4))
            {
                var window = w;
                Audit(window.Start.AddSeconds(1), window.Opener, AuditActions.ShiftOpened, "shift", () => ShiftAt(window.Start.AddSeconds(1))?.Id.ToString(),
                    $"Shift opened with a float of {ShiftFloat} ₴", new { OpeningFloat = ShiftFloat });
                if (window.End is not { } end) continue;
                Audit(end.AddSeconds(-1), window.Opener, AuditActions.ShiftClosed, "shift", () => ShiftAt(end.AddSeconds(-1))?.Id.ToString(),
                    "Shift closed (see the Z-report)", new { variance = _variances.TryGetValue(i, out var v) ? v.Variance : 0m });
            }

            var night = LocalTime(Today, 2, 14);
            if (night > _now) night = night.AddDays(-1);
            var pc = _pcs.First(p => p.Device.Hostname == "STD-05");
            Audit(night, null, AuditActions.EmergencyUnlock, "device", () => pc.Device.Id.ToString(),
                $"Emergency staff PIN unlock on {pc.Device.Hostname} at {TimeZoneInfo.ConvertTime(night, _zone):yyyy-MM-dd HH:mm} (the agent was not responding)",
                new { eventId = Guid.NewGuid().ToString("N"), pc.Device.Hostname, occurredUtc = night, lockReason = nameof(LockReason.AgentSilent), windowsUser = "player" });
        }

        /// <summary>One shift per window with the expected cash of its ledger rows; desk rows get the cashier on duty.</summary>
        private void PlanShifts()
        {
            foreach (var (w, i) in _windows.Select((w, i) => (w, i)))
            {
                var rows = LedgerRows.Where(r => r.CreatedUtc >= w.Start && (w.End == null || r.CreatedUtc < w.End)).ToList();
                foreach (var row in rows.Where(r => r.ActorUsername is null or "demo-cashier"))
                {
                    row.ActorUsername = w.Opener.Username;
                    row.ActorId = w.Opener.Id;
                }
                var shift = new ShiftEntity
                {
                    Status = w.End == null ? ShiftStatuses.Open : ShiftStatuses.Closed, OpenedUtc = w.Start,
                    OpenedById = w.Opener.Id, OpenedByUsername = w.Opener.Username, OpeningFloat = ShiftFloat
                };
                if (w.End != null)
                {
                    var expected = ShiftFloat + rows.Where(r => r.Method == PaymentMethods.Cash).Sum(r => r.DeskAmount);
                    var (variance, note) = _variances.TryGetValue(i, out var v) ? v : (0m, (string?)null);
                    shift.ClosedUtc = w.End;
                    shift.ClosedById = w.Opener.Id;
                    shift.ClosedByUsername = w.Opener.Username;
                    shift.ExpectedCash = expected;
                    shift.CountedCash = expected + variance;
                    shift.Variance = variance;
                    shift.CloseNote = note;
                }
                Shifts.Add(shift);
            }
        }
    }
}
