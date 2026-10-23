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
