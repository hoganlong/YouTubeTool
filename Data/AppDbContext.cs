using Microsoft.EntityFrameworkCore;
using YouTubeTool.Models;

namespace YouTubeTool.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ChannelList> ChannelLists => Set<ChannelList>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<WatchHistoryEntry> WatchHistory => Set<WatchHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>()
            .HasIndex(c => c.YouTubeChannelId)
            .IsUnique();

        modelBuilder.Entity<Video>()
            .HasIndex(v => v.YouTubeVideoId)
            .IsUnique();

        modelBuilder.Entity<Channel>()
            .HasMany(c => c.Lists)
            .WithMany(l => l.Channels)
            .UsingEntity<ChannelListChannel>(
                l => l.HasOne(e => e.List).WithMany().HasForeignKey(e => e.ListsId),
                r => r.HasOne(e => e.Channel).WithMany().HasForeignKey(e => e.ChannelsId),
                j =>
                {
                    j.HasKey(e => new { e.ChannelsId, e.ListsId });
                    j.Property(e => e.SortOrder).HasDefaultValue(0);
                });

        modelBuilder.Entity<WatchHistoryEntry>()
            .HasIndex(w => w.YouTubeVideoId)
            .IsUnique();
    }
}
