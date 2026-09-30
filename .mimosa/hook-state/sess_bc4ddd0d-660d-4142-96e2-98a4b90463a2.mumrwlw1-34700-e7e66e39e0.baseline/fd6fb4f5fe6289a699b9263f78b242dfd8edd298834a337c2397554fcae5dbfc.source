using Microsoft.Extensions.Logging;

namespace RemoteCI.Server.Tests;

/// <summary>统计 EF Core 已执行的数据库命令，作为高频 WebSocket 消息的查询回归信号。</summary>
internal sealed class DatabaseCommandCounterLoggerProvider : ILoggerProvider
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName) =>
        new CounterLogger(this, categoryName == "Microsoft.EntityFrameworkCore.Database.Command");

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public void Dispose() { }

    private sealed class CounterLogger(DatabaseCommandCounterLoggerProvider owner, bool tracksCommands) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => tracksCommands && logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (tracksCommands && eventId.Id == 20101)
                Interlocked.Increment(ref owner._count);
        }
    }
}
