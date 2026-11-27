using System.Globalization;

namespace Cms.Server.Billing;

/// <summary>
/// Money in the texts that the server stores (audit summaries, ledger descriptions): always two decimals, invariant
/// culture ("30.00", "+12.50"). A decimal keeps the scale of its source (PostgreSQL numeric(10,2) gives 30.00, SQLite
/// text gives 30.0, a literal gives 30), so a plain interpolation wrote different texts on the two databases.
/// </summary>
public static class MoneyText
{
    public static string Of(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Of(decimal? amount) => amount.HasValue ? Of(amount.Value) : "—";

    /// <summary>With the sign: "+30.00", "-12.50", "0.00".</summary>
    public static string Signed(decimal amount) => amount.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
}
