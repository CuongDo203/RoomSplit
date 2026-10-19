using Microsoft.AspNetCore.Diagnostics;

namespace RoomSplit.Api.Exceptions;

public class AppExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Unknown exceptions fall through to the default handler (500).
        if (exception is not AppException appException) return false;

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
}
