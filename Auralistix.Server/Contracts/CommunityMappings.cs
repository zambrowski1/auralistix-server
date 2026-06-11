using Auralistix.Server.Data;

namespace Auralistix.Server.Contracts;

public static class CommunityMappings
{
    public static CommunityUserDto ToDto(
        this CommunityUser user,
        string baseUrl,
        int followerCount,
        int followingCount,
        int featuredCollectionCount,
        int achievementCount,
        int approvedUploadCount,
        int totalDownloads)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var avatarUrl = string.IsNullOrWhiteSpace(user.AvatarStoragePath)
            ? null
            : $"{normalizedBaseUrl}/api/community/users/{user.Id}/avatar?v={user.AvatarUpdatedAtUtc?.Ticks ?? 0}";

        return new CommunityUserDto(
            user.Id,
            user.Email,
            user.IsEmailConfirmed,
            user.DisplayName,
            user.Username,
            user.Tier,
            user.Bio,
            user.Country,
            user.FavoriteGame,
            avatarUrl,
            user.IsModerator,
            user.CreatedAtUtc,
            followerCount,
            followingCount,
            featuredCollectionCount,
            achievementCount,
            approvedUploadCount,
            totalDownloads);
    }

    public static ModerationUserDto ToModerationDto(this CommunityUser user, int uploadCount)
    {
        return new ModerationUserDto(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Username,
            user.Tier,
            user.IsEmailConfirmed,
            user.IsModerator,
            user.CreatedAtUtc,
            uploadCount);
    }

    public static CategoryDto ToDto(this SoundCategory category, int approvedSoundCount)
    {
        return new CategoryDto(
            category.Id,
            category.Name,
            category.Slug,
            category.Description,
            approvedSoundCount);
    }

    public static CommunitySoundDto ToDto(
        this CommunitySound sound,
        string baseUrl,
        Guid? currentUserId,
        bool isModerator = false,
        string? recommendationReasonKey = null)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var uploaderAvatarUrl = string.IsNullOrWhiteSpace(sound.UploadedByUser.AvatarStoragePath)
            ? null
            : $"{normalizedBaseUrl}/api/community/users/{sound.UploadedByUserId}/avatar?v={sound.UploadedByUser.AvatarUpdatedAtUtc?.Ticks ?? 0}";

        var canDownload = sound.ModerationStatus == SoundModerationStatus.Approved
            || currentUserId == sound.UploadedByUserId
            || isModerator;

        return new CommunitySoundDto(
            sound.Id,
            sound.Title,
            sound.Description,
            sound.OriginalFileName,
            sound.FileSizeBytes,
            sound.CategoryId,
            sound.Category.Name,
            sound.UploadedByUserId,
            sound.UploadedByUser.DisplayName,
            sound.UploadedByUser.Username,
            sound.UploadedByUser.Tier,
            uploaderAvatarUrl,
            sound.ModerationStatus,
            sound.ModerationNotes,
            sound.DownloadCount,
            sound.CreatedAtUtc,
            sound.ModeratedAtUtc,
            canDownload,
            currentUserId == sound.UploadedByUserId,
            $"{normalizedBaseUrl}/api/community/sounds/{sound.Id}/download",
            recommendationReasonKey);
    }

    public static PublicProfileDto ToPublicProfileDto(
        this CommunityUser user,
        string baseUrl,
        int followerCount,
        int followingCount,
        int approvedUploadCount,
        int totalDownloads,
        bool isFollowedByCurrentUser,
        List<CommunityAchievementDto> achievements,
        List<CommunityCollectionDto> collections,
        List<CommunitySoundDto> uploads)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var avatarUrl = string.IsNullOrWhiteSpace(user.AvatarStoragePath)
            ? null
            : $"{normalizedBaseUrl}/api/community/users/{user.Id}/avatar?v={user.AvatarUpdatedAtUtc?.Ticks ?? 0}";

        return new PublicProfileDto(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Tier,
            user.Bio,
            user.Country,
            user.FavoriteGame,
            avatarUrl,
            user.CreatedAtUtc,
            followerCount,
            followingCount,
            approvedUploadCount,
            totalDownloads,
            isFollowedByCurrentUser,
            achievements,
            collections,
            uploads);
    }

    public static CommunityCollectionDto ToDto(this CommunityCollection collection, string baseUrl, Guid? currentUserId, bool isModerator = false)
    {
        var sounds = collection.CollectionSounds
            .OrderBy(link => link.SortOrder)
            .ThenBy(link => link.CreatedAtUtc)
            .Where(link =>
                link.Sound.ModerationStatus == SoundModerationStatus.Approved
                || (currentUserId.HasValue && link.Sound.UploadedByUserId == currentUserId.Value)
                || isModerator)
            .Select(link => link.Sound.ToDto(baseUrl, currentUserId, isModerator))
            .ToList();

        return new CommunityCollectionDto(
            collection.Id,
            collection.Name,
            collection.Slug,
            collection.Description,
            collection.OwnerUserId,
            collection.Owner.DisplayName,
            collection.Owner.Username,
            collection.Owner.Tier,
            collection.CreatedAtUtc,
            sounds.Count,
            sounds.Sum(sound => sound.DownloadCount),
            sounds);
    }
}
