namespace FlightLog.Application.Common;

/// <summary>
/// The resource does not exist or the caller is not allowed to know that it exists.
/// Both cases map to HTTP 404 so that resource ids cannot be probed across teams.
/// </summary>
public sealed class NotFoundException(string resource, object id)
    : Exception($"{resource} '{id}' was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>The caller can see the resource but lacks the role required for the operation.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>The request conflicts with the current state (e.g. stale version, duplicate name).</summary>
public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
