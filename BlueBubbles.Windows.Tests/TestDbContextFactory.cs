using BlueBubbles.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace BlueBubbles.Windows.Tests;

public class TestDbContextFactory : IDbContextFactory<BlueBubblesDbContext>
{
    private readonly DbContextOptions<BlueBubblesDbContext> _options;
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;

    public TestDbContextFactory(DbContextOptions<BlueBubblesDbContext> options)
    {
        _options = options;
    }

    public BlueBubblesDbContext CreateDbContext()
    {
        var db = new BlueBubblesDbContext(_options);
        if (_connection is null)
        {
            _connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
            _connection.Open();
            db.Database.EnsureCreated();
        }
        return db;
    }

    public static TestDbContextFactory Create()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<BlueBubblesDbContext>()
            .UseSqlite(connection)
            .Options;

        var factory = new TestDbContextFactory(options);
        factory._connection = connection;
        using var db = new BlueBubblesDbContext(options);
        db.Database.EnsureCreated();
        return factory;
    }
}
