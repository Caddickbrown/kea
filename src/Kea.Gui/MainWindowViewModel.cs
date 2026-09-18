using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kea.Core;

namespace Kea.Gui;

/// <summary>
/// The state behind the main window.
/// </summary>
/// <remarks>
/// The original kept all of this inside the WinForms form and drove the UI by disabling every
/// control by hand. Here the view binds to <see cref="IsBusy"/> instead, which keeps the logic
/// testable and independent of any one toolkit.
/// </remarks>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _urlText = string.Empty;
    private string _savePath = string.Empty;
    private string _status = "idle";
    private double _progress;
    private bool _isBusy;
    private QueueEntry? _selectedEntry;
    private SaveFormat _selectedFormat = SaveFormat.Pdf;
    private bool _cartoonFolders;
    private bool _chapterFolders;
    private CancellationTokenSource? _cancellation;

    public MainWindowViewModel()
    {
        // Somewhere sensible to start on every platform, rather than a Windows-shaped default.
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _savePath = string.IsNullOrWhiteSpace(documents)
            ? Directory.GetCurrentDirectory()
            : documents;

        AddToQueueCommand = new RelayCommand(AddToQueue, () => !IsBusy);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => !IsBusy && SelectedEntry is not null);
        RemoveAllCommand = new RelayCommand(RemoveAll, () => !IsBusy && Queue.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);

        Queue.CollectionChanged += (_, _) => RaiseCommandStates();
    }

    public ObservableCollection<QueueEntry> Queue { get; } = [];

    public IReadOnlyList<SaveFormat> Formats { get; } = Enum.GetValues<SaveFormat>();

    public RelayCommand AddToQueueCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand RemoveAllCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string UrlText
    {
        get => _urlText;
        set => Set(ref _urlText, value);
    }

    public string SavePath
    {
        get => _savePath;
        set => Set(ref _savePath, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>Overall completion, 0 to 100, for the progress bar.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RaiseCommandStates();
        }
    }

    public bool IsIdle => !IsBusy;

    public QueueEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (Set(ref _selectedEntry, value)) RemoveSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public SaveFormat SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (!Set(ref _selectedFormat, value)) return;

            // Chapter sub-folders only mean anything when the images are left as separate files;
            // the other formats need a scratch folder that is deleted once the file is written.
            OnPropertyChanged(nameof(ChapterFoldersEnabled));
            ChapterFolders = ChapterFoldersEnabled;
        }
    }

    public bool CartoonFolders
    {
        get => _cartoonFolders;
        set => Set(ref _cartoonFolders, value);
    }

    public bool ChapterFolders
    {
        get => _chapterFolders;
        set => Set(ref _chapterFolders, value);
    }

    public bool ChapterFoldersEnabled => SelectedFormat == SaveFormat.MultipleImages;

    /// <summary>Raised when the view should show a message box.</summary>
    public event Func<string, string, Task>? MessageRequested;

    private void AddToQueue()
    {
        List<string> rejected = [];

        char[] lineBreaks = ['\n', '\r'];
        foreach (string line in UrlText.Split(lineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (!WebtoonUrl.TryParse(trimmed, out WebtoonUrl? url, out string? error))
            {
                rejected.Add($"{trimmed} — {error}");
                continue;
            }

            // The site's own title_no is a comic's identity; the original compared slugs.
            if (Queue.Any(q => q.Url.TitleNo == url.TitleNo)) continue;

            Queue.Add(new QueueEntry(url));
        }

        UrlText = string.Empty;

        // The original silently dropped bad links, which left people guessing.
        if (rejected.Count > 0)
        {
            _ = MessageRequested?.Invoke(
                "Some links were not added",
                string.Join(Environment.NewLine + Environment.NewLine, rejected));
        }
    }

    private void RemoveSelected()
    {
        if (SelectedEntry is null) return;
        Queue.Remove(SelectedEntry);
        SelectedEntry = null;
    }

    private void RemoveAll()
    {
        Queue.Clear();
        SelectedEntry = null;
    }

    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Validates the queue and runs it. Returns once the whole queue is finished.</summary>
    public async Task StartAsync()
    {
        if (IsBusy) return;

        if (Queue.Count == 0)
        {
            await ShowAsync("Nothing to do", "Add at least one comic to the queue first.").ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(SavePath) || !Directory.Exists(SavePath))
        {
            await ShowAsync("No save folder", "Please select a directory for saving.").ConfigureAwait(true);
            return;
        }

        List<ToonJob> jobs = [];
        foreach (QueueEntry entry in Queue)
        {
            if (!entry.TryBuildJob(out ToonJob? job, out string? error))
            {
                await ShowAsync("Check the queue", error!).ConfigureAwait(true);
                return;
            }

            jobs.Add(job!);
        }

        IsBusy = true;
        Progress = 0;
        _cancellation = new CancellationTokenSource();

        try
        {
            DownloadOptions options = new()
            {
                SavePath = SavePath,
                Format = SelectedFormat,
                CartoonFolders = CartoonFolders,
                ChapterFolders = ChapterFolders,
            };

            using WebtoonClient client = new();
            KeaDownloader downloader = new(client);

            // Progress<T> marshals back to the UI thread, so no Invoke dance is needed.
            Progress<DownloadProgress> progress = new(update =>
            {
                Status = update.Status;
                if (update.Fraction is { } fraction) Progress = Math.Clamp(fraction * 100, 0, 100);
            });

            DownloadReport report = await downloader
                .DownloadAsync(jobs, options, progress, _cancellation.Token)
                .ConfigureAwait(true);

            Status = report.FailedCount == 0
                ? $"done! {report.SucceededCount} chapter(s) saved."
                : $"done with errors: {report.SucceededCount} saved, {report.FailedCount} failed.";

            if (report.FailedCount > 0)
            {
                string detail = string.Join(
                    Environment.NewLine,
                    report.Chapters.Where(c => !c.Succeeded).Take(20)
                        .Select(c => $"{c.Comic} chapter {c.ChapterNumber}: {c.Error}"));

                await ShowAsync("Some chapters failed", detail).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "cancelled.";
        }
        catch (Exception ex)
        {
            Status = "failed.";
            await ShowAsync("Something went wrong", ex.Message).ConfigureAwait(true);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    private Task ShowAsync(string title, string message)
        => MessageRequested?.Invoke(title, message) ?? Task.CompletedTask;

    private void RaiseCommandStates()
    {
        AddToQueueCommand.RaiseCanExecuteChanged();
        RemoveSelectedCommand.RaiseCanExecuteChanged();
        RemoveAllCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
