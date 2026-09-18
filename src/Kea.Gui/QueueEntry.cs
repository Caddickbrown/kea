using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kea.Core;

namespace Kea.Gui;

/// <summary>One row of the download queue.</summary>
public sealed class QueueEntry : INotifyPropertyChanged
{
    private string _start = "1";
    private string _end = "end";

    public QueueEntry(WebtoonUrl url) => Url = url;

    public WebtoonUrl Url { get; }

    public string Name => Url.Slug;

    /// <summary>First chapter, edited as free text so the grid can show validation errors.</summary>
    public string Start
    {
        get => _start;
        set => Set(ref _start, value);
    }

    /// <summary>Last chapter, or the word "end", matching the original's grid.</summary>
    public string End
    {
        get => _end;
        set => Set(ref _end, value);
    }

    /// <summary>
    /// Turns the row into a job, or explains why it cannot be one.
    /// </summary>
    public bool TryBuildJob(out ToonJob? job, out string? error)
    {
        job = null;
        error = null;

        if (!int.TryParse(Start.Trim(), out int start))
        {
            error = $"{Name}: the start chapter must be a number.";
            return false;
        }

        if (start < 1)
        {
            error = $"{Name}: the start chapter must be greater than zero.";
            return false;
        }

        int? end = null;
        string endText = End.Trim();
        if (!string.Equals(endText, "end", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(endText, out int parsedEnd))
            {
                error = $"{Name}: the end chapter must be a number or the word 'end'.";
                return false;
            }

            if (parsedEnd < 1)
            {
                error = $"{Name}: the end chapter must be greater than zero.";
                return false;
            }

            if (parsedEnd < start)
            {
                error = $"{Name}: the start chapter must be smaller than the end chapter.";
                return false;
            }

            end = parsedEnd;
        }

        job = new ToonJob(Url, start, end);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
