namespace RoomSplit.Api.Exceptions;

/// <summary>
/// Expected errors thrown by services; mapped to ProblemDetails by <see cref="AppExceptionHandler"/>.
/// </summary>
public abstract class AppException(int statusCode, string title, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}

public class NotFoundException(string message)
    : AppException(StatusCodes.Status404NotFound, "Not Found", message);

public class ConflictException(string title, string message)
    : AppException(StatusCodes.Status409Conflict, title, message);

public class BusinessRuleException(string title, string message)
    : AppException(StatusCodes.Status400BadRequest, title, message);
