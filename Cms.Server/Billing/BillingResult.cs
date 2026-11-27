using Microsoft.AspNetCore.Mvc;

namespace Cms.Server.Billing;

/// <summary>
/// An error of a billing operation. The API returns { error, code, args }: the admin UI shows its own
/// text for the code (admin-ui/app/i18n.ts, with {0}, {1} ... from args) and falls back to the
/// English message.
/// </summary>
public sealed record BillingError(string Code, string Message, int Status = StatusCodes.Status400BadRequest)
{
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public BillingError With(params object[] args) => this with
    {
        Message = string.Format(System.Globalization.CultureInfo.InvariantCulture, Message, args),
        Args = args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList()
    };

    private object Body => new { error = Message, code = Code, args = Args };

    /// <summary>400 / 409 with { error, code, args }; 404 without a body (like the other endpoints).</summary>
    public ActionResult ToActionResult() => Status switch
    {
        StatusCodes.Status404NotFound => new NotFoundResult(),
        StatusCodes.Status409Conflict => new ConflictObjectResult(Body),
        StatusCodes.Status400BadRequest => new BadRequestObjectResult(Body),
        _ => new ObjectResult(Body) { StatusCode = Status }
    };
}

public readonly record struct BillingResult<T>(T? Value, BillingError? Error)
{
    public bool Ok => Error == null;
    public static implicit operator BillingResult<T>(BillingError error) => new(default, error);
    public static BillingResult<T> Success(T value) => new(value, null);
}

public static class BillingErrors
{
    private const int NotFound = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    public static readonly BillingError DeviceNotFound = new("device_not_found", "Device not found", NotFound);
    public static readonly BillingError DeviceNotApproved = new("device_not_approved", "The PC is not approved.", Conflict);
    public static readonly BillingError DeviceBusy = new("device_busy", "Device already has an active session.", Conflict);
    public static readonly BillingError SameDevice = new("same_device", "The session already runs on this PC.");
    public static readonly BillingError SessionNotFound = new("session_not_found", "Session not found", NotFound);
    public static readonly BillingError SessionEnded = new("session_ended", "Session already ended");
    public static readonly BillingError SessionNotActive = new("session_not_active", "Session is not active");
    public static readonly BillingError SessionPaused = new("session_paused", "The session is paused. Resume it first.", Conflict);
    public static readonly BillingError SessionNotPaused = new("session_not_paused", "The session is not paused.", Conflict);
    public static readonly BillingError SessionExpired = new("session_expired", "The session time is over.", Conflict);
    public static readonly BillingError SessionChanged = new("session_changed", "The session was changed at the same time. Reload and try again.", Conflict);
    public static readonly BillingError OpenSessionNoEnd = new("open_session_no_end", "Open-ended sessions have no end time to extend");
    public static readonly BillingError PastCutoff = new("past_cutoff", "The package ends at {0}; the session cannot run past it.");
    public static readonly BillingError BadDuration = new("bad_duration", "Duration must be between 1 and 1440 minutes");
    public static readonly BillingError BadMinutes = new("bad_minutes", "Minutes must be between 1 and 1440");
    public static readonly BillingError NoTariff = new("no_tariff", "No tariff configured. Create a tariff first.");
    public static readonly BillingError TariffNotFound = new("tariff_not_found", "Tariff not found", NotFound);
    public static readonly BillingError TariffOtherZone = new("tariff_other_zone", "This tariff belongs to another zone.");
    public static readonly BillingError UserNotFound = new("user_not_found", "User not found", NotFound);
    public static readonly BillingError InsufficientBalance = new("insufficient_balance", "Insufficient balance. Need {0} ₴, have {1} ₴ (bonus {2} ₴)");
    public static readonly BillingError BalanceTooLow = new("balance_too_low", "The balance is too low for this operation");
    public static readonly BillingError BadPaymentMethod = new("bad_payment_method", "Payment method must be balance, cash or card.");
    public static readonly BillingError GuestNeedsDesk = new("guest_needs_desk", "A guest pays at the desk: cash or card.");
    public static readonly BillingError PackageNotFound = new("package_not_found", "Package not found", NotFound);
    public static readonly BillingError PackageInactive = new("package_inactive", "The package is not on sale.");
    public static readonly BillingError PackageInvalid = new("package_invalid", "The package has neither a duration nor a time window.");
    public static readonly BillingError PackageOtherZone = new("package_other_zone", "The package is for another zone.");
    public static readonly BillingError PackageOutsideWindow = new("package_outside_window", "The package is sold only from {0} to {1}.");
    public static readonly BillingError PackageNotToday = new("package_not_today", "The package is not sold today.");
    public static readonly BillingError PackageWindowEnds = new("package_window_ends", "The package time window ends now.");
    public static readonly BillingError PackageNoMatchInZone = new("package_no_match_in_zone", "Zone {0} has no package of the same kind; move the session within its zone.", Conflict);
    public static readonly BillingError BadAmount = new("bad_amount", "Amount must be positive (at most 100000)");
    public static readonly BillingError ReasonRequired = new("reason_required", "A reason is required");
}
