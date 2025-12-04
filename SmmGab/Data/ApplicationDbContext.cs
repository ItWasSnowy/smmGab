using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmmGab.Domain.Models;

namespace SmmGab.Data;

public class ApplicationDbContext : IdentityDbContext<User, IdentityRole<Guid>, Guid>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects { get; set; }
    public DbSet<Channel> Channels { get; set; }
    public DbSet<Publication> Publications { get; set; }
    public DbSet<PublicationTarget> PublicationTargets { get; set; }
    public DbSet<FileStorage> FileStorage { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Project configuration
        builder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerId);
            entity.HasOne(e => e.Owner)
                .WithMany(u => u.Projects)
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Channel configuration
        builder.Entity<Channel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Channels)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Publication configuration
        builder.Entity<Publication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.AuthorId);
            entity.HasIndex(e => e.ScheduledAtUtc);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Publications)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Author)
                .WithMany(u => u.Publications)
                .HasForeignKey(e => e.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // PublicationTarget configuration
        builder.Entity<PublicationTarget>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PublicationId);
            entity.HasIndex(e => e.ChannelId);
            entity.HasOne(e => e.Publication)
                .WithMany(p => p.Targets)
                .HasForeignKey(e => e.PublicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Channel)
                .WithMany(c => c.PublicationTargets)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // FileStorage configuration
        builder.Entity<FileStorage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PublicationId);
            entity.HasOne(e => e.Publication)
                .WithMany(p => p.Files)
                .HasForeignKey(e => e.PublicationId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

