using Cms.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cms.Server.Shifts;

/// <summary>
/// CmsDbContext refuses a cash or card ledger row without an open shift (ShiftRequiredException);
/// the transaction of the request rolls back and the API answers 409 { error, code: "shift_required" }.
/// </summary>
public sealed class ShiftRequiredFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ShiftRequiredException) return;
        context.Result = ShiftErrors.Required.ToActionResult();
        context.ExceptionHandled = true;
    }
}
