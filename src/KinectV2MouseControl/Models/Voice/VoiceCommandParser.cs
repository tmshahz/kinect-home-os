using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KinectV2MouseControl
{
    public enum VoiceIntentKind
    {
        /// <summary>
        /// A ControlAction for the ActionRouter.
        /// </summary>
        Action,

        /// <summary>
        /// Something the control center does itself ("open", "compact", "calibrate").
        /// </summary>
        Shell,

        /// <summary>
        /// "Cancel" / "never mind": close the command window without doing anything.
        /// </summary>
        Cancel,

        /// <summary>
        /// A user-defined command. CommandId is its id; the view model looks up what it does.
        /// </summary>
        Custom,
        Request
    }

    /// <summary>
    /// What a recognized command phrase means. Produced only by VoiceCommandParser, and only
    /// ever executed by the voice view model after the wake-gated engine has authorized it.
    /// </summary>
    public sealed class VoiceIntent
    {
        public VoiceIntentKind Kind { get; private set; }
        public ControlAction Action { get; private set; }
        public string ShellCommand { get; private set; }

        /// <summary>
        /// Catalog id of the command ("setvolume", "mute", ...).
        /// </summary>
        public string CommandId { get; private set; }

        /// <summary>
        /// The normalized phrase that produced this intent.
        /// </summary>
        public string Canonical { get; private set; }

        /// <summary>
        /// Short confirmation for the HUD: "Volume → 70%", "Next window".
        /// </summary>
        public string Feedback { get; private set; }

        public static VoiceIntent ForAction(string commandId, ControlAction action, string canonical, string feedback)
        {
            VoiceIntent intent = new VoiceIntent();
            intent.Kind = VoiceIntentKind.Action;
            intent.Action = action;
            intent.CommandId = commandId;
            intent.Canonical = canonical;
            intent.Feedback = feedback;
            return intent;
        }

        public static VoiceIntent ForShell(string commandId, string shellCommand, string canonical, string feedback)
        {
            VoiceIntent intent = new VoiceIntent();
            intent.Kind = VoiceIntentKind.Shell;
            intent.ShellCommand = shellCommand;
            intent.CommandId = commandId;
            intent.Canonical = canonical;
            intent.Feedback = feedback;
            return intent;
        }

        public static VoiceIntent ForCustom(string customId, string canonical, string feedback)
        {
            VoiceIntent intent = new VoiceIntent();
            intent.Kind = VoiceIntentKind.Custom;
            intent.CommandId = customId;
            intent.Canonical = canonical;
            intent.Feedback = feedback;
            return intent;
        }

        public static VoiceIntent ForCancel(string canonical)
        {
            VoiceIntent intent = new VoiceIntent();
            intent.Kind = VoiceIntentKind.Cancel;
            intent.CommandId = "cancel";
            intent.Canonical = canonical;
            intent.Feedback = "Cancelled";
            return intent;
        }

        public static VoiceIntent ForRequest(string transcript)
        {
            return new VoiceIntent { Kind = VoiceIntentKind.Request, Canonical = transcript, Feedback = transcript };
        }

        public override string ToString()
        {
            return Feedback;
        }
    }

    /// <summary>
    /// Spoken English cardinal numbers, 0-999, in the forms people actually say to a volume
    /// control: "seventy five", "seventy-five", "one hundred", "a hundred", "two hundred and
    /// five", or plain digits. Strict: anything that is not a well-formed number is refused
    /// rather than guessed at, so "volume fifty something" never becomes 50.
    /// </summary>
    public static class SpokenNumber
    {
        public static readonly string[] SmallWords =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
        };

        public static readonly string[] TensWords =
        {
            null, null, "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        private static readonly Dictionary<string, int> Small = BuildIndex(SmallWords, 1);
        private static readonly Dictionary<string, int> Tens = BuildIndex(TensWords, 10);

        private static Dictionary<string, int> BuildIndex(string[] words, int scale)
        {
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i] != null)
                {
                    index[words[i]] = i * scale;
                }
            }

            return index;
        }

        /// <summary>
        /// Words for 0-999 exactly as the command grammar lists them ("forty three",
        /// "two hundred five"). The parser accepts these and the common variants.
        /// </summary>
        public static string ToWords(int value)
        {
            if (value < 0 || value > 999)
            {
                throw new ArgumentOutOfRangeException("value");
            }

            if (value < 20)
            {
                return SmallWords[value];
            }

            if (value < 100)
            {
                int unit = value % 10;
                return TensWords[value / 10] + (unit == 0 ? "" : " " + SmallWords[unit]);
            }

            int rest = value % 100;
            return SmallWords[value / 100] + " hundred" + (rest == 0 ? "" : " " + ToWords(rest));
        }

        /// <summary>
        /// Parses digits or number words. The input must already be normalized (lower case,
        /// single spaces, no punctuation - see VoiceCommandParser.Normalize).
        /// </summary>
        public static bool TryParse(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (IsAllDigits(text))
            {
                // Digits beyond three places are not a volume anyone says; refuse rather than
                // overflow.
                return text.Length <= 3 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            }

            string[] tokens = text.Split(' ');
            int i = 0;
            int total = 0;
            bool any = false;

            // Optional hundreds: "<one..nine> hundred" or "a hundred", then an optional "and".
            if (i + 1 < tokens.Length && tokens[i + 1] == "hundred")
            {
                int hundreds;
                if (tokens[i] == "a")
                {
                    hundreds = 1;
                }
                else if (!Small.TryGetValue(tokens[i], out hundreds) || hundreds < 1 || hundreds > 9)
                {
                    return false;
                }

                total = hundreds * 100;
                i += 2;
                any = true;

                if (i < tokens.Length && tokens[i] == "and")
                {
                    i++;
                    if (i >= tokens.Length)
                    {
                        return false;
                    }
                }
            }

            // Below one hundred: "<tens> [<one..nine>]" or "<zero..nineteen>".
            if (i < tokens.Length)
            {
                int tens;
                int small;
                if (Tens.TryGetValue(tokens[i], out tens))
                {
                    total += tens;
                    i++;
                    if (i < tokens.Length && Small.TryGetValue(tokens[i], out small) && small >= 1 && small <= 9)
                    {
                        total += small;
                        i++;
                    }
                }
                else if (Small.TryGetValue(tokens[i], out small))
                {
                    // "two hundred zero" is not a number.
                    if (any && small == 0)
                    {
                        return false;
                    }

                    total += small;
                    i++;
                }
                else
                {
                    return false;
                }

                any = true;
            }

            if (!any || i != tokens.Length)
            {
                return false;
            }

            value = total;
            return true;
        }

        private static bool IsAllDigits(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Deterministic command parser: normalized text in, one intent (or a reason) out.
    ///
    /// No substring or fuzzy matching of any kind. A phrase is either exactly one of the
    /// catalog phrases, exactly the volume pattern with a well-formed number, or it is refused.
    /// "I was playing something earlier and then paused it" normalizes to a sentence that is
    /// none of those, so it can never become Play or Pause - even if it somehow reached here.
    ///
    /// The parser is the last word on meaning. The speech grammar only limits what the
    /// recognizer can hear; this decides what a heard phrase does. A future assistant fallback
    /// would sit after a refusal here, never before it.
    /// </summary>
    public static class VoiceCommandParser
    {
        public const int MaxVolume = 100;

        /// <summary>
        /// "[set [the]] volume [to|at] N [percent]"
        /// </summary>
        private static readonly Regex VolumePattern = new Regex(
            @"^(?:set (?:the )?)?volume (?:to |at )?(?<n>.+?)(?: percent)?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Dictionary<string, VoiceCommand> PhraseIndex = BuildPhraseIndex();

        private static Dictionary<string, VoiceCommand> BuildPhraseIndex()
        {
            Dictionary<string, VoiceCommand> index = new Dictionary<string, VoiceCommand>(StringComparer.Ordinal);
            foreach (VoiceCommand command in VoiceCommandCatalog.BuiltIn)
            {
                if (command.Kind == VoiceCommandKind.VolumePattern || !command.IsAvailable)
                {
                    continue;
                }

                foreach (string phrase in command.AllPhrases())
                {
                    string key = Normalize(phrase);
                    if (key.Length > 0 && !index.ContainsKey(key))
                    {
                        index.Add(key, command);
                    }
                }
            }

            return index;
        }

        /// <summary>
        /// Lower case, hyphens as spaces, "%" as "percent", everything but letters, digits and
        /// spaces removed, single spaces, trimmed.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            StringBuilder builder = new StringBuilder(text.Length + 8);
            bool lastWasSpace = true;
            foreach (char raw in text.ToLowerInvariant())
            {
                char c = raw;
                if (c == '%')
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                    }

                    builder.Append("percent");
                    lastWasSpace = false;
                    continue;
                }

                if (c == '-' || c == '_' || char.IsWhiteSpace(c))
                {
                    c = ' ';
                }
                else if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                {
                    // Apostrophes, full stops, commas and the like carry no meaning here.
                    continue;
                }

                if (c == ' ')
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                    }

                    lastWasSpace = true;
                }
                else
                {
                    builder.Append(c);
                    lastWasSpace = false;
                }
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// Built-in phrases and the volume pattern only.
        /// </summary>
        public static bool TryParse(string text, out VoiceIntent intent, out string reason)
        {
            return TryParse(text, null, out intent, out reason);
        }

        /// <summary>
        /// True when <paramref name="phrase"/> already means something built in: a catalog
        /// phrase (or alias), "cancel", or a volume form. A wake word or custom phrase may not
        /// be one of these.
        /// </summary>
        public static bool IsBuiltInCommand(string phrase)
        {
            VoiceIntent intent;
            string reason;
            if (TryParse(phrase, null, out intent, out reason))
            {
                return true;
            }

            // Also refuse forms the grammar would read as a volume ("volume max" is not a
            // volume, but a custom command must not shadow the volume pattern either).
            string normalized = Normalize(phrase);
            return PhraseIndex.ContainsKey(normalized) || VolumePattern.IsMatch(normalized);
        }

        /// <summary>
        /// Order: exact built-in phrases, then the user's custom phrases (exact, by match key),
        /// then the volume pattern. No substring or fuzzy matching at any step.
        /// </summary>
        public static bool TryParse(string text, CustomPhraseSet custom, out VoiceIntent intent, out string reason)
        {
            intent = null;
            reason = null;

            string normalized = Normalize(text);
            if (normalized.Length == 0)
            {
                reason = "nothing to parse";
                return false;
            }

            // Exact catalog phrases first, so "volume up" is never read as "volume <number>".
            VoiceCommand command;
            if (PhraseIndex.TryGetValue(normalized, out command))
            {
                intent = ToIntent(command, normalized);
                if (intent == null)
                {
                    reason = "“" + normalized + "” has no action behind it yet";
                    return false;
                }

                return true;
            }

            CustomPhraseSet.Entry entry;
            if (custom != null && custom.TryMatch(text, out entry))
            {
                intent = VoiceIntent.ForCustom(entry.Id, entry.Key, entry.Phrase);
                return true;
            }

            Match match = VolumePattern.Match(normalized);
            if (match.Success)
            {
                string numberText = match.Groups["n"].Value;
                int value;
                if (!SpokenNumber.TryParse(numberText, out value))
                {
                    reason = "“" + numberText + "” is not a volume level";
                    return false;
                }

                if (value > MaxVolume)
                {
                    reason = "volume " + value + " is out of range (0-" + MaxVolume + ")";
                    return false;
                }

                ControlAction action = ControlAction.SetVolumeTo(value);
                intent = VoiceIntent.ForAction("setvolume", action, normalized, action.ToString());
                return true;
            }

            reason = "“" + normalized + "” is not a KINECT-OS command";
            return false;
        }

        private static VoiceIntent ToIntent(VoiceCommand command, string normalized)
        {
            if (command.Kind == VoiceCommandKind.Cancel)
            {
                return VoiceIntent.ForCancel(normalized);
            }

            ActionDescriptor descriptor = command.Action;
            if (descriptor == null || !descriptor.IsImplemented)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(descriptor.ShellCommand))
            {
                return VoiceIntent.ForShell(command.Id, descriptor.ShellCommand, normalized, descriptor.Name);
            }

            if (descriptor.Action.Type == ControlActionType.None)
            {
                return null;
            }

            return VoiceIntent.ForAction(command.Id, descriptor.Action, normalized, descriptor.Name);
        }
    }
}
