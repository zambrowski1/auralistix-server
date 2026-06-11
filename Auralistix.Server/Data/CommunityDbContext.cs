using Microsoft.EntityFrameworkCore;

namespace Auralistix.Server.Data;

public sealed class CommunityDbContext(DbContextOptions<CommunityDbContext> options) : DbContext(options)
{
    public DbSet<CommunityUser> Users => Set<CommunityUser>();
    public DbSet<SoundCategory> Categories => Set<SoundCategory>();
    public DbSet<CommunitySound> Sounds => Set<CommunitySound>();
    public DbSet<CommunityFollow> Follows => Set<CommunityFollow>();
    public DbSet<CommunityCollection> Collections => Set<CommunityCollection>();
    public DbSet<CommunityCollectionSound> CollectionSounds => Set<CommunityCollectionSound>();
    public DbSet<CommunitySoundDownload> SoundDownloads => Set<CommunitySoundDownload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommunityUser>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.Email).IsUnique();
            entity.HasIndex(user => user.Username).IsUnique();
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.Property(user => user.Username).HasMaxLength(32);
            entity.Property(user => user.DisplayName).HasMaxLength(80);
            entity.Property(user => user.Bio).HasMaxLength(280);
            entity.Property(user => user.Country).HasMaxLength(80);
            entity.Property(user => user.FavoriteGame).HasMaxLength(120);
            entity.Property(user => user.Tier).HasConversion<string>();

            entity.HasMany(user => user.FollowingRelationships)
                .WithOne(follow => follow.FollowerUser)
                .HasForeignKey(follow => follow.FollowerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(user => user.FollowerRelationships)
                .WithOne(follow => follow.FollowedUser)
                .HasForeignKey(follow => follow.FollowedUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(user => user.Collections)
                .WithOne(collection => collection.Owner)
                .HasForeignKey(collection => collection.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(user => user.SoundDownloads)
                .WithOne(download => download.User)
                .HasForeignKey(download => download.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SoundCategory>(entity =>
        {
            entity.HasKey(category => category.Id);
            entity.HasIndex(category => category.Slug).IsUnique();
            entity.Property(category => category.Name).HasMaxLength(80);
            entity.Property(category => category.Slug).HasMaxLength(80);
            entity.Property(category => category.Description).HasMaxLength(280);
        });

        modelBuilder.Entity<CommunitySound>(entity =>
        {
            entity.HasKey(sound => sound.Id);
            entity.Property(sound => sound.Title).HasMaxLength(120);
            entity.Property(sound => sound.Description).HasMaxLength(500);
            entity.Property(sound => sound.OriginalFileName).HasMaxLength(255);
            entity.Property(sound => sound.StoredFilePath).HasMaxLength(255);
            entity.Property(sound => sound.ContentType).HasMaxLength(120);
            entity.Property(sound => sound.ModerationStatus).HasConversion<string>();
            entity.Property(sound => sound.ModerationNotes).HasMaxLength(500);

            entity.HasOne(sound => sound.UploadedByUser)
                .WithMany(user => user.UploadedSounds)
                .HasForeignKey(sound => sound.UploadedByUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(sound => sound.ModeratedByUser)
                .WithMany(user => user.ModeratedSounds)
                .HasForeignKey(sound => sound.ModeratedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(sound => sound.Category)
                .WithMany(category => category.Sounds)
                .HasForeignKey(sound => sound.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(sound => sound.CollectionLinks)
                .WithOne(link => link.Sound)
                .HasForeignKey(link => link.SoundId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(sound => sound.DownloadEvents)
                .WithOne(download => download.Sound)
                .HasForeignKey(download => download.SoundId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommunityFollow>(entity =>
        {
            entity.HasKey(follow => follow.Id);
            entity.HasIndex(follow => new { follow.FollowerUserId, follow.FollowedUserId }).IsUnique();
        });

        modelBuilder.Entity<CommunityCollection>(entity =>
        {
            entity.HasKey(collection => collection.Id);
            entity.HasIndex(collection => collection.Slug).IsUnique();
            entity.Property(collection => collection.Name).HasMaxLength(120);
            entity.Property(collection => collection.Slug).HasMaxLength(120);
            entity.Property(collection => collection.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<CommunityCollectionSound>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.HasIndex(link => new { link.CollectionId, link.SoundId }).IsUnique();

            entity.HasOne(link => link.Collection)
                .WithMany(collection => collection.CollectionSounds)
                .HasForeignKey(link => link.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommunitySoundDownload>(entity =>
        {
            entity.HasKey(download => download.Id);
            entity.HasIndex(download => new { download.UserId, download.SoundId, download.DownloadedAtUtc });
        });
    }
}

public enum AccountTier
{
    Free = 0,
    Member = 1
}

public enum SoundModerationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public sealed class CommunityUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public bool IsEmailConfirmed { get; set; }
    public string? EmailConfirmationTokenHash { get; set; }
    public DateTime? EmailConfirmationExpiresAtUtc { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AccountTier Tier { get; set; } = AccountTier.Free;
    public string Bio { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string FavoriteGame { get; set; } = string.Empty;
    public string? AvatarStoragePath { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTime? AvatarUpdatedAtUtc { get; set; }
    public bool IsModerator { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
    public List<CommunitySound> UploadedSounds { get; set; } = new();
    public List<CommunitySound> ModeratedSounds { get; set; } = new();
    public List<CommunityFollow> FollowingRelationships { get; set; } = new();
    public List<CommunityFollow> FollowerRelationships { get; set; } = new();
    public List<CommunityCollection> Collections { get; set; } = new();
    public List<CommunitySoundDownload> SoundDownloads { get; set; } = new();
}

public sealed class SoundCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CommunitySound> Sounds { get; set; } = new();
}

public sealed class CommunitySound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long FileSizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public CommunityUser UploadedByUser { get; set; } = null!;
    public Guid CategoryId { get; set; }
    public SoundCategory Category { get; set; } = null!;
    public SoundModerationStatus ModerationStatus { get; set; } = SoundModerationStatus.Pending;
    public string ModerationNotes { get; set; } = string.Empty;
    public int DownloadCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ModeratedAtUtc { get; set; }
    public Guid? ModeratedByUserId { get; set; }
    public CommunityUser? ModeratedByUser { get; set; }
    public List<CommunityCollectionSound> CollectionLinks { get; set; } = new();
    public List<CommunitySoundDownload> DownloadEvents { get; set; } = new();
}

public sealed class CommunityFollow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FollowerUserId { get; set; }
    public CommunityUser FollowerUser { get; set; } = null!;
    public Guid FollowedUserId { get; set; }
    public CommunityUser FollowedUser { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CommunityCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public CommunityUser Owner { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<CommunityCollectionSound> CollectionSounds { get; set; } = new();
}

public sealed class CommunityCollectionSound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CollectionId { get; set; }
    public CommunityCollection Collection { get; set; } = null!;
    public Guid SoundId { get; set; }
    public CommunitySound Sound { get; set; } = null!;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CommunitySoundDownload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public CommunityUser? User { get; set; }
    public Guid SoundId { get; set; }
    public CommunitySound Sound { get; set; } = null!;
    public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
}
