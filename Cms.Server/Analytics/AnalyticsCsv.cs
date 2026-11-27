using Cms.Server.Billing;
using Cms.Server.Shifts;

namespace Cms.Server.Analytics;

/// <summary>
/// The analytics of a period as CSV for accounting (UTF-8 with BOM, see CsvWriter): a summary table
/// "section; item; amount; count" like the Z-report, then the days (or hours), the zones, the
/// heatmap (weekday x club hour) and the top bar products. Money headers name the club currency.
/// </summary>
public static class AnalyticsCsv
{
    public static byte[] Build(AnalyticsReport a, ClubCurrency currency, string? lang, string? dialect)
    {
        var t = new CsvTexts(lang);
        var csv = new CsvWriter(dialect);
        string M(string uk, string en) => t.Money(uk, en, currency);

        csv.Row(t.T("Розділ", "Section"), t.T("Показник", "Item"), M("Сума", "Amount"), t.T("Кількість", "Count"));
        var period = t.T("Період", "Period");
        csv.Row(period, t.T("З", "From"), a.From.ToString("yyyy-MM-dd"));
        csv.Row(period, t.T("По", "To"), a.To.ToString("yyyy-MM-dd"));
        csv.Row(period, t.T("Часовий пояс", "Time zone"), a.TimeZone);
        csv.Row(period, t.T("Валюта", "Currency"), currency.Code);

        var revenue = t.T("Виручка", "Revenue");
        csv.Row(revenue, t.T("Разом (час + бар)", "Total (PC time + bar)"), a.Revenue.Total);
        csv.Row(revenue, t.T("Час на ПК", "PC time"), a.Revenue.Time, a.Time.Rows);
        csv.Row(revenue, t.T("Бар (нетто)", "Bar (net)"), a.Revenue.Bar, a.Bar.Receipts);
        var byMethod = t.T("Виручка за способом оплати", "Revenue by payment");
        csv.Row(byMethod, t.Method(PaymentMethods.Cash), a.Revenue.ByMethod.Cash);
        csv.Row(byMethod, t.Method(PaymentMethods.Card), a.Revenue.ByMethod.Card);
        csv.Row(byMethod, t.Method(PaymentMethods.Balance), a.Revenue.ByMethod.Balance);
        csv.Row(byMethod, t.T("Бонуси", "Bonus"), a.Revenue.ByMethod.Bonus);
        csv.Row(byMethod, t.Method(PaymentMethods.Debt), a.Revenue.ByMethod.Debt);

        var bar = t.T("Бар", "Bar");
        csv.Row(bar, t.T("Продажі", "Sales"), a.Bar.Sales.Total, a.Bar.Receipts);
        csv.Row(bar, t.T("Повернення", "Refunds"), a.Bar.Refunds.Total, a.Bar.RefundReceipts);
        var refunds = t.T("Повернення", "Refunds");
        csv.Row(refunds, t.T("Чеки бару", "Bar receipts"), a.Refunds.Bar, a.Refunds.BarReceipts);
        csv.Row(refunds, t.T("З балансу гравців", "From player balances"), a.Refunds.Balance, a.Refunds.BalanceRefunds);
        csv.Row(refunds, t.T("Разом", "Total"), a.Refunds.Total);
        csv.Row(t.T("Поповнення (не виручка)", "Top-ups (not revenue)"), t.T("Разом", "Total"), a.TopUps.Total, a.TopUps.Count);
        var bonus = t.T("Бонуси", "Bonus");
        csv.Row(bonus, t.T("Нараховано (кешбек)", "Issued (cashback)"), a.Bonus.Issued);
        csv.Row(bonus, t.T("Витрачено на час", "Spent on PC time"), a.Bonus.Spent);
        csv.Row(bonus, t.T("Скасовано", "Reversed"), a.Bonus.Reversed);
        csv.Row(bonus, t.T("Коригування", "Corrections"), a.Bonus.Adjusted);
        var debt = t.T("Борги", "Debt");
        csv.Row(debt, t.T("Записано в борг", "Recorded"), a.Debt.Recorded);
        csv.Row(debt, t.T("Сплачено", "Paid"), a.Debt.Paid.Total);
        csv.Row(debt, t.T("Списано", "Written off"), a.Debt.WrittenOff);
        csv.Row(t.T("Виплати з каси", "Cash payouts"), t.T("Разом", "Total"), a.Payouts);
        var average = t.T("Середній чек", "Average ticket");
        csv.Row(average, t.T("Сесія (час на ПК)", "Session (PC time)"), a.AverageTicket.Session, a.AverageTicket.Sessions);
        csv.Row(average, t.T("Чек бару", "Bar receipt"), a.AverageTicket.Receipt, a.AverageTicket.Receipts);
        var desk = t.T("Каса", "Desk");
        csv.Row(desk, t.T("Готівка (нетто)", "Cash (net)"), a.Cash.Net);
        csv.Row(desk, t.T("Картка (нетто)", "Card (net)"), a.Card.Net);
        var shifts = t.T("Зміни", "Shifts");
        csv.Row(shifts, t.T("Закрито змін", "Shifts closed"), null, a.Shifts.Closed);
        csv.Row(shifts, t.T("Розбіжність каси (разом)", "Cash variance (total)"), a.Shifts.Variance);
        csv.Row(shifts, t.T("Нестача", "Shortage"), a.Shifts.Shortage);
        csv.Row(shifts, t.T("Надлишок", "Surplus"), a.Shifts.Surplus);
        var check = t.T("Перевірка", "Check");
        csv.Row(check, t.T("Рядків журналу операцій", "Ledger rows"), null, a.Check.LedgerRows);
        csv.Row(check, t.T("Збігається з журналом операцій", "Reconciled with the ledger"), a.Check.Reconciled ? t.T("так", "yes") : t.T("НІ", "NO"));
        csv.Row(check, t.T("Зміни повністю в періоді (Z-звіти)", "Shifts fully in the period (Z-reports)"), null, a.Check.ShiftsInside);
        csv.Row(check, t.T("Збігається із Z-звітами", "Reconciled with the Z-reports"), a.Check.ShiftsAddUp ? t.T("так", "yes") : t.T("НІ", "NO"));

        csv.Blank();
        csv.Row(a.Granularity == "hour" ? t.T("Година", "Hour") : t.T("Дата", "Date"), M("Час на ПК", "PC time"), M("Бар", "Bar"), M("Разом", "Total"),
            M("Готівка", "Cash"), M("Картка", "Card"), M("Баланс", "Balance"), M("Бонуси", "Bonus"), M("Борг", "Debt"),
            t.T("Завантаженість, %", "Occupancy, %"));
        foreach (var p in a.Series)
            csv.Row(p.Label, p.Time, p.Bar, p.Total, p.ByMethod.Cash, p.ByMethod.Card, p.ByMethod.Balance, p.ByMethod.Bonus, p.ByMethod.Debt, p.Occupancy);

        csv.Blank();
        csv.Row(t.T("Зона", "Zone"), t.T("ПК", "PCs"), t.T("Зайнято, год", "Busy, hours"), t.T("Можливо, год", "Capacity, hours"), t.T("Завантаженість, %", "Occupancy, %"));
        csv.Row(t.T("Усі ПК", "All PCs"), a.Occupancy.Pcs, a.Occupancy.BusyHours, a.Occupancy.CapacityHours, a.Occupancy.Percent);
        foreach (var z in a.Occupancy.Zones)
            csv.Row(z.ZoneId == null ? t.T("Без зони", "No zone") : z.Name, z.Pcs, z.BusyHours, z.CapacityHours, z.Percent);

        csv.Blank();
        var days = t.T("Пн,Вт,Ср,Чт,Пт,Сб,Нд", "Mon,Tue,Wed,Thu,Fri,Sat,Sun").Split(',');
        csv.Row(new object?[] { t.T("Завантаженість, % (день / година)", "Occupancy, % (day / hour)") }.Concat(Enumerable.Range(0, 24).Select(h => (object?)$"{h:00}")).ToArray());
        for (var d = 0; d < 7; d++)
            csv.Row(new object?[] { days[d] }.Concat(a.Heatmap.Percent[d].Select(v => (object?)v)).ToArray());

        if (a.Bar.Items.Count > 0)
        {
            csv.Blank();
            csv.Row(t.T("Товар бару", "Bar product"), t.T("Категорія", "Category"), t.T("Кількість", "Quantity"), M("Сума", "Amount"));
            foreach (var item in a.Bar.Items) csv.Row(item.Name, item.Category, item.Quantity, item.Amount);
        }
        return csv.ToBytes();
    }
}
