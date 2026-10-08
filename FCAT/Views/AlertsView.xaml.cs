using System.Windows.Controls;
using FCAT.Services;

namespace FCAT.Views;

public partial class AlertsView : UserControl
{
    public AlertsView() => InitializeComponent();

    // Remembers what a sound dropdown held when it was opened, so closing it can tell a real
    // choice from "opened it and changed my mind".
    private object? _soundBeforeOpen;

    private void SoundDropDownOpened(object sender, EventArgs e)
        => _soundBeforeOpen = ((ComboBox)sender).SelectedValue;

    /// <summary>
    /// Plays the cue once the dropdown is closed, which is the point the FC has actually chosen it.
    ///
    /// This used to fire from the view model whenever the bound value changed. A WPF ComboBox moves
    /// its selection as you arrow down the open list, so that played every preset you passed over
    /// and left you on whichever one you stopped at - "None" being the first item in the list.
    /// </summary>
    private void SoundDropDownClosed(object sender, EventArgs e)
    {
        var value = ((ComboBox)sender).SelectedValue;
        if (Equals(value, _soundBeforeOpen)) return;
        SoundService.Play(value as string);
    }
}
