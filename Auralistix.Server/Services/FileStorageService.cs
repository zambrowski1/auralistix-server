using Auralistix.Server.Options;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net;

namespace Auralistix.Server.Services;

public sealed record StoredFileInfo(
    string RelativePath,
    string OriginalFileName,
    string ContentType,
    long SizeBytes);

public sealed class FileStorageService : IDisposable
{
    private static readonly HashSet<string> AllowedSoundExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".wma"
    };

    private static readonly HashSet<string> AllowedAvatarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    private readonly CommunityOptions _communityOptions;
    private readonly S3Options _s3Options;
    private readonly bool _useS3;
    private readonly string _storageRoot;
    private readonly IAmazonS3? _s3Client;

    public FileStorageService(
        IWebHostEnvironment environment,
        IOptions<CommunityOptions> communityOptions,
        IOptions<S3Options> s3Options)
    {
        _communityOptions = communityOptions.Value;
        _s3Options = s3Options.Value;
        _useS3 = string.Equals(_communityOptions.StorageProvider, "S3", StringComparison.OrdinalIgnoreCase);
        _storageRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, _communityOptions.StorageRoot));

        if (_useS3)
        {
            ValidateS3Options(_s3Options);
            var config = new AmazonS3Config
            {
                ForcePathStyle = _s3Options.ForcePathStyle,
                AuthenticationRegion = string.IsNullOrWhiteSpace(_s3Options.Region) ? "ru-1" : _s3Options.Region
            };

            if (!string.IsNullOrWhiteSpace(_s3Options.ServiceUrl))
                config.ServiceURL = _s3Options.ServiceUrl.TrimEnd('/');
            else
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(config.AuthenticationRegion);

            _s3Client = new AmazonS3Client(
                new BasicAWSCredentials(_s3Options.AccessKey, _s3Options.SecretKey),
                config);
        }
    }

    public async Task<StoredFileInfo> SaveAvatarAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Max(1, _communityOptions.MaxAvatarUploadMegabytes) * 1024L * 1024L;
        return await SaveFileAsync(file, "avatars", AllowedAvatarExtensions, maxBytes, cancellationToken);
    }

    public async Task<StoredFileInfo> SaveSoundAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Max(1, _communityOptions.MaxSoundUploadMegabytes) * 1024L * 1024L;
        return await SaveFileAsync(file, "sounds", AllowedSoundExtensions, maxBytes, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (_useS3)
            return await OpenS3ReadAsync(relativePath, cancellationToken);

        var fullPath = ResolveFullPath(relativePath);
        return File.OpenRead(fullPath);
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (_useS3)
            return await S3ExistsAsync(relativePath, cancellationToken);

        var fullPath = ResolveFullPath(relativePath);
        return File.Exists(fullPath);
    }

    public async Task DeleteIfExistsAsync(string? relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        if (_useS3)
        {
            await DeleteS3IfExistsAsync(relativePath, cancellationToken);
            return;
        }

        var fullPath = ResolveFullPath(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private async Task<StoredFileInfo> SaveFileAsync(
        IFormFile file,
        string subfolder,
        HashSet<string> allowedExtensions,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            throw new InvalidOperationException("The uploaded file is empty.");

        if (file.Length > maxBytes)
            throw new InvalidOperationException($"The uploaded file exceeds the {maxBytes / 1024 / 1024} MB limit.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || !allowedExtensions.Contains(extension))
            throw new InvalidOperationException($"Files of type {extension} are not allowed.");

        var folderName = $"{subfolder}/{DateTime.UtcNow:yyyyMMdd}";
        var relativePath = $"{folderName}/{Guid.NewGuid():N}{extension}";
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        if (_useS3)
        {
            await SaveS3FileAsync(file, relativePath, contentType, cancellationToken);
            return new StoredFileInfo(
                relativePath,
                Path.GetFileName(file.FileName),
                contentType,
                file.Length);
        }

        var fullPath = ResolveFullPath(relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var input = file.OpenReadStream();
        await using var output = File.Create(fullPath);
        await input.CopyToAsync(output, cancellationToken);

        return new StoredFileInfo(
            relativePath.Replace('\\', '/'),
            Path.GetFileName(file.FileName),
            contentType,
            file.Length);
    }

    private async Task SaveS3FileAsync(
        IFormFile file,
        string relativePath,
        string contentType,
        CancellationToken cancellationToken)
    {
        var client = RequireS3Client();
        await using var input = file.OpenReadStream();
        var request = new PutObjectRequest
        {
            BucketName = _s3Options.BucketName,
            Key = BuildS3Key(relativePath),
            InputStream = input,
            ContentType = contentType
        };

        await client.PutObjectAsync(request, cancellationToken);
    }

    private async Task<Stream> OpenS3ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var client = RequireS3Client();

        try
        {
            using var response = await client.GetObjectAsync(
                _s3Options.BucketName,
                BuildS3Key(relativePath),
                cancellationToken);

            var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            return memory;
        }
        catch (AmazonS3Exception ex) when (IsS3NotFound(ex))
        {
            throw new FileNotFoundException("The requested file was not found in S3 storage.", relativePath, ex);
        }
    }

    private async Task<bool> S3ExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        var client = RequireS3Client();

        try
        {
            await client.GetObjectMetadataAsync(
                _s3Options.BucketName,
                BuildS3Key(relativePath),
                cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsS3NotFound(ex))
        {
            return false;
        }
    }

    private async Task DeleteS3IfExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        var client = RequireS3Client();
        await client.DeleteObjectAsync(_s3Options.BucketName, BuildS3Key(relativePath), cancellationToken);
    }

    private IAmazonS3 RequireS3Client() =>
        _s3Client ?? throw new InvalidOperationException("S3 storage is not configured.");

    private string BuildS3Key(string relativePath)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var prefix = NormalizeRelativePath(_s3Options.KeyPrefix);
        return string.IsNullOrWhiteSpace(prefix) ? normalizedPath : $"{prefix}/{normalizedPath}";
    }

    private static string NormalizeRelativePath(string value) =>
        (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim('/')
            .Replace("//", "/");

    private string ResolveFullPath(string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(_storageRoot, relativePath));
        var storageRootWithSeparator = _storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _storageRoot
            : $"{_storageRoot}{Path.DirectorySeparatorChar}";

        if (!string.Equals(candidate, _storageRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(storageRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target file path is outside of the storage root.");

        return candidate;
    }

    private static void ValidateS3Options(S3Options options)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new InvalidOperationException("Set S3__BucketName before enabling S3 storage.");

        if (string.IsNullOrWhiteSpace(options.AccessKey))
            throw new InvalidOperationException("Set S3__AccessKey before enabling S3 storage.");

        if (string.IsNullOrWhiteSpace(options.SecretKey))
            throw new InvalidOperationException("Set S3__SecretKey before enabling S3 storage.");
    }

    private static bool IsS3NotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _s3Client?.Dispose();
    }
}
