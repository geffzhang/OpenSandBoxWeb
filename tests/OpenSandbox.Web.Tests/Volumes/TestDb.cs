using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSandbox.Web.Infrastructure.Volumes.Persistence;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public VolumeDbContext Db { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new VolumeDbContext(options);
        Db.Database.EnsureCreated();

        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        Db.Tenants.AddRange(
            new TenantEntity
            {
                Id = "goodcrew", DisplayName = "GoodCrew", Status = "active",
                CreatedAt = now, UpdatedAt = now,
            },
            new TenantEntity
            {
                Id = "other", DisplayName = "Other", Status = "active",
                CreatedAt = now, UpdatedAt = now,
            });
        Db.SaveChanges();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
