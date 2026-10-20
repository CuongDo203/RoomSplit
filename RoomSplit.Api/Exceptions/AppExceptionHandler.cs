using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace RoomSplit.Api.Exceptions;

public class AppExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<AppExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var appException = exception switch
        {
            AppException e => e,
            DbUpdateException { InnerException: MySqlException mySql } => FromMySql(mySql),
            _ => null
        };

        // Unknown exceptions fall through to the default handler (500).
        if (appException is null) return false;

        if (appException != exception)
        {
            logger.LogWarning(exception, "Database constraint violation mapped to {StatusCode}", appException.StatusCode);
        }

        httpContext.Response.StatusCode = appException.StatusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = appException.StatusCode,
                Title = appException.Title,
                Detail = appException.Message
            }
        });
    }

    // Safety net for races between a service's existence/history checks and SaveChanges;
    // services still check first so the common case gets a specific message.
    private static ConflictException? FromMySql(MySqlException e) => e.ErrorCode switch
    {
        MySqlErrorCode.RowIsReferenced2 =>
            new ConflictException("Resource is in use", "The resource is referenced by other data and cannot be deleted."),
        MySqlErrorCode.NoReferencedRow2 =>
            new ConflictException("Related resource missing", "A referenced resource no longer exists."),
        MySqlErrorCode.DuplicateKeyEntry =>
            new ConflictException("Duplicate value", "A resource with the same unique value already exists."),
        _ => null
    };
}
