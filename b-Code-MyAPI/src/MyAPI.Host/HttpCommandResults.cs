using MyAPI.Abstractions;

namespace MyAPI.Host;

internal static class HttpCommandResults
{
    public static int StatusCode(CommandStatus status) => status switch
    {
        CommandStatus.Succeeded => StatusCodes.Status200OK,
        CommandStatus.NotFound => StatusCodes.Status404NotFound,
        CommandStatus.Rejected => StatusCodes.Status400BadRequest,
        CommandStatus.Canceled => 499,
        CommandStatus.TimedOut => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError
    };
}
