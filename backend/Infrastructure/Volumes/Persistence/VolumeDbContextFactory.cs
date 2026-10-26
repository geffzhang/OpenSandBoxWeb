using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenSandbox.Web.Infrastructure.Volumes.Persistence;

public sealed class VolumeDbContextFactory : IDesignTimeDbContextFactory<VolumeDbContext>
{
    public VolumeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__VolumeManager")
            ?? "Host=localhost;Port=5432;Database=opensandbox;Username=opensandbox;Password=opensandbox";

        var options = new DbContextOptionsBuilder<VolumeDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new VolumeDbContext(options);
    }
}
