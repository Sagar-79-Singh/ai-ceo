using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Agent> Agents { get; set; } = null!;

    public DbSet<TaskItem> Tasks { get; set; } = null!;

    public DbSet<TaskLog> TaskLogs { get; set; } = null!;

    public DbSet<WorkspaceSettings> WorkspaceSettings { get; set; } = null!;

    public DbSet<UserSettings> UserSettings { get; set; } = null!;

    /// <summary>v2.0.0: the named workspaces and their saved conversations.</summary>
    public DbSet<Space> Spaces { get; set; } = null!;

    public DbSet<SpaceFeedEntry> SpaceFeedEntries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(agent => agent.Id);
            entity.Property(agent => agent.Name).IsRequired();
            entity.Property(agent => agent.Role).IsRequired();
            entity.Property(agent => agent.SystemPrompt).IsRequired();
            entity.Property(agent => agent.ModelId).IsRequired();

            // Stored as text so the SQLite file stays readable and ALTER TABLE defaults are simple.
            entity.Property(agent => agent.Provider)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(agent => agent.BaseUrl).HasMaxLength(500);
            entity.Property(agent => agent.ApiKey).HasMaxLength(500);
            entity.Property(agent => agent.CanUseTools);
        });

        modelBuilder.Entity<WorkspaceSettings>(entity =>
        {
            entity.ToTable("WorkspaceSettings");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.RootPath).HasMaxLength(1000);
        });

        modelBuilder.Entity<UserSettings>(entity =>
        {
            entity.ToTable("UserSettings");
            entity.HasKey(row => row.Id);

            // Stored as text so the SQLite file stays readable and ALTER TABLE defaults are simple.
            entity.Property(row => row.Theme).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(row => row.AccentColor).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(row => row.FontSize).HasConversion<string>().HasMaxLength(16).IsRequired();

            // v1.0.16: the user's LLM connection. The key is a local secret in the same SQLite file
            // as everything else and is never returned to the page in clear text.
            entity.Property(row => row.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(row => row.BaseUrl).HasMaxLength(500);
            entity.Property(row => row.ApiKey).HasMaxLength(500);
            entity.Property(row => row.ModelId).HasMaxLength(200);
        });

        modelBuilder.Entity<Space>(entity =>
        {
            entity.ToTable("Spaces");
            entity.HasKey(space => space.Id);
            entity.Property(space => space.Name).HasMaxLength(60).IsRequired();
            entity.Property(space => space.FolderPath).HasMaxLength(1000);
            entity.Property(space => space.IsActive);
            entity.HasIndex(space => space.IsActive);

            // Deleting a space deletes the conversation that belongs to it.
            entity.HasMany(space => space.Feed)
                .WithOne(entry => entry.Space)
                .HasForeignKey(entry => entry.SpaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SpaceFeedEntry>(entity =>
        {
            entity.ToTable("SpaceFeedEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Kind).HasMaxLength(32).IsRequired();
            entity.Property(entry => entry.AgentName).HasMaxLength(120);
            entity.Property(entry => entry.ToolName).HasMaxLength(64);
            entity.Property(entry => entry.Title).HasMaxLength(200);
            entity.Property(entry => entry.StepsJson).IsRequired();
            entity.HasIndex(entry => new { entry.SpaceId, entry.Ordinal });
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(task => task.Id);
            entity.Property(task => task.UserPrompt).IsRequired();
            entity.HasMany(task => task.Logs)
                .WithOne(log => log.Task)
                .HasForeignKey(log => log.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.AgentName).IsRequired();
            entity.Property(log => log.Action).IsRequired();
            entity.Property(log => log.ToolName).HasMaxLength(64);
            entity.HasIndex(log => new { log.TaskId, log.CreatedAt });
        });
    }
}
