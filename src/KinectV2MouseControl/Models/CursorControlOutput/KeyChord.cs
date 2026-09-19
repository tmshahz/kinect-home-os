using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    /// <summary>
    /// A key combination such as Ctrl+Shift+D: any of Win, Ctrl, Alt, Shift plus exactly one
    /// other key. Stored and shown as text ("Ctrl+Shift+D"); parsed leniently from what people
    /// type ("left control + left shift + d", "ctrl+shift+d") and always written back in one
    /// canonical form.
    /// </summary>
    public sealed class KeyChord
    {
        public bool Win { get; private set; }
        public bool Ctrl { get; private set; }
        public bool Alt { get; private set; }
        public bool Shift { get; private set; }
        public Key Key { get; private set; }

        private static readonly Dictionary<Key, string> Names = BuildNames();
        private static readonly Dictionary<string, Key> ByName = BuildByName();

        private static Dictionary<Key, string> BuildNames()
        {
            Dictionary<Key, string> names = new Dictionary<Key, string>();
            for (Key k = Key.D0; k <= Key.D9; k++)
            {
                names[k] = ((int)(k - Key.D0)).ToString();
            }

            for (Key k = Key.NumPad0; k <= Key.NumPad9; k++)
            {
                names[k] = "Num" + (int)(k - Key.NumPad0);
            }

            names[Key.Return] = "Enter";
            names[Key.Escape] = "Esc";
            names[Key.Back] = "Backspace";
            names[Key.PageUp] = "PageUp";
            names[Key.PageDown] = "PageDown";
            names[Key.Capital] = "CapsLock";
            names[Key.Snapshot] = "PrintScreen";
            names[Key.OemPlus] = "=";
            names[Key.OemMinus] = "-";
            names[Key.OemComma] = ",";
            names[Key.OemPeriod] = ".";
            names[Key.OemQuestion] = "/";
            names[Key.OemSemicolon] = ";";
            names[Key.OemQuotes] = "'";
            names[Key.OemOpenBrackets] = "[";
            names[Key.OemCloseBrackets] = "]";
            names[Key.OemPipe] = "\\";
            names[Key.OemTilde] = "`";
            names[Key.Multiply] = "Num*";
            names[Key.Add] = "Num+";
            names[Key.Subtract] = "Num-";
            names[Key.Divide] = "Num/";
            names[Key.Decimal] = "Num.";
            return names;
        }

        private static Dictionary<string, Key> BuildByName()
        {
            Dictionary<string, Key> byName = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<Key, string> pair in BuildNames())
            {
                byName[pair.Value] = pair.Key;
            }

            byName["Return"] = Key.Return;
            byName["Escape"] = Key.Escape;
            byName["Del"] = Key.Delete;
            byName["Ins"] = Key.Insert;
            byName["PgUp"] = Key.PageUp;
            byName["PgDn"] = Key.PageDown;
            byName["Page Up"] = Key.PageUp;
            byName["Page Down"] = Key.PageDown;
            byName["Spacebar"] = Key.Space;
            byName["Arrow Up"] = Key.Up;
            byName["Arrow Down"] = Key.Down;
            byName["Arrow Left"] = Key.Left;
            byName["Arrow Right"] = Key.Right;
            return byName;
        }

        private KeyChord()
        {
        }

        /// <summary>
        /// Canonical text: Win, Ctrl, Alt, Shift, then the key.
        /// </summary>
        public string Text
        {
            get
            {
                List<string> parts = new List<string>();
                if (Win) parts.Add("Win");
                if (Ctrl) parts.Add("Ctrl");
                if (Alt) parts.Add("Alt");
                if (Shift) parts.Add("Shift");
                parts.Add(KeyName(Key));
                return string.Join("+", parts.ToArray());
            }
        }

        public override string ToString()
        {
            return Text;
        }

        public static string KeyName(Key key)
        {
            string name;
            return Names.TryGetValue(key, out name) ? name : key.ToString();
        }

        public static bool IsModifierKey(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// From a key press (recording). Null for a modifier on its own.
        /// </summary>
        public static KeyChord FromPress(ModifierKeys modifiers, Key key)
        {
            if (key == Key.None || IsModifierKey(key))
            {
                return null;
            }

            KeyChord chord = new KeyChord();
            chord.Win = (modifiers & ModifierKeys.Windows) != 0;
            chord.Ctrl = (modifiers & ModifierKeys.Control) != 0;
            chord.Alt = (modifiers & ModifierKeys.Alt) != 0;
            chord.Shift = (modifiers & ModifierKeys.Shift) != 0;
            chord.Key = key;
            return chord;
        }

        public static bool TryParse(string text, out KeyChord chord, out string error)
        {
            chord = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Press the key combination.";
                return false;
            }

            KeyChord result = new KeyChord();
            bool haveKey = false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                {
                    // "Ctrl++" style: a literal plus is written "=" (the key it shares).
                    error = "“" + text.Trim() + "” is not a key combination.";
                    return false;
                }

                string modifier = part.ToLowerInvariant();
                if (modifier.StartsWith("left ", StringComparison.Ordinal) || modifier.StartsWith("right ", StringComparison.Ordinal))
                {
                    modifier = modifier.Substring(modifier.IndexOf(' ') + 1).Trim();
                }

                switch (modifier)
                {
                    case "ctrl":
                    case "control":
                    case "lctrl":
                    case "rctrl":
                        result.Ctrl = true;
                        continue;
                    case "alt":
                    case "lalt":
                    case "ralt":
                        result.Alt = true;
                        continue;
                    case "shift":
                    case "lshift":
                    case "rshift":
                        result.Shift = true;
                        continue;
                    case "win":
                    case "windows":
                    case "lwin":
                    case "rwin":
                        result.Win = true;
                        continue;
                }

                Key key;
                if (!TryParseKey(part, out key))
                {
                    error = "Unknown key “" + part + "”.";
                    return false;
                }

                if (haveKey)
                {
                    error = "Only one key besides Ctrl, Alt, Shift and Win.";
                    return false;
                }

                result.Key = key;
                haveKey = true;
            }

            if (!haveKey)
            {
                error = "Add a key after the modifiers (e.g. Ctrl+Shift+D).";
                return false;
            }

            chord = result;
            return true;
        }

        private static bool TryParseKey(string text, out Key key)
        {
            if (ByName.TryGetValue(text, out key))
            {
                return true;
            }

            if (text.Length == 1 && char.IsLetter(text[0]))
            {
                return Enum.TryParse(text.ToUpperInvariant(), out key);
            }

            if (Enum.TryParse(text.Replace(" ", ""), true, out key) && key != Key.None && !IsModifierKey(key)
                && Enum.IsDefined(typeof(Key), key))
            {
                return true;
            }

            key = Key.None;
            return false;
        }

        /// <summary>
        /// Canonical text for anything parseable, or null.
        /// </summary>
        public static string Canonical(string text)
        {
            KeyChord chord;
            string error;
            return TryParse(text, out chord, out error) ? chord.Text : null;
        }

        /// <summary>
        /// Virtual-key codes, modifiers first, main key last.
        /// </summary>
        public ushort[] VirtualKeys()
        {
            List<ushort> keys = new List<ushort>();
            if (Win) keys.Add(Win32Input.VK_LWIN);
            if (Ctrl) keys.Add(Win32Input.VK_CONTROL);
            if (Alt) keys.Add(Win32Input.VK_MENU);
            if (Shift) keys.Add(Win32Input.VK_SHIFT);
            keys.Add((ushort)KeyInterop.VirtualKeyFromKey(Key));
            return keys.ToArray();
        }
    }
}
