using ApplicationScopeSample.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApplicationScopeSample.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }

    public DbSet<FeaturePermission> FeaturePermissions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.Property(p => p.Id).HasDefaultValueSql("NEWSEQUENTIALID()");

            e.HasIndex(p => p.Email).IsUnique();
        });

        modelBuilder.Entity<FeaturePermission>(e =>
        {
            e.Property(p => p.Id).HasDefaultValueSql("NEWSEQUENTIALID()");

            e.HasIndex(p => p.Name).IsUnique();
        });
    }
}
