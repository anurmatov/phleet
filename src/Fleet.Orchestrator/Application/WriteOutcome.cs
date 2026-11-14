namespace Fleet.Orchestrator.Application;

/// <summary>Which adapter a write came through. It selects wording and the audit tag, never a rule.</summary>
/// <remarks>
/// A rule that differs between the two surfaces is a named policy on the call (for example
/// <see cref="WriteAuthority"/>), so the difference is visible where the call is made.
/// </remarks>
public enum WriteSurface
{
    Rest,
    Mcp,
}

/// <summary>What kind of refusal a fault is. REST maps it to a status code; MCP only uses the text.</summary>
public enum WriteFaultKind
{
    /// <summary>The request is wrong (400).</summary>
    Invalid,
    /// <summary>The thing named does not exist (404).</summary>
    NotFound,
    /// <summary>The request conflicts with what is stored (409).</summary>
    Conflict,
    /// <summary>The orchestrator could not carry it out (500).</summary>
    Failed,
}

/// <summary>
/// A refused write: nothing was saved. <see cref="Code"/> is stable and is what an adapter branches
/// on; <see cref="Message"/> is the sentence shown to the caller.
/// </summary>
public sealed record WriteFault(WriteFaultKind Kind, string Code, string Message)
{
    /// <summary>Structured detail for an adapter that renders more than the sentence.</summary>
    public object? Detail { get; init; }
}

/// <summary>
/// The outcome of one write. Exactly one of three: a <see cref="Fault"/> (nothing saved), a value
/// with <see cref="NotDelivered"/> set (saved, but the running service that enforces it could not be
/// told), or a value alone (saved and delivered).
/// </summary>
public sealed class WriteOutcome<T>
{
    private WriteOutcome(T? value, WriteFault? fault, string? notDelivered)
    {
        Value = value;
        Fault = fault;
        NotDelivered = notDelivered;
    }

    public T? Value { get; }
    public WriteFault? Fault { get; }

    /// <summary>
    /// The sentence from <see cref="Services.LiveDelivery"/> when the change is stored but was not
    /// delivered. Never a success: REST answers 503 with it, MCP answers with the sentence.
    /// </summary>
    public string? NotDelivered { get; }

    public bool Saved => Fault is null;

    public static WriteOutcome<T> Ok(T value, string? notDelivered = null) => new(value, null, notDelivered);
    public static WriteOutcome<T> Refused(WriteFault fault) => new(default, fault, null);

    public static implicit operator WriteOutcome<T>(WriteFault fault) => Refused(fault);
}

/// <summary>The faults every writer shares, so codes and kinds cannot drift between them.</summary>
public static class WriteFaults
{
    public static WriteFault Invalid(string code, string message, object? detail = null) =>
        new(WriteFaultKind.Invalid, code, message) { Detail = detail };

    public static WriteFault NotFound(string code, string message) => new(WriteFaultKind.NotFound, code, message);

    public static WriteFault Conflict(string code, string message, object? detail = null) =>
        new(WriteFaultKind.Conflict, code, message) { Detail = detail };

    public static WriteFault Failed(string code, string message) => new(WriteFaultKind.Failed, code, message);

    /// <summary>An orchestrator started without a database cannot store anything.</summary>
    public static WriteFault NoDatabase => Failed("database_not_configured", "Database is not configured on this orchestrator");

    /// <summary>The agent row every agent write starts from.</summary>
    public static WriteFault AgentNotFound(string agentName) => NotFound("agent_not_found", $"Agent '{agentName}' not found in DB");
}

/// <summary>How a REST adapter answers a fault or an undelivered save. One mapping for every route.</summary>
public static class WriteHttp
{
    /// <summary>The default body is <c>{ error }</c>; a route with a different shape builds its own.</summary>
    public static IResult Fault(WriteFault fault) => fault.Kind switch
    {
        WriteFaultKind.Invalid => Results.BadRequest(new { error = fault.Message }),
        WriteFaultKind.NotFound => Results.NotFound(new { error = fault.Message }),
        WriteFaultKind.Conflict => Results.Conflict(new { error = fault.Message }),
        _ => Results.Problem(fault.Message),
    };

    /// <summary>
    /// The answer for an outcome whose success body the route supplies: the fault, else 503
    /// <c>saved: true, delivered: false</c>, else <paramref name="ok"/>.
    /// </summary>
    public static IResult Answer<T>(WriteOutcome<T> outcome, Func<T, IResult> ok)
    {
        if (outcome.Fault is { } fault) return Fault(fault);
        if (outcome.NotDelivered is { } sentence) return Services.LiveDelivery.NotDelivered(sentence);
        return ok(outcome.Value!);
    }
}
