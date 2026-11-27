using System.Globalization;
using System.Text;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Shifts;

/// <summary>
/// CSV for spreadsheets and accounting: UTF-8 with a BOM (Excel then reads Cyrillic text), CRLF line
/// ends, quoted fields where needed. Two dialects:
///   semicolon (default) - ';' between fields and a decimal comma (Excel with Ukrainian regional settings);
///   comma               - ',' between fields and a decimal point (RFC 4180, English Excel, scripts).
/// Text that starts with = + - @ gets a leading apostrophe, so a spreadsheet never runs it as a formula.
/// </summary>
public sealed class CsvWriter
{
    private readonly StringBuilder _text = new();
    private readonly char _separator;
    private readonly bool _decimalComma;

    public CsvWriter(string? dialect)
    {
        var comma = string.Equals(dialect?.Trim(), "comma", StringComparison.OrdinalIgnoreCase);
        _separator = comma ? ',' : ';';
        _decimalComma = !comma;
    }

    public CsvWriter Row(params object?[] cells)
    {
        _text.Append(string.Join(_separator, cells.Select(Cell))).Append("\r\n");
        return this;
    }

    public CsvWriter Blank() => Row();

    private string Cell(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case decimal d:
                var number = d.ToString("0.00", CultureInfo.InvariantCulture);
                return _decimalComma ? number.Replace('.', ',') : number;
            case int or long:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case bool b:
                return b ? "1" : "0";
        }
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny(new[] { _separator, '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>UTF-8 bytes with the byte order mark.</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_text.ToString())).ToArray();
}

/// <summary>Texts of the CSV files in Ukrainian (default) and English.</summary>
public sealed class CsvTexts
{
    private readonly bool _en;

    public CsvTexts(string? lang) => _en = string.Equals(lang?.Trim(), "en", StringComparison.OrdinalIgnoreCase);

    public string T(string uk, string en) => _en ? en : uk;

    public string Method(string? method) => method switch
    {
        PaymentMethods.Cash => T("Готівка", "Cash"),
        PaymentMethods.Card => T("Картка", "Card"),
        PaymentMethods.Balance => T("Баланс", "Balance"),
        PaymentMethods.Debt => T("Борг", "Debt"),
        PaymentMethods.None or null => "",
        _ => method
    };

    public string Type(string type) => type switch
    {
        LedgerTypes.TopUp => T("Поповнення", "Top-up"),
        LedgerTypes.Refund => T("Повернення з балансу", "Balance refund"),
        LedgerTypes.Adjustment => T("Коригування", "Correction"),
        LedgerTypes.SessionCharge => T("Оплата часу", "PC time"),
        LedgerTypes.PackagePurchase => T("Пакет", "Package"),
        LedgerTypes.BonusAccrual => T("Кешбек", "Cashback"),
        LedgerTypes.BonusReversal => T("Скасування кешбеку", "Cashback reversal"),
        LedgerTypes.OpeningBalance => T("Початковий залишок", "Opening balance"),
        LedgerTypes.CashPayout => T("Виплата з каси", "Cash payout"),
        LedgerTypes.BarSale => T("Бар: продаж", "Bar sale"),
        LedgerTypes.BarRefund => T("Бар: повернення", "Bar refund"),
        LedgerTypes.DebtPayment => T("Сплата боргу", "Debt payment"),
        LedgerTypes.DebtWriteOff => T("Списання боргу", "Debt write-off"),
        LedgerTypes.Legacy => T("Стара операція", "Legacy row"),
        _ => type
    };
}

public static class CsvExport
{
    public const string ContentType = "text/csv; charset=utf-8";

    private static string Local(ClubClock clock, DateTimeOffset utc) => clock.ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string? Local(ClubClock clock, DateTimeOffset? utc) => utc.HasValue ? Local(clock, utc.Value) : null;

    /// <summary>The Z-report as "section; item; amount" rows (amounts in ₴, times in club time).</summary>
    public static byte[] ZReport(ZReport z, ClubClock clock, string? lang, string? dialect)
    {
        var t = new CsvTexts(lang);
        var csv = new CsvWriter(dialect);
        var s = z.Shift;
        csv.Row(t.T("Розділ", "Section"), t.T("Показник", "Item"), t.T("Сума, ₴", "Amount, UAH"), t.T("Кількість", "Count"));
        string Shift() => t.T("Зміна", "Shift");
        csv.Row(Shift(), t.T("Номер", "Number"), null, s.Id);
        csv.Row(Shift(), t.T("Стан", "Status"), s.Status == ShiftStatuses.Closed ? t.T("закрита", "closed") : t.T("відкрита", "open"));
        csv.Row(Shift(), t.T("Відкрито", "Opened"), Local(clock, s.OpenedUtc) + " " + s.OpenedBy);
        csv.Row(Shift(), t.T("Закрито", "Closed"), s.ClosedUtc.HasValue ? Local(clock, s.ClosedUtc) + " " + s.ClosedBy : "");
        csv.Row(Shift(), t.T("Часовий пояс", "Time zone"), z.TimeZone);

        void Methods(string section, string item, MethodSums m, int? count = null, bool bonusAndDebt = false)
        {
            csv.Row(section, item, m.Total, count);
            csv.Row(section, item + " — " + t.Method(PaymentMethods.Cash), m.Cash);
            csv.Row(section, item + " — " + t.Method(PaymentMethods.Card), m.Card);
            csv.Row(section, item + " — " + t.Method(PaymentMethods.Balance), m.Balance);
            if (!bonusAndDebt) return;
            csv.Row(section, item + " — " + t.T("Бонуси", "Bonus"), m.Bonus);
            csv.Row(section, item + " — " + t.Method(PaymentMethods.Debt), m.Debt);
        }

        Methods(t.T("Час на ПК", "PC time"), t.T("Виручка за час", "Time revenue"), z.Time.ByMethod, z.Time.Rows, bonusAndDebt: true);
        var bar = t.T("Бар", "Bar");
        Methods(bar, t.T("Продажі", "Sales"), z.Bar.Sales, z.Bar.Receipts);
        Methods(bar, t.T("Повернення", "Refunds"), z.Bar.Refunds, z.Bar.RefundReceipts);
        csv.Row(bar, t.T("Виручка бару (нетто)", "Bar revenue (net)"), z.Bar.Net);
        foreach (var item in z.Bar.Items) csv.Row(bar, $"{item.Name} ({item.Category})", item.Amount, item.Quantity);
        Methods(t.T("Поповнення", "Top-ups"), t.T("Поповнення балансу", "Balance top-ups"), z.TopUps.ByMethod, z.TopUps.Count);
        Methods(t.T("Повернення", "Refunds"), t.T("Повернення з балансу", "Balance refunds"), z.BalanceRefunds.ByMethod, z.BalanceRefunds.Count);
        var bonus = t.T("Бонуси", "Bonus");
        csv.Row(bonus, t.T("Нараховано (кешбек)", "Issued (cashback)"), z.Bonus.Issued);
        csv.Row(bonus, t.T("Витрачено на час", "Spent on PC time"), z.Bonus.Spent);
        csv.Row(bonus, t.T("Скасовано", "Reversed"), z.Bonus.Reversed);
        csv.Row(bonus, t.T("Коригування", "Corrections"), z.Bonus.Adjusted);
        var debt = t.T("Борги", "Debt");
        csv.Row(debt, t.T("Записано в борг", "Recorded"), z.Debt.Recorded);
        Methods(debt, t.T("Сплачено", "Paid"), z.Debt.Paid);
        csv.Row(debt, t.T("Списано", "Written off"), z.Debt.WrittenOff);
        var payouts = t.T("Виплати з каси", "Cash payouts");
        csv.Row(payouts, t.T("Разом", "Total"), z.Payouts.Total, z.Payouts.Rows.Count);
        foreach (var p in z.Payouts.Rows) csv.Row(payouts, $"{Local(clock, p.CreatedUtc)} {p.By}: {p.Reason}", p.Amount);
        csv.Row(t.T("Коригування балансу", "Balance corrections"), "+", z.Adjustments.BalancePlus);
        csv.Row(t.T("Коригування балансу", "Balance corrections"), "-", z.Adjustments.BalanceMinus);
        var cash = t.T("Готівка в касі", "Cash in the drawer");
        csv.Row(cash, t.T("Розмінна сума", "Opening float"), z.Cash.Opening);
        csv.Row(cash, t.T("Надійшло", "In"), z.Cash.In);
        csv.Row(cash, t.T("Видано", "Out"), z.Cash.Out);
        csv.Row(cash, t.T("Очікується", "Expected"), z.Cash.Expected);
        csv.Row(cash, t.T("Пораховано", "Counted"), z.Cash.Counted);
        csv.Row(cash, t.T("Розбіжність", "Variance"), z.Cash.Variance);
        var card = t.T("Картка (термінал)", "Card (terminal)");
        csv.Row(card, t.T("Надійшло", "In"), z.Card.In);
        csv.Row(card, t.T("Повернено", "Out"), z.Card.Out);
        csv.Row(card, t.T("Разом", "Net"), z.Card.Net);
        if (z.Other.Rows > 0) csv.Row(t.T("Інші операції", "Other rows"), t.T("Готівка / картка", "Cash / card"), z.Other.Cash + z.Other.Card, z.Other.Rows);
        var staff = t.T("Персонал", "Staff");
        foreach (var p in z.ByStaff)
        {
            csv.Row(staff, $"{p.Username}: {t.T("готівка", "cash")}", p.CashNet, p.Rows);
            csv.Row(staff, $"{p.Username}: {t.T("картка", "card")}", p.CardNet);
            csv.Row(staff, $"{p.Username}: {t.T("час", "PC time")}", p.TimeSales);
            csv.Row(staff, $"{p.Username}: {t.T("бар (продажі)", "bar sales")}", p.BarSales);
            csv.Row(staff, $"{p.Username}: {t.T("поповнення", "top-ups")}", p.TopUps);
            csv.Row(staff, $"{p.Username}: {t.T("повернення", "refunds")}", p.Refunds + p.BarRefunds);
            csv.Row(staff, $"{p.Username}: {t.T("виплати", "payouts")}", p.Payouts);
            csv.Row(staff, $"{p.Username}: {t.T("у борг", "debt")}", p.DebtRecorded);
        }
        var ledger = t.T("Журнал операцій", "Ledger");
        csv.Row(ledger, t.T("Рядки", "Rows"), null, z.Ledger.Rows);
        csv.Row(ledger, t.T("Номери рядків", "Row ids"), $"{z.Ledger.FirstId}–{z.Ledger.LastId}");
        csv.Row(ledger, t.T("Збігається з журналом", "Reconciled with the ledger"), z.Ledger.Reconciled ? t.T("так", "yes") : t.T("НІ", "NO"));
        if (s.AuditHeadHash != null) csv.Row(t.T("Журнал дій", "Audit log"), t.T("Останній запис і хеш при закритті", "Last row and hash at the close"), $"#{s.AuditHeadId} {s.AuditHeadHash}");
        return csv.ToBytes();
    }

    /// <summary>Ledger rows of a club-time date range, oldest first (accounting export).</summary>
    public static async Task<(byte[] Bytes, int Rows)> LedgerAsync(CmsDbContext db, ClubClock clock, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        string? lang, string? dialect, CancellationToken ct = default)
    {
        var t = new CsvTexts(lang);
        var csv = new CsvWriter(dialect);
        csv.Row("Id", t.T("Час (клуб)", "Time (club)"), t.T("Тип", "Type"), t.T("Код типу", "Type code"), t.T("Спосіб оплати", "Method"),
            t.T("Гравець", "Player"), t.T("Сума", "Amount"), t.T("Зміна балансу", "Balance change"), t.T("Зміна бонусів", "Bonus change"),
            t.T("Зміна боргу", "Debt change"), t.T("Каса (готівка/картка)", "Desk (cash/card)"), t.T("Баланс після", "Balance after"),
            t.T("Бонуси після", "Bonus after"), t.T("Борг після", "Debt after"), t.T("Зміна", "Shift"), t.T("Чек", "Receipt"),
            t.T("Сесія", "Session"), t.T("Персонал", "Staff"), t.T("Опис", "Description"), t.T("Причина", "Reason"));
        var players = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        // PostgreSQL filters in SQL; SQLite cannot compare DateTimeOffset in SQL, so rows are streamed and filtered here
        var query = db.Ledger.AsNoTracking().OrderBy(l => l.Id).AsQueryable();
        if (db.Database.IsNpgsql()) query = query.Where(l => l.CreatedUtc >= fromUtc && l.CreatedUtc < toUtc);
        var count = 0;
        await foreach (var l in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (l.CreatedUtc < fromUtc || l.CreatedUtc >= toUtc) continue;
            count++;
            csv.Row(l.Id, Local(clock, l.CreatedUtc), t.Type(l.Type), l.Type, t.Method(l.Method),
                l.UserId.HasValue && players.TryGetValue(l.UserId.Value, out var name) ? name : null,
                l.Amount, l.BalanceDelta, l.BonusDelta, l.DebtDelta, l.DeskAmount, l.BalanceAfter, l.BonusAfter, l.DebtAfter,
                l.ShiftId, l.SaleId, l.SessionId?.ToString(), l.ActorUsername, l.Description, l.Reason);
        }
        return (csv.ToBytes(), count);
    }
}
