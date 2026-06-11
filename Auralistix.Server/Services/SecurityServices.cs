using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Auralistix.Server.Data;
using Auralistix.Server.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auralistix.Server.Services;

public sealed class PasswordHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int IterationCount = 100_000;

    public (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, IterationCount, HashAlgorithmName.SHA256, HashLength);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string password, string hashBase64, string saltBase64)
    {
        var expectedHash = Convert.FromBase64String(hashBase64);
        var salt = Convert.FromBase64String(saltBase64);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, IterationCount, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

public sealed class TokenDigestService
{
    public string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}

public sealed class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) CreateToken(CommunityUser user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddDays(Math.Max(1, _options.ExpirationDays));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new("tier", user.Tier.ToString()),
            new("email_confirmed", user.IsEmailConfirmed.ToString())
        };

        if (user.IsModerator)
            claims.Add(new Claim(ClaimTypes.Role, "Moderator"));

        var credentials = new SigningCredentials(CreateSecurityKey(_options.SigningKey), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(token), expiresAtUtc);
    }

    public static SymmetricSecurityKey CreateSecurityKey(string? signingKey)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(signingKey)
            ? "Auralistix.Local.Development.Signing.Key.2026.Change.Me"
            : signingKey.Trim();

        if (normalizedKey.Length < 32)
            normalizedKey = normalizedKey.PadRight(32, '_');

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(normalizedKey));
    }
}
