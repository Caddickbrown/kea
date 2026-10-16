using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Kea.Core;
using Kea.Web;
using Kea.Web.Jobs;
using Kea.Web.Library;
using Kea.Web.Security;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Accept the short KEA_ACCESSTOKEN form as well as the canonical Kea__AccessToken one.
// Added last so it wins, and so a token set this way is never silently ignored.
builder.Configuration.AddInMemoryCollection(KeaEnvironment.Map());

builder.Services.Configure<KeaOptions>(builder.Configuration.GetSection(KeaOptions.SectionName));
builder.Services.PostConfigure<KeaOptions>(options => options.Validate());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddHttpClient("webtoons")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(100));

builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<AccessControl>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<RecheckScheduler>();

WebApplication app = builder.Build();

KeaOptions keaOptions = app.Services.GetRequiredService<IOptions<KeaOptions>>().Value;
AccessControl access = app.Services.GetRequiredService<AccessControl>();
LibraryService library = app.Services.GetRequiredService<LibraryService>();

LogStartupPosture(app.Logger, keaOptions, access, library);

app.UseDefaultFiles();
app.UseStaticFiles();

// Everything under /api is closed unless the request passes the access check. The exceptions are
// the login endpoint, the config probe the login page needs, and the bare liveness probe, which
// the container HEALTHCHECK calls without a token and which reveals nothing.
app.Use(async (context, next) =>
{
    PathString path = context.Request.Path;

    if (!path.StartsWithSegments("/api")
        || path.StartsWithSegments("/api/auth/login")
        || path.StartsWithSegments("/api/config")
        || path.Equals("/api/health"))
    {
        await next();
        return;
    }

    AccessDecision decision = access.Check(context);
    if (decision == AccessDecision.Allowed)
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new
    {
        error = decision == AccessDecision.TokenRequired
            ? "An access token is required."
            : "This server only answers local requests. Set an access token to use it remotely.",
        reason = decision.ToString(),
    });
});

// --- configuration probe ------------------------------------------------------------------

app.MapGet("/api/config", (HttpContext context) => Results.Ok(new
{
    tokenRequired = access.TokenRequired,
    authenticated = access.Check(context) == AccessDecision.Allowed,
    formats = Enum.GetValues<SaveFormat>().Select(f => new { value = f.CliName(), label = f.DisplayName() }),
    maxUrlsPerJob = keaOptions.MaxUrlsPerJob,
}));

// --- authentication -----------------------------------------------------------------------

app.MapPost("/api/auth/login", async (HttpContext context) =>
{
    LoginRequest? body = await context.Request.ReadFromJsonAsync<LoginRequest>();

    if (!access.TokenRequired)
    {
        return Results.BadRequest(new { error = "This server has no access token configured." });
    }

    if (body is null || !access.IsValidToken(body.Token))
    {
        // Same wording and timing whether the token was absent or wrong.
        return Results.Json(new { error = "That token was not accepted." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    context.Response.Cookies.Append(AccessControl.CookieName, body.Token, new CookieOptions
    {
        // HttpOnly so a script cannot read it, which also means plain <a> downloads carry it.
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        MaxAge = TimeSpan.FromDays(30),
        Path = "/",
    });

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete(AccessControl.CookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { ok = true });
});

// --- jobs ---------------------------------------------------------------------------------

app.MapPost("/api/jobs", async (HttpContext context, JobQueue queue) =>
{
    JobRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<JobRequest>();
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Could not read the request: {ex.Message}" });
    }

    if (request is null) return Results.BadRequest(new { error = "Empty request." });

    try
    {
        return Results.Ok(queue.Submit(request));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/jobs", (JobQueue queue) => Results.Ok(queue.List()));

app.MapGet("/api/jobs/{id}", (string id, JobQueue queue) =>
    !Guid.TryParse(id, out Guid parsed)
        ? Results.BadRequest(new { error = "Not a job id." })
        : queue.Get(parsed) is { } job
            ? Results.Ok(job)
            : Results.NotFound(new { error = "No such job." }));

app.MapPost("/api/jobs/{id}/cancel", (string id, JobQueue queue) =>
    !Guid.TryParse(id, out Guid parsed)
        ? Results.BadRequest(new { error = "Not a job id." })
        : queue.Cancel(parsed)
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { error = "That job is not running." }));

app.MapDelete("/api/jobs/{id}", (string id, JobQueue queue) =>
    !Guid.TryParse(id, out Guid parsed)
        ? Results.BadRequest(new { error = "Not a job id." })
        : queue.Remove(parsed)
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { error = "That job is still running, or does not exist." }));

// --- live progress ------------------------------------------------------------------------

app.MapGet("/api/events", async (HttpContext context, JobEvents events) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no"; // stop nginx buffering the stream

    JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    (Guid id, ChannelReader<JobEvent> reader) = events.Subscribe();

    try
    {
        await context.Response.WriteAsync(": connected\n\n");
        await context.Response.Body.FlushAsync();

        await foreach (JobEvent update in reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"event: {update.Type}\n");
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(update.Job, json)}\n\n");
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // The browser navigated away or closed the tab; nothing to do.
    }
    finally
    {
        events.Unsubscribe(id);
    }
});

// --- library ------------------------------------------------------------------------------

app.MapGet("/api/library", (string? path) =>
    library.List(path) is { } listing
        ? Results.Ok(listing)
        : Results.NotFound(new { error = "No such folder." }));

app.MapGet("/api/library/file", (string? path) =>
{
    if (!library.TryResolveFile(path, out string? fullPath))
        return Results.NotFound(new { error = "No such file." });

    return Results.File(
        fullPath,
        LibraryService.ContentTypeFor(fullPath),
        Path.GetFileName(fullPath),
        enableRangeProcessing: true);
});

app.MapDelete("/api/library", (string? path) =>
    library.Delete(path)
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { error = "Could not delete that." }));

app.MapPost("/api/library/schedule", (string? path, ScheduleRequest? body, JobQueue queue) =>
{
    if (body is null) return Results.BadRequest(new { error = "Empty request." });
    if (!library.TryReadMeta(path, out Kea.Web.Library.KeaMeta? meta))
        return Results.BadRequest(new { error = "No recheck data found for that folder." });

    int days = body.IntervalDays;
    if (days != 0 && days != 7 && days != 30)
        return Results.BadRequest(new { error = "intervalDays must be 0 (off), 7 (weekly) or 30 (monthly)." });

    meta.RecheckIntervalDays = days;
    // Reset LastCheckedAt so the next sweep doesn't immediately think it's overdue.
    meta.LastCheckedAt = days == 0 ? null : DateTimeOffset.UtcNow;

    return library.WriteMetaFile(path, meta)
        ? Results.Ok(new { ok = true, recheckIntervalDays = days })
        : Results.BadRequest(new { error = "Could not save schedule." });
});

app.MapPost("/api/library/merge", (string? path, string? format, JobQueue queue) =>
{
    if (!library.TryReadMeta(path, out Kea.Web.Library.KeaMeta? meta))
        return Results.BadRequest(new { error = "No download data found for that folder." });

    string fmt = format switch { "merged-pdf" => "merged-pdf", _ => "merged-cbz" };

    try
    {
        JobView view = queue.Submit(new Kea.Web.Jobs.JobRequest
        {
            Urls = [meta.Url],
            Format = fmt,
            Start = 1,
            End = null,
            ComicFolders = meta.ComicFolders,
            ChapterFolders = false,
        });
        return Results.Ok(view);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});

app.MapPost("/api/library/repack", async (string? path, string? format, HttpContext context) =>
{
    // Repack existing CBZ files in the folder into a single merged file — no network needed.
    if (!library.TryResolve(path, out string? fullPath) || !Directory.Exists(fullPath))
        return Results.NotFound(new { error = "Folder not found." });

    bool pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
    string ext = pdf ? ".pdf" : ".cbz";
    string contentType = pdf ? "application/pdf" : "application/vnd.comicbook+zip";

    List<string> cbzFiles = Directory
        .EnumerateFiles(fullPath, "*.cbz")
        .OrderBy(f => Path.GetFileName(f), Kea.Web.Library.LibraryService.NaturalFileComparer.Instance)
        .ToList();

    if (cbzFiles.Count == 0)
        return Results.BadRequest(new { error = "No CBZ files found in this folder." });

    string folderName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
    string outputName = folderName + ext;

    context.Response.ContentType = contentType;
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{outputName}\"";

    if (pdf)
        await Kea.Core.ChapterPackager.MergeCbzFilesToPdfAsync(cbzFiles, context.Response.Body, context.RequestAborted);
    else
        await Kea.Core.ChapterPackager.MergeCbzFilesStreamAsync(cbzFiles, context.Response.Body, context.RequestAborted);

    return Results.Empty;
});

app.MapPost("/api/library/recheck", (string? path, JobQueue queue) =>
{
    if (!library.TryReadMeta(path, out Kea.Web.Library.KeaMeta? meta))
        return Results.BadRequest(new { error = "No recheck data found for that folder." });

    try
    {
        JobView view = queue.Submit(new Kea.Web.Jobs.JobRequest
        {
            Urls = [meta.Url],
            Format = meta.Format,
            Start = meta.LastChapter + 1,
            End = null,
            ComicFolders = meta.ComicFolders,
            ChapterFolders = meta.ChapterFolders,
        });
        return Results.Ok(view);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static void LogStartupPosture(ILogger logger, KeaOptions options, AccessControl access, LibraryService library)
{
    logger.LogInformation("Library: {Path}", library.Root);

    if (access.TokenRequired)
    {
        logger.LogInformation("Access: token required.");
    }
    else if (options.AllowAnonymous)
    {
        logger.LogWarning(
            "Access: ANONYMOUS. Anyone who can reach this port can queue downloads and read the " +
            "library. Only do this behind a proxy that authenticates first.");
    }
    else
    {
        logger.LogInformation(
            "Access: local only. No token is set, so remote requests are refused. " +
            "Set Kea:AccessToken (or KEA_ACCESSTOKEN) to use this from another machine.");
    }
}

internal sealed record LoginRequest(string Token);
internal sealed record ScheduleRequest(int IntervalDays);

/// <summary>Exposed so the test project can spin the app up in-process.</summary>
public partial class Program;
