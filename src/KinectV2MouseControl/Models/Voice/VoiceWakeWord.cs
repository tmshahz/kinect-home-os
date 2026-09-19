using System;
using System.Collections.Generic;
using System.Text;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Phrase handling shared by the wake word and custom commands: how a typed phrase becomes
    /// grammar tokens, and the key both a typed phrase and a recognized one are compared by.
    ///
    /// Acronyms are spelled. "GPT" has no pronunciation the recognizer can guess, so a token
    /// typed in capitals (up to four letters) or one without a vowel is listed letter by letter
    /// ("G P T"). The recognizer then reports "G P T listen", and the match key joins runs of
    /// single letters back together, so "GPT listen", "gpt listen" and "G P T listen" are all the
    /// same phrase.
    /// </summary>
    public static class VoicePhrases
    {
        /// <summary>
        /// Letters, spaces, apostrophes and hyphens only. Digits are refused rather than guessed
        /// at: the recognizer has no fixed way to say "2".
        /// </summary>
        public static bool HasOnlyWordCharacters(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!(char.IsLetter(c) || c == ' ' || c == '\'' || c == '’' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Trimmed, with single spaces between words; case kept (it decides acronym spelling).
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            string[] words = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words);
        }

        /// <summary>
        /// Words as typed ("GPT listen" is two, "a b c" is three).
        /// </summary>
        public static int WordCount(string text)
        {
            string cleaned = Clean(text);
            return cleaned.Length == 0 ? 0 : cleaned.Split(' ').Length;
        }

        /// <summary>
        /// The comparison key: VoiceCommandParser.Normalize, then runs of single letters joined
        /// ("g p t listen" → "gpt listen").
        /// </summary>
        public static string MatchKey(string text)
        {
            string normalized = VoiceCommandParser.Normalize(text);
            if (normalized.Length == 0)
            {
                return "";
            }

            string[] tokens = normalized.Split(' ');
            StringBuilder key = new StringBuilder(normalized.Length);
            bool previousWasLetter = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                bool isLetter = tokens[i].Length == 1 && tokens[i][0] >= 'a' && tokens[i][0] <= 'z';
                if (key.Length > 0 && !(isLetter && previousWasLetter))
                {
                    key.Append(' ');
                }

                key.Append(tokens[i]);
                previousWasLetter = isLetter;
            }

            return key.ToString();
        }

        /// <summary>
        /// True when <paramref name="phraseKey"/> contains <paramref name="wordsKey"/> as whole
        /// words (both already match keys).
        /// </summary>
        public static bool ContainsWords(string phraseKey, string wordsKey)
        {
            if (phraseKey.Length == 0 || wordsKey.Length == 0)
            {
                return false;
            }

            return (" " + phraseKey + " ").IndexOf(" " + wordsKey + " ", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// One grammar token: its text and, when known, an explicit IPA pronunciation.
        /// </summary>
        public struct Token
        {
            public string Text;
            public string Pronunciation;
        }

        /// <summary>
        /// The tokens a phrase is listed as in a grammar: acronyms spelled, "Kinect" with its
        /// exact pronunciation, everything else as written (lower case).
        /// </summary>
        public static List<Token> GrammarTokens(string phrase)
        {
            List<Token> tokens = new List<Token>();
            string cleaned = Clean(phrase).Replace('-', ' ').Replace('’', '\'');
            foreach (string raw in cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string word = raw.Trim('\'');
                if (word.Length == 0)
                {
                    continue;
                }

                if (IsSpelledAcronym(word))
                {
                    foreach (char letter in word)
                    {
                        tokens.Add(new Token { Text = char.ToUpperInvariant(letter).ToString() });
                    }

                    continue;
                }

                Token token = new Token { Text = word.ToLowerInvariant() };
                if (token.Text == "kinect")
                {
                    token.Pronunciation = VoiceWakeWord.KinectPronunciationIpa;
                }

                tokens.Add(token);
            }

            return tokens;
        }

        /// <summary>
        /// The phrase as the recognizer will report it ("G P T listen").
        /// </summary>
        public static string GrammarText(string phrase)
        {
            List<Token> tokens = GrammarTokens(phrase);
            string[] words = new string[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                words[i] = tokens[i].Text;
            }

            return string.Join(" ", words);
        }

        private static bool IsSpelledAcronym(string word)
        {
            if (word.Length < 2 || word.Length > 5)
            {
                return false;
            }

            bool allCaps = true;
            bool hasVowel = false;
            foreach (char c in word)
            {
                if (!char.IsLetter(c))
                {
                    return false;
                }

                if (!char.IsUpper(c))
                {
                    allCaps = false;
                }

                if ("aeiouyAEIOUY".IndexOf(c) >= 0)
                {
                    hasVowel = true;
                }
            }

            return (allCaps && word.Length <= 4) || !hasVowel;
        }
    }

    /// <summary>
    /// The wake word: what it may be, and what it defaults to.
    ///
    /// It is the user's to choose (Voice page), one to three words. The isolation checks in
    /// WakeGatedVoiceEngine - a pause before it, not buried in a sentence, a plausible length -
    /// apply to whatever it is, so a custom wake word is protected exactly like the default.
    /// It may not be a command phrase, and custom commands may not contain it: no grammar ever
    /// holds both the wake word and a command.
    /// </summary>
    public static class VoiceWakeWord
    {
        public const string Default = "Jarvis";
        public const int MaxWords = 3;

        /// <summary>
        /// /kɪˈnɛkt/ for the original wake word. The schwa form /kəˈnɛkt/ is "connect", which
        /// people do say, so it is deliberately not the one given.
        /// </summary>
        public const string KinectPronunciationIpa = "kɪˈnɛkt";

        /// <summary>
        /// Checks a typed wake word. On success <paramref name="cleaned"/> is the form to save.
        /// </summary>
        public static bool TryValidate(string text, out string cleaned, out string error)
        {
            cleaned = VoicePhrases.Clean(text);
            error = null;

            if (cleaned.Length == 0)
            {
                error = "Type a wake word.";
            }
            else if (!VoicePhrases.HasOnlyWordCharacters(cleaned))
            {
                error = "Letters only - no numbers or symbols.";
            }
            else if (VoicePhrases.WordCount(cleaned) > MaxWords)
            {
                error = "Use one to three words.";
            }
            else if (VoicePhrases.MatchKey(cleaned).Replace(" ", "").Length < 3)
            {
                error = "Too short to be recognized reliably.";
            }
            else if (VoiceCommandParser.IsBuiltInCommand(cleaned))
            {
                error = "“" + cleaned + "” is already a command.";
            }

            return error == null;
        }

        /// <summary>
        /// Advice rather than a refusal: very short single words are easier to trigger by accident.
        /// </summary>
        public static string Advice(string cleaned)
        {
            string key = VoicePhrases.MatchKey(cleaned);
            if (key.Length > 0 && key.IndexOf(' ') < 0 && key.Length <= 4)
            {
                return "Short single words wake more easily by accident; two or three syllables work best.";
            }

            return "";
        }

        /// <summary>
        /// Longest plausible time for the wake word: one word fits in 1.1 s, each extra word
        /// adds 0.5 s.
        /// </summary>
        public static TimeSpan MaxDuration(string wakeWord)
        {
            int words = Math.Max(1, VoicePhrases.GrammarTokens(wakeWord).Count);
            return TimeSpan.FromMilliseconds(1100 + 500 * (words - 1));
        }
    }
}
