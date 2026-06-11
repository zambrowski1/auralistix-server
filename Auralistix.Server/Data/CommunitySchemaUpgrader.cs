using System.Data;
using Auralistix.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Auralistix.Server.Data;

public static class CommunitySchemaUpgrader
{
    public static async Task EnsureLatestSchemaAsync(CommunityDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!IsSqliteDatabase(db))
        {
            await NormalizeExistingUsernamesAsync(db, cancellationToken);
            return;
        }

        await EnsureUserColumnsAsync(db, cancellationToken);
        await EnsureSocialTablesAsync(db, cancellationToken);
        await NormalizeExistingUsernamesAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users(Username);",
            cancellationToken);
    }

    private static bool IsSqliteDatabase(CommunityDbContext db) =>
        db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task EnsureUserColumnsAsync(CommunityDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('Users');";

        var hasUsernameColumn = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "Username", StringComparison.OrdinalIgnoreCase))
            {
                hasUsernameColumn = true;
                break;
            }
        }

        if (hasUsernameColumn)
            return;

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE Users ADD COLUMN Username TEXT NOT NULL DEFAULT '';",
            cancellationToken);
    }

    private static async Task NormalizeExistingUsernamesAsync(CommunityDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users
            .OrderBy(user => user.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var usedUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasChanges = false;

        foreach (var user in users)
        {
            var current = CommunityUsername.Normalize(user.Username);
            if (current.Length < CommunityUsername.MinLength || usedUsernames.Contains(current))
            {
                var seed = CommunityUsername.BuildSeed(user.Username, user.DisplayName, user.Email);
                current = BuildUniqueUsername(seed, usedUsernames);
            }

            if (!string.Equals(user.Username, current, StringComparison.Ordinal))
            {
                user.Username = current;
                hasChanges = true;
            }

            usedUsernames.Add(current);
        }

        if (hasChanges)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildUniqueUsername(string seed, HashSet<string> usedUsernames)
    {
        var index = 1;
        while (true)
        {
            var candidate = CommunityUsername.WithSuffix(seed, index);
            if (candidate.Length >= CommunityUsername.MinLength && usedUsernames.Add(candidate))
                return candidate;

            index++;
        }
    }

    private static async Task EnsureSocialTablesAsync(CommunityDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Follows (
                Id TEXT NOT NULL CONSTRAINT PK_Follows PRIMARY KEY,
                FollowerUserId TEXT NOT NULL,
                FollowedUserId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_Follows_Users_FollowerUserId FOREIGN KEY (FollowerUserId) REFERENCES Users (Id) ON DELETE CASCADE,
                CONSTRAINT FK_Follows_Users_FollowedUserId FOREIGN KEY (FollowedUserId) REFERENCES Users (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Collections (
                Id TEXT NOT NULL CONSTRAINT PK_Collections PRIMARY KEY,
                OwnerUserId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Slug TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_Collections_Users_OwnerUserId FOREIGN KEY (OwnerUserId) REFERENCES Users (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS CollectionSounds (
                Id TEXT NOT NULL CONSTRAINT PK_CollectionSounds PRIMARY KEY,
                CollectionId TEXT NOT NULL,
                SoundId TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_CollectionSounds_Collections_CollectionId FOREIGN KEY (CollectionId) REFERENCES Collections (Id) ON DELETE CASCADE,
                CONSTRAINT FK_CollectionSounds_Sounds_SoundId FOREIGN KEY (SoundId) REFERENCES Sounds (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS SoundDownloads (
                Id TEXT NOT NULL CONSTRAINT PK_SoundDownloads PRIMARY KEY,
                UserId TEXT NULL,
                SoundId TEXT NOT NULL,
                DownloadedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_SoundDownloads_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE SET NULL,
                CONSTRAINT FK_SoundDownloads_Sounds_SoundId FOREIGN KEY (SoundId) REFERENCES Sounds (Id) ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Follows_FollowerUserId_FollowedUserId ON Follows(FollowerUserId, FollowedUserId);",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Collections_Slug ON Collections(Slug);",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CollectionSounds_CollectionId_SoundId ON CollectionSounds(CollectionId, SoundId);",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_SoundDownloads_UserId_SoundId_DownloadedAtUtc ON SoundDownloads(UserId, SoundId, DownloadedAtUtc);",
            cancellationToken);
    }
}
