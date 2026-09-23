using System.Text.Json;
using System.Threading.Channels;
using ErGe.Core.Actions;

namespace ErGe.Core.Service;

public sealed class SessionAgentActionQueue : IInteractiveCapabilityExecutor
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(8);
    private readonly Channel<PendingSessionAction> _channel =
        Channel.CreateBounded<PendingSessionAction>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

    private volatile bool _connected;

    public bool Connected => _connected;

    public void SetConnected(bool connected)
    {
        _connected = connected;

        if (!connected)
        {
            FailPending(new InvalidOperationException(
                "Interactive Session Agent is not connected."));
        }
    }

    public async Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException(
                "Interactive Session Agent is not connected.");
        }

        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = new PendingSessionAction(
            Guid.NewGuid().ToString("N"),
            action,
            arguments.Clone(),
            completion);

        if (!_channel.Writer.TryWrite(pending))
        {
            throw new InvalidOperationException(
                "Interactive action queue is full.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linked.CancelAfter(ExecutionTimeout);

        try
        {
            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Interactive action '{action}' exceeded {ExecutionTimeout.TotalSeconds:g} seconds.");
        }
    }

    internal bool TryRead(out PendingSessionAction? pending) =>
        _channel.Reader.TryRead(out pending);

    internal ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    internal void Complete(
        PendingSessionAction pending,
        JsonElement result) =>
        pending.Completion.TrySetResult(result.Clone());

    internal void Fail(
        PendingSessionAction pending,
        Exception error) =>
        pending.Completion.TrySetException(error);

    internal void FailPending(Exception error)
    {
        while (_channel.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetException(error);
        }
    }
}

public sealed record PendingSessionAction(
    string QueueId,
    string Action,
    JsonElement Arguments,
    TaskCompletionSource<JsonElement> Completion);
