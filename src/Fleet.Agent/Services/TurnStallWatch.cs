namespace Fleet.Agent.Services;

/// <summary>
/// One read of the executor's event stream, with a limit on how long it may stay silent.
/// </summary>
/// <remarks>
/// <para>
/// A turn has no total deadline, and must not get one: a long turn is legitimate as long as the
/// provider keeps reporting what it is doing. What is not legitimate is a provider process that
/// has stopped reporting anything. It holds the agent's single turn slot, the queue behind it
/// fills, and nothing ends it. So the limit is on <b>silence</b>: the timer restarts on every event
/// the executor yields, and only when it runs out is <see cref="Token"/> cancelled.
/// </para>
/// <para>
/// <see cref="Token"/> is what the executor is handed in place of the turn's own token. It is
/// cancelled by the turn's token too, so a user or operator cancel still reaches the executor;
/// <see cref="Stalled"/> tells the two apart.
/// </para>
/// <para>
/// One watch per executor read, never reused: once it has fired its token stays cancelled, and a
/// continuation turn started with it would be cancelled before it began.
/// </para>
/// </remarks>
internal sealed class TurnStallWatch : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly CancellationTokenRegistration _turnLink;
    private readonly CancellationToken _turnToken;
    private readonly TimeSpan _timeout;

    /// <param name="timeout">The allowed silence. Zero or less disables the watch.</param>
    /// <param name="turnToken">The turn's own cancellation, which still has to reach the executor.</param>
    /// <param name="time">The clock the timer runs on.</param>
    public TurnStallWatch(TimeSpan timeout, CancellationToken turnToken, TimeProvider time)
    {
        _timeout = timeout;
        _turnToken = turnToken;
        if (timeout <= TimeSpan.Zero)
            return;

        _cts = new CancellationTokenSource(timeout, time);
        _turnLink = turnToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), _cts);
    }

    /// <summary>The token for the executor read: the turn's own when the watch is disabled.</summary>
    public CancellationToken Token => _cts?.Token ?? _turnToken;

    public TimeSpan Timeout => _timeout;

    /// <summary>An event arrived: the silence starts over.</summary>
    public void Touch()
    {
        try { _cts?.CancelAfter(_timeout); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// True when the read was cancelled by silence. A turn that was also cancelled by its own token
    /// is reported as that cancel, not as a stall.
    /// </summary>
    public bool Stalled => _cts is { IsCancellationRequested: true } && !_turnToken.IsCancellationRequested;

    public void Dispose()
    {
        _turnLink.Dispose();
        _cts?.Dispose();
    }
}
