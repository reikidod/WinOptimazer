using WinOptimizer.Infrastructure;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task Execute_BlocksConcurrentRunsUntilCurrentExecutionCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task;
        });

        command.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, Volatile.Read(ref executions));

        release.TrySetResult();
        await WaitUntilAsync(() => command.CanExecute(null));

        Assert.Equal(1, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task Execute_ReportsExceptionsAndRestoresCommandAvailability()
    {
        var expected = new InvalidOperationException("expected test failure");
        Exception? reported = null;

        var command = new AsyncRelayCommand(
            () => Task.FromException(expected),
            onError: exception => reported = exception);

        command.Execute(null);
        await WaitUntilAsync(() => reported is not null && command.CanExecute(null));

        Assert.Same(expected, reported);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task GenericCommand_PassesParameterAndBlocksSecondExecution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? received = null;

        var command = new AsyncRelayCommand<string>(async value =>
        {
            received = value;
            started.TrySetResult();
            await release.Task;
        });

        command.Execute("first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("first", received);
        Assert.False(command.CanExecute("second"));
        command.Execute("second");
        Assert.Equal("first", received);

        release.TrySetResult();
        await WaitUntilAsync(() => command.CanExecute("second"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
