namespace Auralistix.Server.Options;

public sealed class CommunityOptions
{
    public const string SectionName = "Community";

    public string PublicBaseUrl { get; set; } = "http://localhost:5188";
    public string StorageProvider { get; set; } = "Local";
    public string StorageRoot { get; set; } = "Data\\Storage";
    public List<string> ModeratorEmails { get; set; } = new();
    public int MaxSoundUploadMegabytes { get; set; } = 25;
    public int MaxAvatarUploadMegabytes { get; set; } = 4;
}
