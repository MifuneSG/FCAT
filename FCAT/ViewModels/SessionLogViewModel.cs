using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>The after-action report view — shows the session timeline and exports it.</summary>
public partial class SessionLogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public SessionLog Log { get; }

    public SessionLogViewModel(SessionLog log, ShellViewModel shell)
    {
        Log = log;
        _shell = shell;
    }

    [ObservableProperty] private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Copy()
    {
        if (Log.Entries.Count == 0) { StatusMessage = "Nothing to copy yet."; return; }
        try
        {
            System.Windows.Clipboard.SetText(Log.Export());
            StatusMessage = "AAR copied to clipboard.";
        }
        catch { StatusMessage = "Couldn't access the clipboard."; }
    }

    [RelayCommand]
    private void Save()
    {
        if (Log.Entries.Count == 0) { StatusMessage = "Nothing to save yet."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save after-action report",
            FileName = $"FCAT-AAR-{(Log.SessionStart ?? DateTime.Now):yyyyMMdd-HHmm}.md",
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Log.Export());
            StatusMessage = $"Saved to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Save failed — {ex.Message}"; }
    }

    [RelayCommand]
    private void Clear()
    {
        Log.Clear();
        StatusMessage = "Cleared.";
    }

    [RelayCommand]
    private void Back() => _shell.BackToMenu();
}
