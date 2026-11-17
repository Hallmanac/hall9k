using System.Data;
using Marten;
using Marten.Services;
using Npgsql;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A query session over one repeatable-read transaction, so every read <c>h9k config show</c>'s spend lines make
/// sees the same committed events. The period total and the per-account split are separate queries over
/// <c>TokensRecorded</c>; read committed, a session the daemon records between them would land in the account
/// lines but not the total and push the unattributed line below zero. Read-only: the transaction is committed
/// on dispose only so that nothing Marten did on it is rolled back.
/// </summary>
internal sealed class SpendSnapshot : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private SpendSnapshot(NpgsqlConnection connection, NpgsqlTransaction transaction, IQuerySession session)
    {
        _connection = connection;
        _transaction = transaction;
        Session = session;
    }

    public IQuerySession Session { get; }

    public static async Task<SpendSnapshot> OpenAsync(
        IDocumentStore store, string connectionString, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            return new SpendSnapshot(connection, transaction, store.QuerySession(SessionOptions.ForTransaction(transaction)));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        try
        {
            // Uncancellable cleanup with no token in scope. A read-only transaction that fails to commit
            // loses nothing, so a failure here must not discard lines already read.
            await _transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
        }
        finally
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
