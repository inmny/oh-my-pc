using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OhMyPc.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppPaths.DatabasePath};Default Timeout=5")
            .Options;
        return new AppDbContext(options);
    }
}
