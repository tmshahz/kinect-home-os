using System;
using System.Collections.Generic;
using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Recognition.SrgsGrammar;

namespace KinectV2MouseControl
{
    /// <summary>
    /// The two closed grammars the wake-gated engine switches between. There is no grammar that
    /// contains both the wake word and a command, so no single utterance can ever be both.
    ///
    /// Wake: exactly one word, "Kinect", with its pronunciation given explicitly (IPA
    /// /kɪˈnɛkt/) so the recognizer does not have to guess it by letter-to-sound rules. The
    /// schwa form /kəˈnɛkt/ is deliberately not listed: that is "connect", which people do say.
    ///
    /// Commands: every catalog phrase plus the volume forms. Volume numbers include 101-999 on
    /// purpose, so "volume two hundred" is heard as two hundred and refused by the parser,
    /// instead of being bent to the nearest listed number ("one hundred") and executed.
    /// </summary>
    internal static class VoiceGrammars
    {
        public const string WakeGrammarName = "KINECT-OS wake word";
        public const string CommandGrammarName = "KINECT-OS commands";

        /// <summary>
        /// /kɪˈnɛkt/
        /// </summary>
        public const string WakePronunciationIpa = "kɪˈnɛkt";

        public static Grammar BuildWake(CultureInfo culture)
        {
            try
            {
                SrgsRule rule = new SrgsRule("wake");
                SrgsToken token = new SrgsToken(VoiceCommandCatalog.WakeWord);
                token.Pronunciation = WakePronunciationIpa;
                rule.Add(new SrgsItem(token));

                SrgsDocument document = new SrgsDocument();
                document.Culture = culture;
                document.Mode = SrgsGrammarMode.Voice;
                document.PhoneticAlphabet = SrgsPhoneticAlphabet.Ipa;
                document.Rules.Add(rule);
                document.Root = rule;

                Grammar grammar = new Grammar(document);
                grammar.Name = WakeGrammarName;
                return grammar;
            }
            catch (Exception ex)
            {
                // Letter-to-sound gives the same pronunciation on the en-US recognizer; the
                // explicit form is belt and braces.
                RuntimeLog.Write("Wake grammar pronunciation not accepted (" + ex.Message + "); using letter-to-sound");
                GrammarBuilder builder = new GrammarBuilder(VoiceCommandCatalog.WakeWord);
                builder.Culture = culture;
                Grammar grammar = new Grammar(builder);
                grammar.Name = WakeGrammarName;
                return grammar;
            }
        }

        /// <summary>
        /// Built as an explicit SRGS document with named rules. The GrammarBuilder form of the
        /// same thing - "volume" followed by the structured number, inside a choice - fails to
        /// compile in System.Speech ("'' rule reference not defined"); named rules sidestep that
        /// and make the structure plain.
        /// </summary>
        public static Grammar BuildCommands(CultureInfo culture)
        {
            SrgsDocument document = new SrgsDocument();
            document.Culture = culture;
            document.Mode = SrgsGrammarMode.Voice;

            SrgsRule below100 = WordRule("below100", 0);
            SrgsRule below100NonZero = WordRule("below100nonzero", 1);
            SrgsRule number = NumberRule(below100, below100NonZero);
            SrgsRule volume = VolumeRule(number);

            SrgsOneOf all = new SrgsOneOf();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int phrases = 0;

            foreach (VoiceCommand command in VoiceCommandCatalog.BuiltIn)
            {
                if (!command.IsAvailable)
                {
                    continue;
                }

                if (command.Kind == VoiceCommandKind.VolumePattern)
                {
                    all.Add(new SrgsItem(new SrgsRuleRef(volume)));
                    phrases++;
                    continue;
                }

                foreach (string phrase in command.AllPhrases())
                {
                    if (!string.IsNullOrWhiteSpace(phrase) && seen.Add(phrase))
                    {
                        all.Add(new SrgsItem(phrase));
                        phrases++;
                    }
                }
            }

            if (phrases == 0)
            {
                throw new InvalidOperationException("No available voice commands to listen for.");
            }

            SrgsRule root = new SrgsRule("command");
            root.Scope = SrgsRuleScope.Public;
            root.Add(all);

            document.Rules.Add(below100, below100NonZero, number, volume, root);
            document.Root = root;

            Grammar grammar = new Grammar(document);
            grammar.Name = CommandGrammarName;
            return grammar;
        }

        /// <summary>
        /// "volume N [percent]" or "set volume [to] N [percent]".
        /// </summary>
        private static SrgsRule VolumeRule(SrgsRule number)
        {
            SrgsItem plain = new SrgsItem();
            plain.Add(new SrgsItem("volume"));
            plain.Add(new SrgsRuleRef(number));
            plain.Add(new SrgsItem(0, 1, "percent"));

            SrgsItem set = new SrgsItem();
            set.Add(new SrgsItem("set volume"));
            set.Add(new SrgsItem(0, 1, "to"));
            set.Add(new SrgsRuleRef(number));
            set.Add(new SrgsItem(0, 1, "percent"));

            SrgsRule rule = new SrgsRule("volume");
            rule.Add(new SrgsOneOf(plain, set));
            return rule;
        }

        /// <summary>
        /// 0-999 in words: "zero".."ninety nine", "(one-nine) hundred [[and] 1-99]",
        /// "a hundred".
        /// </summary>
        private static SrgsRule NumberRule(SrgsRule below100, SrgsRule below100NonZero)
        {
            SrgsOneOf forms = new SrgsOneOf();
            forms.Add(new SrgsItem(new SrgsRuleRef(below100)));

            for (int hundreds = 1; hundreds <= 9; hundreds++)
            {
                SrgsItem form = new SrgsItem();
                form.Add(new SrgsItem(SpokenNumber.SmallWords[hundreds] + " hundred"));

                SrgsItem rest = new SrgsItem(0, 1);
                rest.Add(new SrgsItem(0, 1, "and"));
                rest.Add(new SrgsRuleRef(below100NonZero));
                form.Add(rest);

                forms.Add(form);
            }

            forms.Add(new SrgsItem("a hundred"));

            SrgsRule rule = new SrgsRule("number");
            rule.Add(forms);
            return rule;
        }

        private static SrgsRule WordRule(string id, int from)
        {
            SrgsOneOf words = new SrgsOneOf();
            for (int value = from; value < 100; value++)
            {
                words.Add(new SrgsItem(SpokenNumber.ToWords(value)));
            }

            SrgsRule rule = new SrgsRule(id);
            rule.Add(words);
            return rule;
        }
    }
}
