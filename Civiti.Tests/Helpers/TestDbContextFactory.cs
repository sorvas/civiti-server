using Civiti.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Civiti.Tests.Helpers;

/// <summary>
/// Creates CivitiDbContext instances backed by a persistent SQLite in-memory connection.
/// The connection stays open for the lifetime of the factory, keeping the in-memory database alive.
/// Dispose the factory to close the connection and discard the database.
/// </summary>
public sealed class TestDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CivitiDbContext> _options;

    public TestDbContextFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CivitiDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Create schema
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public CivitiDbContext CreateContext() => new(_options);

    /// <summary>
    /// A context with command interceptors attached, for tests that need to act in the middle
    /// of a service's own statement sequence — the only way to reach a conditional claim's
    /// losing branch here, since every context shares one SQLite connection and so cannot hold
    /// two genuinely concurrent transactions.
    /// </summary>
    public CivitiDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CivitiDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options);

    public void Dispose() => _connection.Dispose();
}
