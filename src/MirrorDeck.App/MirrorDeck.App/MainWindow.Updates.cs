using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MirrorDeck.App.ViewModels;
using MirrorDeck.Core.Updates;

namespace MirrorDeck.App;

public partial class MainWindow
{
    public UpdateViewModel Updates { get; } = new();
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAsync();
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e) => await Updates.DownloadAsync();
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => Updates.Cancel();
    private void OpenReleasePage_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(GitHubUpdates.Repository + "/releases") { UseShellExecute = true });

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!Updates.CanInstall || _runtimeMaintenance) return;
        if (MessageBox.Show(this, "Install the verified update and restart MirrorDeck?\n\nThe preview will close. Your settings, presets and add-ons will be kept. A backup of this version will be saved beside the application folder.",
            "Update MirrorDeck", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        BeginRuntimeOperation();
        try
        {
            string helper = Path.Combine(Path.GetDirectoryName(Updates.PackagePath)!, "MirrorDeck.Updater.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "MirrorDeck.Updater.exe"), helper, true);
            await _viewModel.BeginRuntimeMaintenanceAsync();
            CaptureWindowSettings();
            await _viewModel.SaveAsync();
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            foreach (string argument in new[] { AppContext.BaseDirectory, Updates.PackagePath, Updates.Release.Sha256,
                Updates.Release.Version.ToString(3), parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(argument);
            using var worker = Process.Start(start) ?? throw new IOException("The updater could not be started.");
            _closingAfterShutdown = true;
            await ShutdownFromWindowAsync();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.Error("Starting update failed.", ex);
            MessageBox.Show(this, ex.Message, "Update could not start", MessageBoxButton.OK, MessageBoxImage.Warning);
            _closingAfterShutdown = false;
            EndRuntimeOperation();
        }
    }
}
