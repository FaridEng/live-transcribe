using System.Windows;
using LiveTranscribe.Core.Translation;

namespace LiveTranscribe.App.Windows;

/// <summary>
/// Writes the topic note for translations (architecture T14). Typing needs the keyboard focus, which the
/// floating control must never take, so the note gets this small window of its own; closing it hands
/// the focus back to the window the user was writing in.
/// </summary>
public partial class TopicNoteWindow : Window
{
    private readonly TranslationService _translation;

    public TopicNoteWindow(TranslationService translation)
    {
        _translation = translation;
        InitializeComponent();

        NoteBox.Text = translation.TopicNote ?? string.Empty;
        ClearButton.IsEnabled = translation.TopicNote is not null;
        Loaded += (_, _) =>
        {
            Activate();
            NoteBox.Focus();
            NoteBox.SelectAll();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _translation.SetTopicNote(NoteBox.Text);
        Close();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _translation.SetTopicNote(null);
        Close();
    }
}
