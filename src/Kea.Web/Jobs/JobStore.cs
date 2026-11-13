using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kea.Web.Jobs;

/// <summary>A job as written to disk: what was asked for, and how far it got.</summary>
public sealed record PersistedJob(JobRequest Request, JobView View);

/// <summary>
/// Keeps the job list in a file under the library volume, so a restart or a rebuilt container
/// does not lose the queue, the history or the errors.
/// </summary>
/// <remarks>
/// The file is written to a temporary name and moved over the old one, so a crash mid-write
/// leaves the previous copy intact. It lives in <c>.kea-state</c> inside the library, because the
/// library is the one volume the container is allowed to write to.
/// </remarks>
public sealed class JobStore
{
    public const string FolderName = ".kea-state";
    private const string FileName = "jobs.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogger<JobStore> _logger;
    private readonly object _gate = new();

    public JobStore(Microsoft.Extensions.Options.IOptions<KeaOptions> options, ILogger<JobStore> logger)
    {
        _logger = logger;
        string folder = Path.Combine(Path.GetFullPath(options.Value.LibraryPath), FolderName);
        _path = Path.Combine(folder, FileName);
    }

    /// <summary>Reads the saved jobs. A missing or unreadable file means an empty list, never a crash.</summary>
    public IReadOnlyList<PersistedJob> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];

                using FileStream stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize<List<PersistedJob>>(stream, Json) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not read the saved job list at {Path}; starting empty", _path);
                return [];
            }
        }
    }

    /// <summary>Writes the whole list. A failure is logged loudly but never stops a download.</summary>
    public void Save(IReadOnlyList<PersistedJob> jobs)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";

                using (FileStream stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, jobs, Json);
                }

                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The earlier "lost it silently" bug was exactly this kind of failure going unseen.
                _logger.LogError(ex, "Could not save the job list to {Path}", _path);
            }
        }
    }
}
