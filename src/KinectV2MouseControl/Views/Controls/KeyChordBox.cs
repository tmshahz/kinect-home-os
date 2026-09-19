using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    /// <summary>
    /// A text box for a key combination. Pressing a shortcut that uses Ctrl, Alt or Win (or a
    /// key that types nothing, such as F5 or an arrow) records it as text, "Ctrl+Shift+D".
    /// Plain typing still works, so a combination Windows keeps for itself (Win+H never
    /// reaches an app) can be written out by hand. Enter commits, Esc restores what was there.
    /// </summary>
    public class KeyChordBox : TextBox
    {
        private string before = "";

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            before = Text;
            base.OnGotKeyboardFocus(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            ModifierKeys modifiers = Keyboard.Modifiers;

            if (KeyChord.IsModifierKey(key))
            {
                // A lone Alt would otherwise move focus to a menu.
                if (key == Key.LeftAlt || key == Key.RightAlt)
                {
                    e.Handled = true;
                }

                return;
            }

            bool commandModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
            if (!commandModifier)
            {
                if (key == Key.Escape)
                {
                    Text = before;
                    SelectAll();
                    e.Handled = true;
                    return;
                }

                if (key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                    return;
                }

                if (!IsNonTypingKey(key))
                {
                    // Typing and editing.
                    base.OnPreviewKeyDown(e);
                    return;
                }
            }

            KeyChord chord = KeyChord.FromPress(modifiers, key);
            if (chord != null)
            {
                Text = chord.Text;
                CaretIndex = Text.Length;
                Commit();
                e.Handled = true;
            }
        }

        private void Commit()
        {
            BindingExpression binding = GetBindingExpression(TextProperty);
            if (binding != null)
            {
                binding.UpdateSource();
            }

            before = Text;
        }

        private static bool IsNonTypingKey(Key key)
        {
            if (key >= Key.F1 && key <= Key.F24)
            {
                return true;
            }

            // Arrows, Home and End stay caret keys unless a modifier is held.
            switch (key)
            {
                case Key.PageUp:
                case Key.PageDown:
                case Key.Insert:
                case Key.Snapshot:
                case Key.Pause:
                case Key.Apps:
                case Key.VolumeMute:
                case Key.VolumeDown:
                case Key.VolumeUp:
                case Key.MediaNextTrack:
                case Key.MediaPreviousTrack:
                case Key.MediaPlayPause:
                case Key.MediaStop:
                    return true;
                default:
                    return false;
            }
        }
    }
}
