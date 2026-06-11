namespace Auralistix.Server.Options;

public sealed class S3Options
{
    public const string SectionName = "S3";

    public string ServiceUrl { get; set; } = "https://s3.twcstorage.ru";
    public string Region { get; set; } = "ru-1";
    public string BucketName { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "community";
    public bool ForcePathStyle { get; set; } = true;
}
