using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace KinectV2MouseControl
{
    /// <summary>
    /// A choice in a custom command's "what it does" dropdown.
    /// </summary>
    public sealed class CustomKindChoice
    {
        public CustomKindChoice(CustomCommandKind kind, string label)
        {
            Kind = kind;
            Label = label;
        }

        public CustomCommandKind Kind { get; private set; }
        public string Label { get; private set; }

        public static readonly CustomKindChoice[] All =
        {
            new CustomKindChoice(CustomCommandKind.Keys, "Key combination"),
            new CustomKindChoice(CustomCommandKind.OpenApp, "Open app / file / URL"),
            new CustomKindChoice(CustomCommandKind.Action, "Built-in action")
        };
    }

    /// <summary>
    /// A choice in the "only when in front" dropdown: a process name, or "" for any window.
    /// </summary>
    public sealed class AppChoice
    {
        public AppChoice(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; private set; }
        public string Label { get; private set; }
    }

    /// <summary>
    /// One editable custom voice command on the Voice page. Writes straight through to its
    /// stored definition and tells the owner (VoiceViewModel) what changed; the owner
    /// validates, saves and decides whether the recognizer needs a restart.
    /// </summary>
    public sealed class CustomCommandRowViewModel : ObservableObject
    {
        public const string AnyApp = "";

        private readonly CustomCommandDefinition definition;
        private readonly Action<CustomCommandRowViewModel, bool> changed;

        /// <param name="changed">Called after every edit; the flag is true when the edit can
        /// change what the recognizer listens for (phrase, on/off, validity).</param>
        public CustomCommandRowViewModel(CustomCommandDefinition definition, Action<CustomCommandRowViewModel, bool> changed,
            Action<CustomCommandRowViewModel> delete)
        {
            this.definition = definition;
            this.changed = changed;

            CustomCommandKind kind;
            if (!CustomCommandDefinition.TryParseKind(definition.Kind, out kind))
            {
                // Unknown kind from a newer build: show it as keys, keep the stored text.
                kind = CustomCommandKind.Keys;
                unknownKind = true;
            }

            this.kind = kind;
            AppChoices = new ObservableCollection<AppChoice>();
            AppChoices.Add(new AppChoice(AnyApp, "Any window"));
            AddAppChoice(definition.OnlyInApp);

            DeleteCommand = new RelayCommand(() => delete(this));
        }

        private readonly bool unknownKind;

        public CustomCommandDefinition Definition
        {
            get { return definition; }
        }

        public string Id
        {
            get { return definition.Id; }
        }

        public RelayCommand DeleteCommand { get; private set; }

        // ---- Phrase and switch ------------------------------------------------------------------

        public string Phrase
        {
            get
            {
                return definition.Phrase ?? "";
            }
            set
            {
                string cleaned = VoicePhrases.Clean(value);
                if (cleaned == Phrase)
                {
                    return;
                }

                definition.Phrase = cleaned;
                Raise("Phrase");
                changed(this, true);
            }
        }

        public bool IsEnabled
        {
            get
            {
                return definition.Enabled;
            }
            set
            {
                if (definition.Enabled == value)
                {
                    return;
                }

                definition.Enabled = value;
                Raise("IsEnabled");
                changed(this, true);
            }
        }

        // ---- What it does -----------------------------------------------------------------------

        private CustomCommandKind kind;

        public CustomCommandKind Kind
        {
            get
            {
                return kind;
            }
            set
            {
                if (kind == value && !HasUnknownKind)
                {
                    return;
                }

                kind = value;
                definition.Kind = CustomCommandDefinition.KindToText(value);
                Raise("Kind");
                Raise("IsKeys");
                Raise("IsOpenApp");
                Raise("IsAction");
                changed(this, true);
            }
        }

        public bool IsKeys { get { return kind == CustomCommandKind.Keys; } }
        public bool IsOpenApp { get { return kind == CustomCommandKind.OpenApp; } }
        public bool IsAction { get { return kind == CustomCommandKind.Action; } }

        /// <summary>
        /// Stored by a newer build with a kind this one does not know, and not changed since.
        /// </summary>
        public bool HasUnknownKind
        {
            get
            {
                CustomCommandKind parsed;
                return unknownKind && !CustomCommandDefinition.TryParseKind(definition.Kind, out parsed);
            }
        }

        /// <summary>
        /// "Ctrl+Shift+D". Anything that parses is stored in canonical form; anything else is
        /// kept as typed and reported by validation.
        /// </summary>
        public string Keys
        {
            get
            {
                return definition.Keys ?? "";
            }
            set
            {
                string text = (value ?? "").Trim();
                string canonical = KeyChord.Canonical(text);
                string stored = canonical ?? text;
                if (stored == Keys)
                {
                    // Still re-raise, so a box showing a lenient spelling snaps to the canonical one.
                    Raise("Keys");
                    return;
                }

                definition.Keys = stored.Length == 0 ? null : stored;
                Raise("Keys");
                changed(this, true);
            }
        }

        /// <summary>
        /// Process name the key combination is limited to, or "" for whatever window is in front.
        /// </summary>
        public string OnlyInApp
        {
            get
            {
                return definition.OnlyInApp ?? AnyApp;
            }
            set
            {
                // The dropdown pushes null while its list is being changed; that is not a choice.
                if (value == null || value == OnlyInApp)
                {
                    return;
                }

                definition.OnlyInApp = value.Length == 0 ? null : value;
                Raise("OnlyInApp");
                changed(this, false);
            }
        }

        public ObservableCollection<AppChoice> AppChoices { get; private set; }

        /// <summary>
        /// Adds any running app not yet offered. Never removes one, so the current choice is
        /// never pulled out from under the dropdown.
        /// </summary>
        public void AddAppChoices(IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                AddAppChoice(name);
            }
        }

        private void AddAppChoice(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            foreach (AppChoice choice in AppChoices)
            {
                if (string.Equals(choice.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            AppChoices.Add(new AppChoice(name, "Only in " + name));
        }

        /// <summary>
        /// Program, shortcut, file or URL to open.
        /// </summary>
        public string Target
        {
            get
            {
                return definition.Target ?? "";
            }
            set
            {
                string text = (value ?? "").Trim().Trim('"');
                if (text == Target)
                {
                    return;
                }

                definition.Target = text.Length == 0 ? null : text;
                Raise("Target");
                changed(this, true);
            }
        }

        public string ActionId
        {
            get
            {
                return definition.ActionId ?? "";
            }
            set
            {
                if (value == null || value == ActionId)
                {
                    return;
                }

                definition.ActionId = value.Length == 0 ? null : value;
                Raise("ActionId");
                changed(this, true);
            }
        }

        // ---- Validation (set by the owner) ------------------------------------------------------

        private string errorText = "";
        public string ErrorText
        {
            get { return errorText; }
            set { if (Set(ref errorText, value ?? "")) { Raise("HasError"); Raise("StatusText"); } }
        }

        public bool HasError
        {
            get { return errorText.Length > 0; }
        }

        /// <summary>
        /// Valid and switched on: in the grammar.
        /// </summary>
        public bool IsListening
        {
            get { return definition.Enabled && errorText.Length == 0; }
        }

        private string summaryText = "";
        /// <summary>
        /// What saying it does, e.g. "Ctrl+Shift+D in claude".
        /// </summary>
        public string SummaryText
        {
            get { return summaryText; }
            private set { if (Set(ref summaryText, value)) { Raise("StatusText"); } }
        }

        public string StatusText
        {
            get
            {
                if (errorText.Length > 0)
                {
                    return errorText;
                }

                return definition.Enabled ? SummaryText : "Off · " + SummaryText;
            }
        }

        /// <summary>
        /// Called by the owner after validation, and after anything that changes the summary.
        /// </summary>
        public void Refresh(string error)
        {
            SummaryText = Describe();
            ErrorText = error;
            Raise("IsListening");
            Raise("StatusText");
        }

        /// <summary>
        /// Plain description of the action, without the phrase.
        /// </summary>
        public string Describe()
        {
            switch (kind)
            {
                case CustomCommandKind.Keys:
                    string keys = Keys.Length > 0 ? Keys : "(no keys yet)";
                    return OnlyInApp.Length > 0 ? "Presses " + keys + " in " + OnlyInApp : "Presses " + keys + " in the window in front";

                case CustomCommandKind.OpenApp:
                    return Target.Length > 0 ? "Opens " + DisplayTarget(Target) : "Opens (nothing chosen yet)";

                default:
                    ActionDescriptor descriptor = ActionCatalog.Find(ActionId);
                    return descriptor != null ? "Runs " + descriptor.Name : "Runs (no action chosen yet)";
            }
        }

        /// <summary>
        /// File name without extension for paths ("Cursor" for ...\Cursor.lnk); URLs and
        /// commands as they are.
        /// </summary>
        public static string DisplayTarget(string target)
        {
            try
            {
                if (target.IndexOf("://", StringComparison.Ordinal) < 0
                    && (target.IndexOf('\\') >= 0 || target.IndexOf('/') >= 0) && target.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                {
                    string name = Path.GetFileNameWithoutExtension(target);
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Not a path.
            }

            return target;
        }
    }
}
