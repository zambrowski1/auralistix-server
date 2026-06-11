using Auralistix.Server.Data;

namespace Auralistix.Server.Contracts;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName, string? Username);

public sealed record LoginRequest(string Email, string Password);

public sealed record ResendConfirmationRequest(string Email);

public sealed record RegisterResponse(
    string Message,
    bool RequiresEmailConfirmation,
    string? DeliveryMode,
    string? DevelopmentConfirmationLink);

public sealed record AuthResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    CommunityUserDto User);

public sealed record CommunityUserDto(
    Guid Id,
    string Email,
    bool IsEmailConfirmed,
    string DisplayName,
    string Username,
    AccountTier Tier,
    string Bio,
    string Country,
    string FavoriteGame,
    string? AvatarUrl,
    bool IsModerator,
    DateTime CreatedAtUtc,
    int FollowerCount,
    int FollowingCount,
    int FeaturedCollectionCount,
    int AchievementCount,
    int ApprovedUploadCount,
    int TotalDownloads);

public sealed record ModerationUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Username,
    AccountTier Tier,
    bool IsEmailConfirmed,
    bool IsModerator,
    DateTime CreatedAtUtc,
    int UploadCount);

public sealed record UpdateProfileRequest(
    string? DisplayName,
    string? Username,
    string? Bio,
    string? Country,
    string? FavoriteGame);

public sealed record CategoryDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    int ApprovedSoundCount);

public sealed record CreateCategoryRequest(
    string Name,
    string? Description);

public sealed record CommunitySoundDto(
    Guid Id,
    string Title,
    string Description,
    string OriginalFileName,
    long FileSizeBytes,
    Guid CategoryId,
    string CategoryName,
    Guid UploadedByUserId,
    string UploadedByDisplayName,
    string UploadedByUsername,
    AccountTier UploadedByTier,
    string? UploadedByAvatarUrl,
    SoundModerationStatus ModerationStatus,
    string ModerationNotes,
    int DownloadCount,
    DateTime CreatedAtUtc,
    DateTime? ModeratedAtUtc,
    bool CanDownload,
    bool IsOwnedByCurrentUser,
    string DownloadUrl,
    string? RecommendationReasonKey);

public sealed record CommunityAchievementDto(
    string Key,
    string TitleKey,
    string DescriptionKey,
    string AccentHex);

public sealed record CommunityCollectionDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    Guid OwnerUserId,
    string OwnerDisplayName,
    string OwnerUsername,
    AccountTier OwnerTier,
    DateTime CreatedAtUtc,
    int SoundCount,
    int TotalDownloads,
    List<CommunitySoundDto> Sounds);

public sealed record PublicProfileDto(
    Guid Id,
    string Username,
    string DisplayName,
    AccountTier Tier,
    string Bio,
    string Country,
    string FavoriteGame,
    string? AvatarUrl,
    DateTime CreatedAtUtc,
    int FollowerCount,
    int FollowingCount,
    int ApprovedUploadCount,
    int TotalDownloads,
    bool IsFollowedByCurrentUser,
    List<CommunityAchievementDto> Achievements,
    List<CommunityCollectionDto> Collections,
    List<CommunitySoundDto> Uploads);

public sealed record ModerateSoundRequest(string? Notes);

public sealed record SetAccountTierRequest(AccountTier Tier);

public sealed record CreateCollectionRequest(
    string Name,
    string? Description,
    List<Guid>? SoundIds);

public sealed record FollowStateDto(
    bool IsFollowing,
    int FollowerCount,
    int FollowingCount);

public sealed record OperationResponse(string Message);
