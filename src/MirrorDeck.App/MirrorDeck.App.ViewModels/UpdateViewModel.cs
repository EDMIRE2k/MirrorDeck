using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MirrorDeck.Core.Updates;

namespace MirrorDeck.App.ViewModels;

public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly GitHubUpdates _client = new();
    private CancellationTokenSource? _operation;
    private UpdateRelease? _release;
    private string _status = "Not checked yet", _connection = "No connection", _notes = "", _download = "", _hash = "", _checked = "Never";
    private bool _busy, _ready;
    private double _progress;
    public string CurrentVersion { get; } = typeof(UpdateViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('-')[0];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Connection { get => _connection; private set => SetProperty(ref _connection, value); }
    public string Notes { get => _notes; private set => SetProperty(ref _notes, value); }
    public string DownloadUrl { get => _download; private set => SetProperty(ref _download, value); }
    public string Sha256 { get => _hash; private set => SetProperty(ref _hash, value); }
    public string LastChecked { get => _checked; private set => SetProperty(ref _checked, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool Busy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(CanInstall)); } }
    public bool CanCheck => !Busy;
    public bool CanDownload => !Busy && !_ready && _release != null && _release.Version > GitHubUpdates.ParseVersion(CurrentVersion);
    public bool CanInstall => !Busy && _ready;
    public string PackagePath { get; private set; } = "";
    public UpdateRelease Release => _release ?? throw new InvalidOperationException("No update selected.");

    public async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true; _ready = false; _release = null;
        _operation?.Dispose(); _operation = new();
        Status = "Checking GitHub..."; Connection = GitHubUpdates.Endpoint;
        DownloadUrl = ""; Sha256 = ""; Notes = ""; Progress = 0;
        try
        {
            _release = await _client.CheckAsync(_operation.Token);
            LastChecked = DateTime.Now.ToString("g");
            if (_release == null) { Status = "No stable release has been published yet."; return; }
            DownloadUrl = _release.Download.AbsoluteUri; Sha256 = _release.Sha256;
            Notes = _release.Notes;
            Status = _release.Version > GitHubUpdates.ParseVersion(CurrentVersion)
                ? $"Version {_release.Version.ToString(3)} available ({_release.Size / 1048576d:F0} MB)"
                : "You are up to date.";
        }
        catch (OperationCanceledException) { Status = "Check cancelled or timed out."; }
        catch (Exception ex) { Status = "Update check failed: " + ex.Message; }
        finally { Busy = false; }
    }

    public async Task DownloadAsync()
    {
        if (!CanDownload) return;
        Busy = true;
        _operation?.Dispose(); _operation = new();
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDeck", "Updates", Guid.NewGuid().ToString("N"));
        PackagePath = Path.Combine(cache, "MirrorDeck-update.zip");
        try
        {
            var progress = new Progress<UpdateProgress>(p => { Status = p.Status; Progress = p.Percent; Connection = p.Connection; });
            await _client.DownloadAsync(Release, PackagePath, progress, _operation.Token);
            _ready = true; Status = "SHA-256 verified. Ready to install.";
        }
        catch (OperationCanceledException) { Status = "Download cancelled or timed out. Nothing was installed."; }
        catch (Exception ex) { Status = "Download failed: " + ex.Message; }
        finally { Busy = false; }
    }

    public void Cancel() => _operation?.Cancel();
    public void Dispose() { Cancel(); _client.Dispose(); _operation?.Dispose(); }
}
