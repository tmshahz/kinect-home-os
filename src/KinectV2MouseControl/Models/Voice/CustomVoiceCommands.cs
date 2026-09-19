using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace KinectV2MouseControl
{
    /// <summary>
    /// What a custom voice command does.
    /// </summary>
    public enum CustomCommandKind
    {
        /// <summary>
        /// Presses a key combination in the window in front, optionally only when a named app is
        /// the one in front ("Claude listen" → Ctrl+Shift+D, only in Claude).
        /// </summary>
        Keys,

        /// <summary>
        /// Opens a program, shortcut, file or URL.
        /// </summary>
        OpenApp,

        /// <summary>
        /// Runs one of the built-in catalog actions under a phrase of the user's choosing.
        /// </summary>
        Action
    }

    /// <summary>
    /// One user-defined command as stored on disk. Kind is kept as text so a file written by a
    /// newer build with a kind this build does not know still loads (the row shows as invalid).
    /// </summary>
    [DataContract]
    public sealed class CustomCommandDefinition
    {
        [DataMember] public string Id;
        [DataMember] public string Phrase;
        [DataMember] public bool Enabled;
        [DataMember] public string Kind;
        [DataMember(EmitDefaultValue = false)] public string Keys;
        [DataMember(EmitDefaultValue = false)] public string OnlyInApp;
        [DataMember(EmitDefaultValue = false)] public string Target;
        [DataMember(EmitDefaultValue = false)] public string ActionId;

        public const string KindKeys = "keys";
        public const string KindOpenApp = "app";
        public const string KindAction = "action";

        public static string KindToText(CustomCommandKind kind)
        {
            switch (kind)
            {
                case CustomCommandKind.OpenApp: return KindOpenApp;
                case CustomCommandKind.Action: return KindAction;
                default: return KindKeys;
            }
        }

        public static bool TryParseKind(string text, out CustomCommandKind kind)
        {
            switch (text)
            {
                case KindKeys: kind = CustomCommandKind.Keys; return true;
                case KindOpenApp: kind = CustomCommandKind.OpenApp; return true;
                case KindAction: kind = CustomCommandKind.Action; return true;
                default: kind = CustomCommandKind.Keys; return false;
            }
        }

        public static CustomCommandDefinition Create(string phrase, CustomCommandKind kind)
        {
            CustomCommandDefinition definition = new CustomCommandDefinition();
            definition.Id = Guid.NewGuid().ToString("N");
            definition.Phrase = phrase ?? "";
            definition.Enabled = true;
            definition.Kind = KindToText(kind);
            return definition;
        }

        /// <summary>
        /// What a fresh install starts with: one dictation command per app, keys left blank for
        /// the user to fill in (each app has its own shortcut, and they change between versions).
        /// </summary>
        public static List<CustomCommandDefinition> Starters()
        {
            List<CustomCommandDefinition> starters = new List<CustomCommandDefinition>();
            starters.Add(Starter("GPT listen", "ChatGPT"));
            starters.Add(Starter("Claude listen", "claude"));
            starters.Add(Starter("Cursor listen", "Cursor"));
            return starters;
        }

        private static CustomCommandDefinition Starter(string phrase, string app)
        {
            CustomCommandDefinition definition = Create(phrase, CustomCommandKind.Keys);
            definition.OnlyInApp = app;
            return definition;
        }
    }

    [DataContract]
    public sealed class CustomCommandFile
    {
        [DataMember] public int Version = 1;
        [DataMember] public CustomCommandDefinition[] Commands;
    }

    /// <summary>
    /// Custom voice commands on disk: %LOCALAPPDATA%\KinectHomeOS\voice-commands.json, shared by
    /// the Debug and Release builds like the profiles. Written atomically; a file that cannot
    /// be read is set aside as voice-commands.json.bad and the defaults are used.
    /// </summary>
    public static class CustomCommandStore
    {
        private static readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CustomCommandFile));

        public static string FilePath
        {
            get { return Path.Combine(RuntimeLog.DirectoryPath, "voice-commands.json"); }
        }

        /// <summary>
        /// The saved commands, or null when nothing has been saved yet (the caller then offers
        /// the starters). Never throws.
        /// </summary>
        public static List<CustomCommandDefinition> Load(out string warning)
        {
            warning = null;
            string path = FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                CustomCommandFile file;
                using (FileStream stream = File.OpenRead(path))
                {
                    file = (CustomCommandFile)serializer.ReadObject(stream);
                }

                List<CustomCommandDefinition> commands = new List<CustomCommandDefinition>();
                if (file != null && file.Commands != null)
                {
                    foreach (CustomCommandDefinition command in file.Commands)
                    {
                        if (command == null)
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(command.Id))
                        {
                            command.Id = Guid.NewGuid().ToString("N");
                        }

                        commands.Add(command);
                    }
                }

                return commands;
            }
            catch (Exception ex)
            {
                warning = "Custom commands file unreadable, set aside as voice-commands.json.bad (" + ex.GetType().Name + ")";
                RuntimeLog.Write(warning + ": " + ex.Message);
                try
                {
                    string bad = path + ".bad";
                    if (File.Exists(bad))
                    {
                        File.Delete(bad);
                    }

                    File.Move(path, bad);
                }
                catch (Exception)
                {
                    // Leave it; the next save overwrites it.
                }

                return null;
            }
        }

        public static bool Save(IList<CustomCommandDefinition> commands, out string error)
        {
            error = null;
            string path = FilePath;
            string temporary = path + ".tmp";
            try
            {
                CustomCommandFile file = new CustomCommandFile();
                file.Commands = new CustomCommandDefinition[commands.Count];
                commands.CopyTo(file.Commands, 0);

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream stream = File.Create(temporary))
                using (XmlDictionaryWriter writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                {
                    serializer.WriteObject(writer, file);
                    writer.Flush();
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                RuntimeLog.Write("Custom commands save failed: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// The rules a custom command must meet before it joins the grammar. A command that breaks
    /// one stays in the list (so nothing typed is lost) but is not listened for.
    /// </summary>
    public static class CustomCommandRules
    {
        public const int MaxWords = 6;

        /// <summary>
        /// Checks the phrase on its own: form, clashes with built-in commands and the wake word.
        /// Duplicates between custom commands are the caller's to check (it has the list).
        /// </summary>
        public static string CheckPhrase(string phrase, string wakeWord)
        {
            string cleaned = VoicePhrases.Clean(phrase);
            if (cleaned.Length == 0)
            {
                return "Type the phrase to say after the chime.";
            }

            if (!VoicePhrases.HasOnlyWordCharacters(cleaned))
            {
                return "Letters only - write numbers as words.";
            }

            string key = VoicePhrases.MatchKey(cleaned);
            if (key.Length == 0)
            {
                return "Type the phrase to say after the chime.";
            }

            if (VoicePhrases.WordCount(cleaned) > MaxWords)
            {
                return "Keep it to six words or fewer.";
            }

            if (VoiceCommandParser.IsBuiltInCommand(cleaned))
            {
                return "“" + cleaned + "” is already a built-in command.";
            }

            if (!string.IsNullOrEmpty(wakeWord) && VoicePhrases.ContainsWords(key, VoicePhrases.MatchKey(wakeWord)))
            {
                return "A command can't contain the wake word.";
            }

            return null;
        }

        /// <summary>
        /// Built-in actions a custom phrase may run. Mouse-button holds, the volume level (it
        /// needs a number) and the custom-only kinds are excluded.
        /// </summary>
        public static List<ActionDescriptor> AssignableActions()
        {
            List<ActionDescriptor> actions = new List<ActionDescriptor>();
            foreach (ActionDescriptor descriptor in ActionCatalog.All)
            {
                if (IsAssignable(descriptor))
                {
                    actions.Add(descriptor);
                }
            }

            return actions;
        }

        public static bool IsAssignable(ActionDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsImplemented)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(descriptor.ShellCommand))
            {
                return true;
            }

            switch (descriptor.Action.Type)
            {
                case ControlActionType.None:
                case ControlActionType.LeftMouseDown:
                case ControlActionType.LeftMouseUp:
                case ControlActionType.SetVolume:
                case ControlActionType.SendKeys:
                case ControlActionType.LaunchApp:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// The custom phrases the running recognizer listens for, frozen when it starts. Maps what
    /// was heard back to the command's id; the view model looks the command up by id when it
    /// runs it, so what a phrase does can change without restarting the recognizer.
    /// </summary>
    public sealed class CustomPhraseSet
    {
        public static readonly CustomPhraseSet Empty = new CustomPhraseSet(new List<KeyValuePair<string, string>>());

        public sealed class Entry
        {
            public string Id { get; internal set; }
            public string Phrase { get; internal set; }
            public string Key { get; internal set; }
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <param name="idAndPhrase">Command id → phrase as typed. Later duplicates are dropped.</param>
        public CustomPhraseSet(IEnumerable<KeyValuePair<string, string>> idAndPhrase)
        {
            StringBuilder signature = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in idAndPhrase)
            {
                string key = VoicePhrases.MatchKey(pair.Value);
                if (key.Length == 0 || byKey.ContainsKey(key))
                {
                    continue;
                }

                Entry entry = new Entry();
                entry.Id = pair.Key;
                entry.Phrase = VoicePhrases.Clean(pair.Value);
                entry.Key = key;
                entries.Add(entry);
                byKey.Add(key, entry);
                signature.Append(pair.Key).Append('=').Append(key).Append('|');
            }

            Signature = signature.ToString();
        }

        public int Count
        {
            get { return entries.Count; }
        }

        public IList<Entry> Entries
        {
            get { return entries.AsReadOnly(); }
        }

        /// <summary>
        /// Identifies the grammar-relevant content: equal signatures need no recognizer restart.
        /// </summary>
        public string Signature { get; private set; }

        public bool TryMatch(string text, out Entry entry)
        {
            return byKey.TryGetValue(VoicePhrases.MatchKey(text), out entry);
        }
    }
}
