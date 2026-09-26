using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// A system-name box with autocomplete. Setup has two, the fleet form-up system and the alliance
/// capital, and they behaved identically, so they share this instead of one being a copy of the
/// other. Each instance keeps its OWN suggestion list: two boxes sharing one dropped their results
/// into each other's dropdown.
///
/// <para>The ping tool has a box that looks like this one but is deliberately not built on it. Its
/// text change rebuilds the MOTD preview and picking a system writes straight through to settings
/// so the straggler tracker follows, which is real behaviour rather than a variation in wiring.
/// Folding it in here would mean callbacks for the sake of a dozen lines.</para>
/// </summary>
public partial class SystemPicker(SystemSearchService search) : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;

    public ObservableCollection<SystemMatch> Suggestions { get; } = [];

    /// <summary>The matched system, or 0 while the text is not a real system name.</summary>
    public int Id { get; private set; }

    // Set by Set() so filling the box in code does not re-open the dropdown or drop the id.
    private bool _suppress;

    /// <summary>Fills the box without searching. Used when loading saved settings and on a pick.</summary>
    public void Set(string text, int id)
    {
        _suppress = true;
        Text = text;
        Id   = id;
        _suppress = false;
        Suggestions.Clear();
    }

    partial void OnTextChanged(string value)
    {
        if (_suppress) return;
        Suggestions.Clear();
        foreach (var m in search.Search(value)) Suggestions.Add(m);
        Id = search.ResolveId(value) ?? 0;
    }

    [RelayCommand]
    private void Pick(SystemMatch? match)
    {
        if (match != null) Set(match.Name, match.Id);
    }

    [RelayCommand]
    private void Clear() => Set(string.Empty, 0);
}
