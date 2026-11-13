using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RemoteCI.Server.Data;

/// <summary>
/// SQLite 忙/锁重试策略：WAL 下并发写仍可能短暂出现 SQLITE_BUSY(5)/LOCKED(6)，
/// 按指数退避重试整个执行单元；显式事务必须经 CreateExecutionStrategy 包裹后才能重试。
/// </summary>
public sealed class SqliteRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(2))
{
    protected override bool ShouldRetryOn(Exception exception) => IsTransient(exception);

    private static bool IsTransient(Exception? exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        DbUpdateException { InnerException: { } inner } => IsTransient(inner),
        _ => false,
    };
}

public sealed class SqliteRetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
{
    public IExecutionStrategy Create() => new SqliteRetryingExecutionStrategy(dependencies);
}