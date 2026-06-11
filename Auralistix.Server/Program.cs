using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Auralistix.Server.Contracts;
using Auralistix.Server.Data;
using Auralistix.Server.Options;
using Auralistix.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddDebug();

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var communityConnectionString = ResolveCommunityConnectionString(
    builder.Configuration,
    builder.Environment,
    databaseOptions);

ValidateProductionSecrets(builder.Configuration, builder.Environment);

builder.WebHost.UseUrls(ResolveServerUrl(builder.Configuration["Server:Url"]));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<CommunityOptions>(builder.Configuration.GetSection(CommunityOptions.SectionName));
builder.Services.Configure<S3Options>(builder.Configuration.GetSection(S3Options.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 35L * 1024L * 1024L;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<CommunityDbContext>(options =>
{
    if (IsPostgresDatabaseProvider(databaseOptions.Provider))
        options.UseNpgsql(communityConnectionString);
    else
        options.UseSqlite(communityConnectionString);
});

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenDigestService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddSingleton<IEmailSender, EmailSender>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.CreateSecurityKey(jwtOptions.SigningKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ModeratorOnly", policy => policy.RequireRole("Moderator"));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

LogProductionConfigurationWarnings(app.Configuration, app.Environment, app.Logger);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CommunityDbContext>();
    await CommunitySchemaUpgrader.EnsureLatestSchemaAsync(db);
    await SeedCategoriesAsync(db);
}

app.UseForwardedHeaders();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/swagger"));

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    utc = DateTime.UtcNow
}));

var auth = api.MapGroup("/auth");

auth.MapPost("/register", async (
    RegisterRequest request,
    HttpContext httpContext,
    CommunityDbContext db,
    PasswordHasher passwordHasher,
    TokenDigestService tokenDigestService,
    IEmailSender emailSender,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var normalizedEmail = NormalizeEmail(request.Email);
    if (normalizedEmail is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Enter a valid email address.");

    if (!ValidatePassword(request.Password, out var passwordError))
        return ApiMessage(StatusCodes.Status400BadRequest, passwordError);

    var exists = await db.Users.AnyAsync(user => user.Email == normalizedEmail, cancellationToken);
    if (exists)
        return ApiMessage(StatusCodes.Status400BadRequest, "An account with this email already exists.");

    var displayName = BuildDisplayName(request.DisplayName, normalizedEmail);
    var usernameResult = await ResolveUsernameAsync(
        db,
        request.Username,
        displayName,
        normalizedEmail,
        null,
        cancellationToken);

    if (!usernameResult.IsValid)
        return ApiMessage(StatusCodes.Status400BadRequest, usernameResult.ErrorMessage);

    var (hash, salt) = passwordHasher.HashPassword(request.Password);
    var confirmationToken = GenerateSecureToken();
    var user = new CommunityUser
    {
        Email = normalizedEmail,
        PasswordHash = hash,
        PasswordSalt = salt,
        DisplayName = displayName,
        Username = usernameResult.Username,
        Tier = AccountTier.Free,
        IsModerator = IsModeratorEmail(normalizedEmail, communityOptions.Value.ModeratorEmails),
        EmailConfirmationTokenHash = tokenDigestService.HashToken(confirmationToken),
        EmailConfirmationExpiresAtUtc = DateTime.UtcNow.AddHours(24)
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(cancellationToken);

    var confirmationUrl = BuildConfirmationUrl(httpContext, communityOptions.Value, confirmationToken);
    var dispatchInfo = await emailSender.SendConfirmationEmailAsync(
        user.Email,
        user.DisplayName,
        confirmationUrl,
        cancellationToken);

    return Results.Ok(new RegisterResponse(
        "Registration completed. Check your inbox and confirm your email before signing in.",
        true,
        dispatchInfo.Mode,
        dispatchInfo.Mode == "file-preview" ? confirmationUrl : null));
});

auth.MapPost("/resend-confirmation", async (
    ResendConfirmationRequest request,
    HttpContext httpContext,
    CommunityDbContext db,
    TokenDigestService tokenDigestService,
    IEmailSender emailSender,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var normalizedEmail = NormalizeEmail(request.Email);
    if (normalizedEmail is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Enter a valid email address.");

    var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Email == normalizedEmail, cancellationToken);
    if (user is null)
        return Results.Ok(new OperationResponse("If the email exists, a confirmation message has been sent."));

    if (user.IsEmailConfirmed)
        return Results.Ok(new OperationResponse("This email has already been confirmed."));

    var token = GenerateSecureToken();
    user.EmailConfirmationTokenHash = tokenDigestService.HashToken(token);
    user.EmailConfirmationExpiresAtUtc = DateTime.UtcNow.AddHours(24);
    user.IsModerator = IsModeratorEmail(user.Email, communityOptions.Value.ModeratorEmails);

    await db.SaveChangesAsync(cancellationToken);

    var confirmationUrl = BuildConfirmationUrl(httpContext, communityOptions.Value, token);
    var dispatchInfo = await emailSender.SendConfirmationEmailAsync(
        user.Email,
        user.DisplayName,
        confirmationUrl,
        cancellationToken);

    return Results.Ok(new RegisterResponse(
        "A new confirmation email has been generated.",
        true,
        dispatchInfo.Mode,
        dispatchInfo.Mode == "file-preview" ? confirmationUrl : null));
});

auth.MapPost("/login", async (
    LoginRequest request,
    HttpContext httpContext,
    CommunityDbContext db,
    PasswordHasher passwordHasher,
    JwtTokenService jwtTokenService,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var normalizedEmail = NormalizeEmail(request.Email);
    if (normalizedEmail is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Enter a valid email address.");

    var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Email == normalizedEmail, cancellationToken);
    if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash, user.PasswordSalt))
        return ApiMessage(StatusCodes.Status401Unauthorized, "Invalid email or password.");

    if (!user.IsEmailConfirmed)
        return ApiMessage(StatusCodes.Status403Forbidden, "Confirm your email before signing in.");

    user.IsModerator = IsModeratorEmail(user.Email, communityOptions.Value.ModeratorEmails);
    user.LastLoginAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    var (token, expiresAtUtc) = jwtTokenService.CreateToken(user);
    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);

    return Results.Ok(new AuthResponse(
        token,
        expiresAtUtc,
        await BuildUserDtoAsync(db, user, baseUrl, cancellationToken)));
});

auth.MapGet("/confirm-email", async (
    string token,
    CommunityDbContext db,
    TokenDigestService tokenDigestService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(token))
        return Results.Content(BuildConfirmationHtml("Confirmation link is invalid.", false), "text/html; charset=utf-8");

    var hashedToken = tokenDigestService.HashToken(token);
    var user = await db.Users.FirstOrDefaultAsync(
        candidate => candidate.EmailConfirmationTokenHash == hashedToken,
        cancellationToken);

    if (user is null || user.EmailConfirmationExpiresAtUtc is null || user.EmailConfirmationExpiresAtUtc < DateTime.UtcNow)
        return Results.Content(BuildConfirmationHtml("This confirmation link has expired or is invalid.", false), "text/html; charset=utf-8");

    user.IsEmailConfirmed = true;
    user.EmailConfirmationTokenHash = null;
    user.EmailConfirmationExpiresAtUtc = null;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Content(BuildConfirmationHtml("Email confirmed. You can now return to the Auralistix app and sign in.", true), "text/html; charset=utf-8");
});

var account = api.MapGroup("/account").RequireAuthorization();

account.MapGet("/me", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    return Results.Ok(await BuildUserDtoAsync(
        db,
        user,
        ResolveBaseUrl(httpContext, communityOptions.Value),
        cancellationToken));
});

account.MapPut("/me", async (
    UpdateProfileRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var displayName = BuildDisplayName(request.DisplayName, user.Email);
    var usernameResult = await ResolveUsernameAsync(
        db,
        request.Username,
        displayName,
        user.Email,
        user.Id,
        cancellationToken);

    if (!usernameResult.IsValid)
        return ApiMessage(StatusCodes.Status400BadRequest, usernameResult.ErrorMessage);

    user.DisplayName = displayName;
    user.Username = usernameResult.Username;
    user.Bio = SanitizeText(request.Bio, 280);
    user.Country = SanitizeText(request.Country, 80);
    user.FavoriteGame = SanitizeText(request.FavoriteGame, 120);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(await BuildUserDtoAsync(
        db,
        user,
        ResolveBaseUrl(httpContext, communityOptions.Value),
        cancellationToken));
});

account.MapPost("/avatar", async (
    HttpRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    FileStorageService fileStorageService,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Select an avatar image to upload.");

    StoredFileInfo storedFile;
    try
    {
        storedFile = await fileStorageService.SaveAvatarAsync(file, cancellationToken);
    }
    catch (InvalidOperationException ex)
    {
        return ApiMessage(StatusCodes.Status400BadRequest, ex.Message);
    }

    await fileStorageService.DeleteIfExistsAsync(user.AvatarStoragePath, cancellationToken);
    user.AvatarStoragePath = storedFile.RelativePath;
    user.AvatarContentType = storedFile.ContentType;
    user.AvatarUpdatedAtUtc = DateTime.UtcNow;

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(await BuildUserDtoAsync(
        db,
        user,
        ResolveBaseUrl(httpContext, communityOptions.Value),
        cancellationToken));
});

var community = api.MapGroup("/community");

community.MapGet("/categories", async (CommunityDbContext db, CancellationToken cancellationToken) =>
{
    var categories = await db.Categories
        .AsNoTracking()
        .OrderBy(category => category.Name)
        .Select(category => new CategoryDto(
            category.Id,
            category.Name,
            category.Slug,
            category.Description,
            category.Sounds.Count(sound => sound.ModerationStatus == SoundModerationStatus.Approved)))
        .ToListAsync(cancellationToken);

    return Results.Ok(categories);
});

community.MapGet("/sounds", async (
    string? q,
    Guid? categoryId,
    string? sort,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var currentUserId = GetCurrentUserId(principal);
    var isModerator = principal.IsInRole("Moderator");

    var soundsQuery = db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .AsQueryable();

    if (!isModerator)
    {
        soundsQuery = soundsQuery.Where(sound =>
            sound.ModerationStatus == SoundModerationStatus.Approved
            || (currentUserId.HasValue && sound.UploadedByUserId == currentUserId.Value));
    }

    if (categoryId.HasValue)
        soundsQuery = soundsQuery.Where(sound => sound.CategoryId == categoryId.Value);

    if (!string.IsNullOrWhiteSpace(q))
    {
        var search = q.Trim();
        soundsQuery = soundsQuery.Where(sound =>
            sound.Title.Contains(search)
            || sound.Description.Contains(search)
            || sound.Category.Name.Contains(search)
            || sound.UploadedByUser.DisplayName.Contains(search)
            || sound.UploadedByUser.Username.Contains(search));
    }

    soundsQuery = (sort ?? "latest").Trim().ToLowerInvariant() switch
    {
        "top" => soundsQuery.OrderByDescending(sound => sound.DownloadCount).ThenByDescending(sound => sound.CreatedAtUtc),
        _ => soundsQuery.OrderByDescending(sound => sound.CreatedAtUtc)
    };

    var sounds = await soundsQuery.Take(100).ToListAsync(cancellationToken);
    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);

    return Results.Ok(sounds.Select(sound => sound.ToDto(baseUrl, currentUserId, isModerator)).ToList());
});

community.MapGet("/feed/following", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var followedUserIds = await db.Follows
        .AsNoTracking()
        .Where(follow => follow.FollowerUserId == user.Id)
        .Select(follow => follow.FollowedUserId)
        .ToListAsync(cancellationToken);

    if (followedUserIds.Count == 0)
        return Results.Ok(new List<CommunitySoundDto>());

    var sounds = await db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound =>
            sound.ModerationStatus == SoundModerationStatus.Approved
            && followedUserIds.Contains(sound.UploadedByUserId))
        .OrderByDescending(sound => sound.CreatedAtUtc)
        .Take(32)
        .ToListAsync(cancellationToken);

    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);
    return Results.Ok(sounds.Select(sound => sound.ToDto(baseUrl, user.Id, user.IsModerator)).ToList());
}).RequireAuthorization();

community.MapGet("/recommendations", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var currentUserId = GetCurrentUserId(principal);
    var currentUser = currentUserId.HasValue
        ? await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == currentUserId.Value, cancellationToken)
        : null;

    var followedCreatorIds = currentUserId.HasValue
        ? await db.Follows
            .AsNoTracking()
            .Where(follow => follow.FollowerUserId == currentUserId.Value)
            .Select(follow => follow.FollowedUserId)
            .ToListAsync(cancellationToken)
        : [];

    var familiarCategories = currentUserId.HasValue
        ? await db.SoundDownloads
            .AsNoTracking()
            .Where(download => download.UserId == currentUserId.Value)
            .Join(
                db.Sounds.AsNoTracking(),
                download => download.SoundId,
                sound => sound.Id,
                (download, sound) => new { sound.CategoryId })
            .GroupBy(item => item.CategoryId)
            .Select(group => new { CategoryId = group.Key, Score = group.Count() })
            .ToDictionaryAsync(item => item.CategoryId, item => item.Score, cancellationToken)
        : new Dictionary<Guid, int>();

    var downloadedSoundIds = currentUserId.HasValue
        ? await db.SoundDownloads
            .AsNoTracking()
            .Where(download => download.UserId == currentUserId.Value)
            .Select(download => download.SoundId)
            .Distinct()
            .ToListAsync(cancellationToken)
        : [];

    var favoriteGame = SanitizeText(currentUser?.FavoriteGame, 120);
    var sounds = await db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound =>
            sound.ModerationStatus == SoundModerationStatus.Approved
            && (!currentUserId.HasValue || sound.UploadedByUserId != currentUserId.Value))
        .OrderByDescending(sound => sound.CreatedAtUtc)
        .Take(180)
        .ToListAsync(cancellationToken);

    var scoredSounds = sounds
        .Where(sound => !downloadedSoundIds.Contains(sound.Id))
        .Select(sound =>
        {
            var reasonKey = "popular_pick";
            var score = sound.DownloadCount * 3;

            if (followedCreatorIds.Contains(sound.UploadedByUserId))
            {
                score += 160;
                reasonKey = "followed_creator";
            }
            else if (familiarCategories.TryGetValue(sound.CategoryId, out var categoryScore))
            {
                score += 110 + categoryScore * 12;
                reasonKey = "familiar_category";
            }
            else if (!string.IsNullOrWhiteSpace(favoriteGame)
                     && (ContainsIgnoreCase(sound.UploadedByUser.FavoriteGame, favoriteGame)
                         || ContainsIgnoreCase(sound.Category.Name, favoriteGame)
                         || ContainsIgnoreCase(sound.Title, favoriteGame)
                         || ContainsIgnoreCase(sound.Description, favoriteGame)))
            {
                score += 95;
                reasonKey = "favorite_game_match";
            }
            else if (sound.CreatedAtUtc >= DateTime.UtcNow.AddDays(-5))
            {
                score += 45;
                reasonKey = "fresh_pick";
            }

            score += Math.Max(0, 20 - (int)(DateTime.UtcNow - sound.CreatedAtUtc).TotalDays);
            return new { Sound = sound, Score = score, ReasonKey = reasonKey };
        })
        .OrderByDescending(item => item.Score)
        .ThenByDescending(item => item.Sound.DownloadCount)
        .ThenByDescending(item => item.Sound.CreatedAtUtc)
        .Take(18)
        .ToList();

    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);
    return Results.Ok(scoredSounds
        .Select(item => item.Sound.ToDto(baseUrl, currentUserId, currentUser?.IsModerator == true, item.ReasonKey))
        .ToList());
});

community.MapGet("/collections", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var currentUserId = GetCurrentUserId(principal);
    var isModerator = principal.IsInRole("Moderator");
    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);
    var collections = await LoadCollectionsAsync(db, null, baseUrl, currentUserId, isModerator, 18, cancellationToken);

    return Results.Ok(collections
        .OrderByDescending(collection => collection.TotalDownloads)
        .ThenByDescending(collection => collection.CreatedAtUtc)
        .ToList());
});

community.MapGet("/my-collections", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    return Results.Ok(await LoadCollectionsAsync(
        db,
        user.Id,
        ResolveBaseUrl(httpContext, communityOptions.Value),
        user.Id,
        user.IsModerator,
        24,
        cancellationToken));
}).RequireAuthorization();

community.MapPost("/collections", async (
    CreateCollectionRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var name = SanitizeText(request.Name, 120);
    if (string.IsNullOrWhiteSpace(name))
        return ApiMessage(StatusCodes.Status400BadRequest, "Enter a collection name.");

    var requestedSoundIds = (request.SoundIds ?? [])
        .Distinct()
        .Take(24)
        .ToList();

    if (requestedSoundIds.Count == 0)
        return ApiMessage(StatusCodes.Status400BadRequest, "Choose at least one approved sound for the collection.");

    var ownedSounds = await db.Sounds
        .Where(sound =>
            requestedSoundIds.Contains(sound.Id)
            && sound.UploadedByUserId == user.Id
            && sound.ModerationStatus == SoundModerationStatus.Approved)
        .Select(sound => sound.Id)
        .ToListAsync(cancellationToken);

    if (ownedSounds.Count != requestedSoundIds.Count)
        return ApiMessage(StatusCodes.Status400BadRequest, "Collections can only include your approved sounds.");

    var collection = new CommunityCollection
    {
        OwnerUserId = user.Id,
        Owner = user,
        Name = name,
        Slug = await BuildUniqueCollectionSlugAsync(db, user.Username, name, cancellationToken),
        Description = SanitizeText(request.Description, 500)
    };

    db.Collections.Add(collection);

    for (var index = 0; index < requestedSoundIds.Count; index++)
    {
        collection.CollectionSounds.Add(new CommunityCollectionSound
        {
            SoundId = requestedSoundIds[index],
            SortOrder = index
        });
    }

    await db.SaveChangesAsync(cancellationToken);

    var createdCollection = await db.Collections
        .AsNoTracking()
        .Include(item => item.Owner)
        .Include(item => item.CollectionSounds)
            .ThenInclude(link => link.Sound)
                .ThenInclude(sound => sound.Category)
        .Include(item => item.CollectionSounds)
            .ThenInclude(link => link.Sound)
                .ThenInclude(sound => sound.UploadedByUser)
        .FirstAsync(item => item.Id == collection.Id, cancellationToken);

    return Results.Ok(createdCollection.ToDto(
        ResolveBaseUrl(httpContext, communityOptions.Value),
        user.Id,
        user.IsModerator));
}).RequireAuthorization();

community.MapGet("/sounds/top", async (
    HttpContext httpContext,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var baseUrl = ResolveBaseUrl(httpContext, communityOptions.Value);
    var sounds = await db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound => sound.ModerationStatus == SoundModerationStatus.Approved)
        .OrderByDescending(sound => sound.DownloadCount)
        .ThenByDescending(sound => sound.CreatedAtUtc)
        .Take(20)
        .ToListAsync(cancellationToken);

    return Results.Ok(sounds.Select(sound => sound.ToDto(baseUrl, null)).ToList());
});

community.MapGet("/my-sounds", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var sounds = await db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound => sound.UploadedByUserId == user.Id)
        .OrderByDescending(sound => sound.CreatedAtUtc)
        .ToListAsync(cancellationToken);

    return Results.Ok(sounds.Select(sound => sound.ToDto(ResolveBaseUrl(httpContext, communityOptions.Value), user.Id, user.IsModerator)).ToList());
}).RequireAuthorization();

community.MapPost("/sounds/upload", async (
    HttpRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    FileStorageService fileStorageService,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    if (!user.IsEmailConfirmed)
        return ApiMessage(StatusCodes.Status403Forbidden, "Confirm your email before uploading sounds.");

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Select an audio file to upload.");

    if (!Guid.TryParse(form["categoryId"], out var categoryId))
        return ApiMessage(StatusCodes.Status400BadRequest, "Select a valid category.");

    var category = await db.Categories.FirstOrDefaultAsync(item => item.Id == categoryId, cancellationToken);
    if (category is null)
        return ApiMessage(StatusCodes.Status400BadRequest, "Selected category was not found.");

    StoredFileInfo storedFile;
    try
    {
        storedFile = await fileStorageService.SaveSoundAsync(file, cancellationToken);
    }
    catch (InvalidOperationException ex)
    {
        return ApiMessage(StatusCodes.Status400BadRequest, ex.Message);
    }

    var title = SanitizeText(form["title"], 120);
    if (string.IsNullOrWhiteSpace(title))
        title = Path.GetFileNameWithoutExtension(file.FileName);

    var sound = new CommunitySound
    {
        Title = title,
        Description = SanitizeText(form["description"], 500),
        OriginalFileName = storedFile.OriginalFileName,
        StoredFilePath = storedFile.RelativePath,
        ContentType = storedFile.ContentType,
        FileSizeBytes = storedFile.SizeBytes,
        UploadedByUserId = user.Id,
        UploadedByUser = user,
        CategoryId = category.Id,
        Category = category,
        ModerationStatus = SoundModerationStatus.Pending
    };

    db.Sounds.Add(sound);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(sound.ToDto(ResolveBaseUrl(httpContext, communityOptions.Value), user.Id, user.IsModerator));
}).RequireAuthorization();

community.MapGet("/sounds/{soundId:guid}/download", async (
    Guid soundId,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    FileStorageService fileStorageService,
    CancellationToken cancellationToken) =>
{
    var currentUserId = GetCurrentUserId(principal);
    var isModerator = principal.IsInRole("Moderator");

    var sound = await db.Sounds.FirstOrDefaultAsync(item => item.Id == soundId, cancellationToken);
    if (sound is null)
        return Results.NotFound();

    var canDownload = sound.ModerationStatus == SoundModerationStatus.Approved
        || (currentUserId.HasValue && sound.UploadedByUserId == currentUserId.Value)
        || isModerator;

    if (!canDownload)
        return Results.NotFound();

    if (!await fileStorageService.ExistsAsync(sound.StoredFilePath, cancellationToken))
        return ApiMessage(StatusCodes.Status404NotFound, "The file is no longer available on the server.");

    sound.DownloadCount += 1;
    if (currentUserId.HasValue)
    {
        db.SoundDownloads.Add(new CommunitySoundDownload
        {
            UserId = currentUserId.Value,
            SoundId = sound.Id
        });
    }

    await db.SaveChangesAsync(cancellationToken);

    return Results.File(
        await fileStorageService.OpenReadAsync(sound.StoredFilePath, cancellationToken),
        sound.ContentType,
        sound.OriginalFileName,
        enableRangeProcessing: true);
});

community.MapGet("/users/{userId:guid}/avatar", async (
    Guid userId,
    CommunityDbContext db,
    FileStorageService fileStorageService,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

    if (user is null
        || string.IsNullOrWhiteSpace(user.AvatarStoragePath)
        || !await fileStorageService.ExistsAsync(user.AvatarStoragePath, cancellationToken))
        return Results.NotFound();

    return Results.File(
        await fileStorageService.OpenReadAsync(user.AvatarStoragePath, cancellationToken),
        user.AvatarContentType ?? "application/octet-stream",
        enableRangeProcessing: true);
});

community.MapGet("/profiles/{username}", async (
    string username,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var normalizedUsername = CommunityUsername.Normalize(username);
    if (normalizedUsername.Length < CommunityUsername.MinLength)
        return Results.NotFound();

    var currentUserId = GetCurrentUserId(principal);
    var isModerator = principal.IsInRole("Moderator");
    var user = await db.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Username == normalizedUsername, cancellationToken);

    if (user is null)
        return Results.NotFound();

    return Results.Ok(await BuildPublicProfileDtoAsync(
        db,
        user,
        ResolveBaseUrl(httpContext, communityOptions.Value),
        currentUserId,
        isModerator,
        cancellationToken));
});

community.MapPost("/profiles/{username}/follow", async (
    string username,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var normalizedUsername = CommunityUsername.Normalize(username);
    var targetUser = await db.Users.FirstOrDefaultAsync(candidate => candidate.Username == normalizedUsername, cancellationToken);
    if (targetUser is null)
        return Results.NotFound();

    if (targetUser.Id == user.Id)
        return ApiMessage(StatusCodes.Status400BadRequest, "You cannot follow your own profile.");

    var existingFollow = await db.Follows.FirstOrDefaultAsync(
        follow => follow.FollowerUserId == user.Id && follow.FollowedUserId == targetUser.Id,
        cancellationToken);

    if (existingFollow is null)
    {
        db.Follows.Add(new CommunityFollow
        {
            FollowerUserId = user.Id,
            FollowedUserId = targetUser.Id
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    return Results.Ok(await BuildFollowStateAsync(db, user.Id, targetUser.Id, cancellationToken));
}).RequireAuthorization();

community.MapDelete("/profiles/{username}/follow", async (
    string username,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var user = await GetRequiredUserAsync(principal, db, communityOptions.Value, cancellationToken);
    if (user is null)
        return Results.Unauthorized();

    var normalizedUsername = CommunityUsername.Normalize(username);
    var targetUser = await db.Users.FirstOrDefaultAsync(candidate => candidate.Username == normalizedUsername, cancellationToken);
    if (targetUser is null)
        return Results.NotFound();

    var existingFollow = await db.Follows.FirstOrDefaultAsync(
        follow => follow.FollowerUserId == user.Id && follow.FollowedUserId == targetUser.Id,
        cancellationToken);

    if (existingFollow != null)
    {
        db.Follows.Remove(existingFollow);
        await db.SaveChangesAsync(cancellationToken);
    }

    return Results.Ok(await BuildFollowStateAsync(db, user.Id, targetUser.Id, cancellationToken));
}).RequireAuthorization();

var moderation = api.MapGroup("/moderation").RequireAuthorization("ModeratorOnly");

moderation.MapGet("/pending-sounds", async (
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var currentUserId = GetCurrentUserId(principal);
    var sounds = await db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound => sound.ModerationStatus == SoundModerationStatus.Pending)
        .OrderBy(sound => sound.CreatedAtUtc)
        .ToListAsync(cancellationToken);

    return Results.Ok(sounds.Select(sound => sound.ToDto(ResolveBaseUrl(httpContext, communityOptions.Value), currentUserId, true)).ToList());
});

moderation.MapPost("/sounds/{soundId:guid}/approve", async (
    Guid soundId,
    ModerateSoundRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var moderatorId = GetCurrentUserId(principal);
    if (!moderatorId.HasValue)
        return Results.Unauthorized();

    var sound = await db.Sounds
        .Include(item => item.Category)
        .Include(item => item.UploadedByUser)
        .FirstOrDefaultAsync(item => item.Id == soundId, cancellationToken);

    if (sound is null)
        return Results.NotFound();

    sound.ModerationStatus = SoundModerationStatus.Approved;
    sound.ModerationNotes = SanitizeText(request.Notes, 500);
    sound.ModeratedAtUtc = DateTime.UtcNow;
    sound.ModeratedByUserId = moderatorId;

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(sound.ToDto(ResolveBaseUrl(httpContext, communityOptions.Value), moderatorId, true));
});

moderation.MapPost("/sounds/{soundId:guid}/reject", async (
    Guid soundId,
    ModerateSoundRequest request,
    HttpContext httpContext,
    ClaimsPrincipal principal,
    CommunityDbContext db,
    IOptions<CommunityOptions> communityOptions,
    CancellationToken cancellationToken) =>
{
    var moderatorId = GetCurrentUserId(principal);
    if (!moderatorId.HasValue)
        return Results.Unauthorized();

    var sound = await db.Sounds
        .Include(item => item.Category)
        .Include(item => item.UploadedByUser)
        .FirstOrDefaultAsync(item => item.Id == soundId, cancellationToken);

    if (sound is null)
        return Results.NotFound();

    sound.ModerationStatus = SoundModerationStatus.Rejected;
    sound.ModerationNotes = SanitizeText(request.Notes, 500);
    sound.ModeratedAtUtc = DateTime.UtcNow;
    sound.ModeratedByUserId = moderatorId;

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(sound.ToDto(ResolveBaseUrl(httpContext, communityOptions.Value), moderatorId, true));
});

moderation.MapGet("/users", async (
    CommunityDbContext db,
    CancellationToken cancellationToken) =>
{
    var users = await db.Users
        .AsNoTracking()
        .OrderByDescending(user => user.CreatedAtUtc)
        .ToListAsync(cancellationToken);

    var uploadCounts = await db.Sounds
        .AsNoTracking()
        .GroupBy(sound => sound.UploadedByUserId)
        .Select(group => new { UserId = group.Key, Count = group.Count() })
        .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);

    return Results.Ok(users.Select(user => user.ToModerationDto(uploadCounts.GetValueOrDefault(user.Id))).ToList());
});

moderation.MapPost("/users/{userId:guid}/tier", async (
    Guid userId,
    SetAccountTierRequest request,
    CommunityDbContext db,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
    if (user is null)
        return Results.NotFound();

    user.Tier = request.Tier;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new OperationResponse($"Account tier updated to {request.Tier}."));
});

moderation.MapPost("/categories", async (
    CreateCategoryRequest request,
    CommunityDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = SanitizeText(request.Name, 80);
    if (string.IsNullOrWhiteSpace(name))
        return ApiMessage(StatusCodes.Status400BadRequest, "Category name cannot be empty.");

    var slug = await BuildUniqueSlugAsync(db, name, cancellationToken);
    var category = new SoundCategory
    {
        Name = name,
        Slug = slug,
        Description = SanitizeText(request.Description, 280)
    };

    db.Categories.Add(category);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(category.ToDto(0));
});

app.Run();

static async Task<CommunityUserDto> BuildUserDtoAsync(
    CommunityDbContext db,
    CommunityUser user,
    string baseUrl,
    CancellationToken cancellationToken)
{
    var approvedUploadCountTask = db.Sounds
        .AsNoTracking()
        .CountAsync(sound => sound.UploadedByUserId == user.Id && sound.ModerationStatus == SoundModerationStatus.Approved, cancellationToken);
    var totalDownloadsTask = db.Sounds
        .AsNoTracking()
        .Where(sound => sound.UploadedByUserId == user.Id && sound.ModerationStatus == SoundModerationStatus.Approved)
        .SumAsync(sound => (int?)sound.DownloadCount, cancellationToken);
    var followerCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowedUserId == user.Id, cancellationToken);
    var followingCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowerUserId == user.Id, cancellationToken);
    var collectionCountTask = db.Collections
        .AsNoTracking()
        .CountAsync(collection => collection.OwnerUserId == user.Id, cancellationToken);

    await Task.WhenAll(
        approvedUploadCountTask,
        totalDownloadsTask,
        followerCountTask,
        followingCountTask,
        collectionCountTask);

    var achievements = await BuildAchievementsAsync(db, user.Id, cancellationToken);

    return user.ToDto(
        baseUrl,
        followerCountTask.Result,
        followingCountTask.Result,
        collectionCountTask.Result,
        achievements.Count,
        approvedUploadCountTask.Result,
        totalDownloadsTask.Result ?? 0);
}

static async Task<PublicProfileDto> BuildPublicProfileDtoAsync(
    CommunityDbContext db,
    CommunityUser user,
    string baseUrl,
    Guid? currentUserId,
    bool isModerator,
    CancellationToken cancellationToken)
{
    var uploadsTask = db.Sounds
        .AsNoTracking()
        .Include(sound => sound.Category)
        .Include(sound => sound.UploadedByUser)
        .Where(sound => sound.UploadedByUserId == user.Id && sound.ModerationStatus == SoundModerationStatus.Approved)
        .OrderByDescending(sound => sound.DownloadCount)
        .ThenByDescending(sound => sound.CreatedAtUtc)
        .Take(24)
        .ToListAsync(cancellationToken);
    var approvedUploadCountTask = db.Sounds
        .AsNoTracking()
        .CountAsync(sound => sound.UploadedByUserId == user.Id && sound.ModerationStatus == SoundModerationStatus.Approved, cancellationToken);
    var totalDownloadsTask = db.Sounds
        .AsNoTracking()
        .Where(sound => sound.UploadedByUserId == user.Id && sound.ModerationStatus == SoundModerationStatus.Approved)
        .SumAsync(sound => (int?)sound.DownloadCount, cancellationToken);
    var followerCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowedUserId == user.Id, cancellationToken);
    var followingCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowerUserId == user.Id, cancellationToken);
    var isFollowedTask = currentUserId.HasValue
        ? db.Follows
            .AsNoTracking()
            .AnyAsync(follow => follow.FollowerUserId == currentUserId.Value && follow.FollowedUserId == user.Id, cancellationToken)
        : Task.FromResult(false);

    await Task.WhenAll(
        uploadsTask,
        approvedUploadCountTask,
        totalDownloadsTask,
        followerCountTask,
        followingCountTask,
        isFollowedTask);

    var achievements = await BuildAchievementsAsync(db, user.Id, cancellationToken);
    var collections = await LoadCollectionsAsync(db, user.Id, baseUrl, currentUserId, isModerator, 8, cancellationToken);

    return user.ToPublicProfileDto(
        baseUrl,
        followerCountTask.Result,
        followingCountTask.Result,
        approvedUploadCountTask.Result,
        totalDownloadsTask.Result ?? 0,
        isFollowedTask.Result,
        achievements,
        collections,
        uploadsTask.Result.Select(sound => sound.ToDto(baseUrl, currentUserId, isModerator)).ToList());
}

static async Task<FollowStateDto> BuildFollowStateAsync(
    CommunityDbContext db,
    Guid currentUserId,
    Guid viewedUserId,
    CancellationToken cancellationToken)
{
    var isFollowingTask = db.Follows
        .AsNoTracking()
        .AnyAsync(follow => follow.FollowerUserId == currentUserId && follow.FollowedUserId == viewedUserId, cancellationToken);
    var followerCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowedUserId == viewedUserId, cancellationToken);
    var followingCountTask = db.Follows
        .AsNoTracking()
        .CountAsync(follow => follow.FollowerUserId == viewedUserId, cancellationToken);

    await Task.WhenAll(isFollowingTask, followerCountTask, followingCountTask);
    return new FollowStateDto(isFollowingTask.Result, followerCountTask.Result, followingCountTask.Result);
}

static async Task<List<CommunityCollectionDto>> LoadCollectionsAsync(
    CommunityDbContext db,
    Guid? ownerUserId,
    string baseUrl,
    Guid? currentUserId,
    bool isModerator,
    int limit,
    CancellationToken cancellationToken)
{
    var collectionsQuery = db.Collections
        .AsNoTracking()
        .Include(collection => collection.Owner)
        .Include(collection => collection.CollectionSounds)
            .ThenInclude(link => link.Sound)
                .ThenInclude(sound => sound.Category)
        .Include(collection => collection.CollectionSounds)
            .ThenInclude(link => link.Sound)
                .ThenInclude(sound => sound.UploadedByUser)
        .AsQueryable();

    if (ownerUserId.HasValue)
        collectionsQuery = collectionsQuery.Where(collection => collection.OwnerUserId == ownerUserId.Value);

    var collections = await collectionsQuery
        .OrderByDescending(collection => collection.CreatedAtUtc)
        .Take(Math.Clamp(limit, 1, 40))
        .ToListAsync(cancellationToken);

    return collections
        .Select(collection => collection.ToDto(baseUrl, currentUserId, isModerator))
        .Where(collection => collection.Sounds.Count > 0)
        .ToList();
}

static async Task<List<CommunityAchievementDto>> BuildAchievementsAsync(
    CommunityDbContext db,
    Guid userId,
    CancellationToken cancellationToken)
{
    var approvedUploadCountTask = db.Sounds
        .AsNoTracking()
        .CountAsync(sound => sound.UploadedByUserId == userId && sound.ModerationStatus == SoundModerationStatus.Approved, cancellationToken);
    var totalDownloadsTask = db.Sounds
        .AsNoTracking()
        .Where(sound => sound.UploadedByUserId == userId && sound.ModerationStatus == SoundModerationStatus.Approved)
        .SumAsync(sound => (int?)sound.DownloadCount, cancellationToken);
    var collectionCountTask = db.Collections
        .AsNoTracking()
        .CountAsync(collection => collection.OwnerUserId == userId, cancellationToken);
    var moderationHistoryTask = db.Sounds
        .AsNoTracking()
        .Where(sound => sound.UploadedByUserId == userId)
        .OrderBy(sound => sound.CreatedAtUtc)
        .Select(sound => sound.ModerationStatus)
        .ToListAsync(cancellationToken);
    var weeklyTopUsersTask = db.SoundDownloads
        .AsNoTracking()
        .Where(download => download.DownloadedAtUtc >= DateTime.UtcNow.AddDays(-7))
        .Join(
            db.Sounds.AsNoTracking().Where(sound => sound.ModerationStatus == SoundModerationStatus.Approved),
            download => download.SoundId,
            sound => sound.Id,
            (download, sound) => new { sound.UploadedByUserId })
        .GroupBy(item => item.UploadedByUserId)
        .Select(group => new { UserId = group.Key, Count = group.Count() })
        .OrderByDescending(group => group.Count)
        .Take(3)
        .ToListAsync(cancellationToken);

    await Task.WhenAll(
        approvedUploadCountTask,
        totalDownloadsTask,
        collectionCountTask,
        moderationHistoryTask,
        weeklyTopUsersTask);

    var achievements = new List<CommunityAchievementDto>();
    var maxApprovedStreak = GetMaxApprovedStreak(moderationHistoryTask.Result);

    if ((totalDownloadsTask.Result ?? 0) >= 100)
    {
        achievements.Add(new CommunityAchievementDto(
            "downloads_100",
            "CommunityAchievement100DownloadsTitle",
            "CommunityAchievement100DownloadsDescription",
            "#F59E0B"));
    }

    if (approvedUploadCountTask.Result >= 5)
    {
        achievements.Add(new CommunityAchievementDto(
            "approved_5",
            "CommunityAchievement5ApprovedTitle",
            "CommunityAchievement5ApprovedDescription",
            "#22C55E"));
    }

    if (maxApprovedStreak >= 5)
    {
        achievements.Add(new CommunityAchievementDto(
            "streak_5",
            "CommunityAchievement5StreakTitle",
            "CommunityAchievement5StreakDescription",
            "#8B5CF6"));
    }

    if (weeklyTopUsersTask.Result.Any(item => item.UserId == userId))
    {
        achievements.Add(new CommunityAchievementDto(
            "top_week",
            "CommunityAchievementTopWeekTitle",
            "CommunityAchievementTopWeekDescription",
            "#EC4899"));
    }

    if (collectionCountTask.Result >= 1)
    {
        achievements.Add(new CommunityAchievementDto(
            "pack_curator",
            "CommunityAchievementPackCuratorTitle",
            "CommunityAchievementPackCuratorDescription",
            "#06B6D4"));
    }

    return achievements;
}

static int GetMaxApprovedStreak(IEnumerable<SoundModerationStatus> statuses)
{
    var current = 0;
    var max = 0;

    foreach (var status in statuses)
    {
        if (status == SoundModerationStatus.Approved)
        {
            current++;
            if (current > max)
                max = current;
        }
        else
        {
            current = 0;
        }
    }

    return max;
}

static async Task SeedCategoriesAsync(CommunityDbContext db)
{
    if (await db.Categories.AnyAsync())
        return;

    var categories = new[]
    {
        new SoundCategory { Name = "Memes", Slug = "memes", Description = "Short meme sounds and reaction clips." },
        new SoundCategory { Name = "Games", Slug = "games", Description = "Game voice lines, UI effects and moments." },
        new SoundCategory { Name = "Voices", Slug = "voices", Description = "Narration, quotes and voice snippets." },
        new SoundCategory { Name = "Ambience", Slug = "ambience", Description = "Atmospheric loops and background audio." },
        new SoundCategory { Name = "Reactions", Slug = "reactions", Description = "Punchlines, laughs, hype and stingers." }
    };

    db.Categories.AddRange(categories);
    await db.SaveChangesAsync();
}

static Guid? GetCurrentUserId(ClaimsPrincipal principal)
{
    var rawValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(rawValue, out var userId) ? userId : null;
}

static async Task<CommunityUser?> GetRequiredUserAsync(
    ClaimsPrincipal principal,
    CommunityDbContext db,
    CommunityOptions communityOptions,
    CancellationToken cancellationToken)
{
    var userId = GetCurrentUserId(principal);
    if (!userId.HasValue)
        return null;

    var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId.Value, cancellationToken);
    if (user is null)
        return null;

    var shouldBeModerator = IsModeratorEmail(user.Email, communityOptions.ModeratorEmails);
    if (user.IsModerator != shouldBeModerator)
    {
        user.IsModerator = shouldBeModerator;
        await db.SaveChangesAsync(cancellationToken);
    }

    return user;
}

static string? NormalizeEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email))
        return null;

    try
    {
        var address = new MailAddress(email.Trim());
        return address.Address.ToLowerInvariant();
    }
    catch
    {
        return null;
    }
}

static bool ValidatePassword(string? password, out string error)
{
    if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
    {
        error = "Password must contain at least 8 characters.";
        return false;
    }

    error = string.Empty;
    return true;
}

static string BuildDisplayName(string? displayName, string email)
{
    var cleaned = SanitizeText(displayName, 80);
    if (!string.IsNullOrWhiteSpace(cleaned))
        return cleaned;

    var emailName = email.Split('@', 2)[0];
    return SanitizeText(emailName, 80);
}

static async Task<(bool IsValid, string Username, string ErrorMessage)> ResolveUsernameAsync(
    CommunityDbContext db,
    string? requestedUsername,
    string displayName,
    string email,
    Guid? excludedUserId,
    CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(requestedUsername))
    {
        var normalizedRequestedUsername = CommunityUsername.Normalize(requestedUsername);
        if (normalizedRequestedUsername.Length < CommunityUsername.MinLength)
        {
            return (
                false,
                string.Empty,
                $"Handle is invalid. {CommunityUsername.DescribeRules()}");
        }

        var isTaken = await db.Users.AnyAsync(
            user => user.Username == normalizedRequestedUsername && (!excludedUserId.HasValue || user.Id != excludedUserId.Value),
            cancellationToken);

        if (isTaken)
            return (false, string.Empty, "This handle is already taken.");

        return (true, normalizedRequestedUsername, string.Empty);
    }

    var seed = CommunityUsername.BuildSeed(null, displayName, email);
    var generatedUsername = await BuildUniqueUsernameAsync(db, seed, excludedUserId, cancellationToken);
    return (true, generatedUsername, string.Empty);
}

static async Task<string> BuildUniqueUsernameAsync(
    CommunityDbContext db,
    string seed,
    Guid? excludedUserId,
    CancellationToken cancellationToken)
{
    var index = 1;
    while (true)
    {
        var candidate = CommunityUsername.WithSuffix(seed, index);
        var exists = await db.Users.AnyAsync(
            user => user.Username == candidate && (!excludedUserId.HasValue || user.Id != excludedUserId.Value),
            cancellationToken);

        if (!exists)
            return candidate;

        index++;
    }
}

static string SanitizeText(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
        return string.Empty;

    var trimmed = value.Trim();
    return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
}

static string ResolveBaseUrl(HttpContext httpContext, CommunityOptions communityOptions)
{
    if (!string.IsNullOrWhiteSpace(communityOptions.PublicBaseUrl))
        return communityOptions.PublicBaseUrl.TrimEnd('/');

    return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
}

static string BuildConfirmationUrl(HttpContext httpContext, CommunityOptions communityOptions, string token)
{
    var baseUrl = ResolveBaseUrl(httpContext, communityOptions);
    return $"{baseUrl}/api/auth/confirm-email?token={Uri.EscapeDataString(token)}";
}

static bool IsModeratorEmail(string email, IEnumerable<string> moderatorEmails)
{
    return moderatorEmails.Any(candidate =>
        string.Equals(candidate?.Trim(), email, StringComparison.OrdinalIgnoreCase));
}

static string GenerateSecureToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

static IResult ApiMessage(int statusCode, string message) =>
    Results.Json(new OperationResponse(message), statusCode: statusCode);

static string BuildConfirmationHtml(string message, bool isSuccess)
{
    var accent = isSuccess ? "#22c55e" : "#f97316";
    var title = isSuccess ? "Email confirmed" : "Confirmation failed";
    var safeMessage = System.Net.WebUtility.HtmlEncode(message);

    return $"""
        <html>
        <body style="margin:0;background:#020617;color:#e2e8f0;font-family:Segoe UI,Arial,sans-serif;">
            <div style="max-width:680px;margin:40px auto;padding:24px;">
                <div style="background:#0f172a;border:1px solid #1e293b;border-radius:24px;padding:32px;">
                    <div style="display:inline-block;padding:6px 12px;border-radius:999px;background:{accent};color:#020617;font-weight:700;">
                        Auralistix Community
                    </div>
                    <h1 style="margin:18px 0 12px;font-size:32px;">{title}</h1>
                    <p style="margin:0;color:#cbd5e1;font-size:18px;line-height:1.6;">{safeMessage}</p>
                </div>
            </div>
        </body>
        </html>
        """;
}

static async Task<string> BuildUniqueSlugAsync(CommunityDbContext db, string name, CancellationToken cancellationToken)
{
    var baseSlug = Slugify(name);
    var slug = baseSlug;
    var index = 2;

    while (await db.Categories.AnyAsync(category => category.Slug == slug, cancellationToken))
    {
        slug = $"{baseSlug}-{index}";
        index++;
    }

    return slug;
}

static async Task<string> BuildUniqueCollectionSlugAsync(
    CommunityDbContext db,
    string ownerUsername,
    string name,
    CancellationToken cancellationToken)
{
    var baseSlug = Slugify($"{ownerUsername}-{name}");
    var slug = baseSlug;
    var index = 2;

    while (await db.Collections.AnyAsync(collection => collection.Slug == slug, cancellationToken))
    {
        slug = $"{baseSlug}-{index}";
        index++;
    }

    return slug;
}

static string Slugify(string value)
{
    var builder = new StringBuilder();
    var pendingDash = false;

    foreach (var character in value.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(character))
        {
            if (pendingDash && builder.Length > 0)
                builder.Append('-');

            builder.Append(character);
            pendingDash = false;
        }
        else if (builder.Length > 0)
        {
            pendingDash = true;
        }
    }

    return builder.Length == 0 ? $"category-{Guid.NewGuid():N}" : builder.ToString();
}

static bool ContainsIgnoreCase(string? source, string? value)
{
    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value))
        return false;

    return source.Contains(value, StringComparison.OrdinalIgnoreCase);
}

static string ResolveServerUrl(string? configuredUrl)
{
    var runtimeUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (!string.IsNullOrWhiteSpace(runtimeUrls))
        return runtimeUrls;

    runtimeUrls = Environment.GetEnvironmentVariable("URLS");
    if (!string.IsNullOrWhiteSpace(runtimeUrls))
        return runtimeUrls;

    var runtimePort = Environment.GetEnvironmentVariable("PORT");
    if (int.TryParse(runtimePort, out var port) && port > 0)
        return $"http://0.0.0.0:{port}";

    return string.IsNullOrWhiteSpace(configuredUrl) ? "http://localhost:5188" : configuredUrl;
}

static string ResolveCommunityConnectionString(
    IConfiguration configuration,
    IHostEnvironment environment,
    DatabaseOptions databaseOptions)
{
    var configuredConnectionString = configuration.GetConnectionString("Community");
    if (IsPostgresDatabaseProvider(databaseOptions.Provider))
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            throw new InvalidOperationException(
                "Set ConnectionStrings__Community to the PostgreSQL connection string before using Database__Provider=Postgres.");

        return configuredConnectionString;
    }

    return ResolveSqliteConnectionString(
        configuredConnectionString ?? "Data Source=Data/auralistix-community.db",
        environment.ContentRootPath);
}

static bool IsPostgresDatabaseProvider(string? provider) =>
    string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
    || string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase)
    || string.Equals(provider, "Npgsql", StringComparison.OrdinalIgnoreCase);

static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
{
    if (string.IsNullOrWhiteSpace(connectionString))
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(contentRootPath, "Data", "auralistix-community.db")
        }.ConnectionString;

    var builder = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource))
        return builder.ConnectionString;

    if (builder.DataSource == ":memory:"
        || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || builder.DataSource.Contains('|')
        || Path.IsPathRooted(builder.DataSource))
    {
        EnsureSqliteDirectory(builder.DataSource);
        return builder.ConnectionString;
    }

    builder.DataSource = Path.GetFullPath(Path.Combine(contentRootPath, builder.DataSource));
    EnsureSqliteDirectory(builder.DataSource);
    return builder.ConnectionString;
}

static void EnsureSqliteDirectory(string dataSource)
{
    if (string.IsNullOrWhiteSpace(dataSource)
        || dataSource == ":memory:"
        || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || dataSource.Contains('|'))
    {
        return;
    }

    var directory = Path.GetDirectoryName(dataSource);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
}

static void ValidateProductionSecrets(IConfiguration configuration, IHostEnvironment environment)
{
    if (!environment.IsProduction())
        return;

    var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
    if (IsPostgresDatabaseProvider(databaseOptions.Provider)
        && string.IsNullOrWhiteSpace(configuration.GetConnectionString("Community")))
    {
        throw new InvalidOperationException(
            "Set ConnectionStrings__Community before deploying Auralistix Community with PostgreSQL.");
    }

    var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
    var defaultSigningKey = new JwtOptions().SigningKey;
    if (string.Equals(jwtOptions.SigningKey, defaultSigningKey, StringComparison.Ordinal)
        || jwtOptions.SigningKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Set Jwt__SigningKey to a private production secret with at least 32 characters before deploying Auralistix Community.");
    }

    var communityOptions = configuration.GetSection(CommunityOptions.SectionName).Get<CommunityOptions>() ?? new CommunityOptions();
    if (!string.Equals(communityOptions.StorageProvider, "S3", StringComparison.OrdinalIgnoreCase))
        return;

    var s3Options = configuration.GetSection(S3Options.SectionName).Get<S3Options>() ?? new S3Options();
    if (string.IsNullOrWhiteSpace(s3Options.BucketName)
        || string.IsNullOrWhiteSpace(s3Options.AccessKey)
        || string.IsNullOrWhiteSpace(s3Options.SecretKey))
    {
        throw new InvalidOperationException(
            "Set S3__BucketName, S3__AccessKey, and S3__SecretKey before deploying with Community__StorageProvider=S3.");
    }
}

static void LogProductionConfigurationWarnings(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger logger)
{
    if (!environment.IsProduction())
        return;

    var communityOptions = configuration.GetSection(CommunityOptions.SectionName).Get<CommunityOptions>() ?? new CommunityOptions();
    var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
    if (!IsPostgresDatabaseProvider(databaseOptions.Provider))
    {
        logger.LogWarning(
            "Database__Provider is not Postgres. SQLite is suitable for local testing, but PostgreSQL is recommended for public community data.");
    }

    if (string.IsNullOrWhiteSpace(communityOptions.PublicBaseUrl)
        || communityOptions.PublicBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "Community__PublicBaseUrl is not configured for a public host. Confirmation, avatar, and download links may point to localhost.");
    }

    var smtpOptions = configuration.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new SmtpOptions();
    if (string.IsNullOrWhiteSpace(smtpOptions.Host))
    {
        logger.LogWarning(
            "Smtp__Host is empty. Registration will use local email previews, which are only suitable for staging or manual testing.");
    }

    if (!string.Equals(communityOptions.StorageProvider, "S3", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "Community__StorageProvider is not S3. Local file storage is suitable for local testing, but S3 is recommended for public uploads.");
    }
}
