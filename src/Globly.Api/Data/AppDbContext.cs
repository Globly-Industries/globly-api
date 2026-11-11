using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<MediaFile> Files => Set<MediaFile>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<PostVote> PostVotes => Set<PostVote>();
    public DbSet<PostReport> PostReports => Set<PostReport>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.HasPostgresExtension("postgis");

        b.Entity<Post>(e =>
        {
            e.Property(p => p.Title).HasMaxLength(120);
            e.Property(p => p.Description).HasMaxLength(2000);
            e.Property(p => p.Location).HasColumnType("geometry(Point,4326)");
            e.HasIndex(p => p.Location).HasMethod("gist");
            e.HasIndex(p => p.CreatedAt);
            e.HasIndex(p => p.IsHidden);

            e.HasOne(p => p.User).WithMany(u => u.Posts)
                .HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.File).WithMany()
                .HasForeignKey(p => p.FileId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MediaFile>(e =>
        {
            e.ToTable("Files");
            e.Property(f => f.ObjectKey).HasMaxLength(300);
            e.Property(f => f.FileName).HasMaxLength(255);
            e.Property(f => f.ContentType).HasMaxLength(100);
            e.HasIndex(f => f.ObjectKey).IsUnique();
        });

        b.Entity<Comment>(e =>
        {
            e.Property(c => c.Body).HasMaxLength(1000);
            e.HasIndex(c => new { c.PostId, c.CreatedAt });
            e.HasOne(c => c.Post).WithMany(p => p.Comments)
                .HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.User).WithMany()
                .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PostVote>(e =>
        {
            e.HasKey(v => new { v.PostId, v.UserId });
            e.HasOne<Post>().WithMany().HasForeignKey(v => v.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PostReport>(e =>
        {
            e.HasKey(r => new { r.PostId, r.UserId });
            e.Property(r => r.Reason).HasMaxLength(500);
            e.HasOne<Post>().WithMany().HasForeignKey(r => r.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
