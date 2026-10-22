using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiCeo.Data;

/// <summary>
/// Lets `dotnet ef migrations add ...` / `dotnet ef database update` work against the SQLite
/// database without booting the web host.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable(DatabasePaths.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "aiceo.db");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        return new AppDbContext(options);
    }
}
