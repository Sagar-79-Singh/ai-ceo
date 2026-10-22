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
            entity.HasIndex(log => new { log.TaskId, log.CreatedAt });
        });
    }
}
