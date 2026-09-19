using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Offline acceptance test for the voice pipeline (KinectV2MouseControl.exe --voice-self-test).
    ///
    /// Uses no microphone, no speakers and executes nothing:
    ///  - the parser and number reader are checked directly;
    ///  - the Core Audio volume interop is checked by reading the level and writing back the
    ///    exact same value;
    ///  - the real WakeGatedVoiceEngine (same grammars, same state machine) is fed synthesized
    ///    speech through a simulated microphone at real-time pace. When the engine "plays" its
    ///    chime, the chime is mixed back into that microphone, like speaker echo, instead of
    ///    being played. Authorized commands are recorded, not executed.
    ///
    /// Synthetic voices are recognized less confidently than a person, so the recognition
    /// scenarios run with lowered thresholds. That makes the negative scenarios (conversation
    /// must produce nothing) stricter than real use, not looser. The positive scenarios prove
    /// the state machine, gate and parser; real-voice confidence is for hardware testing.
    /// </summary>
    internal static class VoiceSelfTest
    {
        private const double TestWakeThreshold = 0.45;
        private const double TestCommandThreshold = 0.45;
        private const double EchoGain = 1.0;

        private static readonly SpeechAudioFormatInfo Format =
            new SpeechAudioFormatInfo(SimulatedMicrophone.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        private const string David = "Microsoft David Desktop";
        private const string Zira = "Microsoft Zira Desktop";

        /// <summary>
        /// The shipped wake word; the scenarios speak it.
        /// </summary>
        private const string Wake = VoiceWakeWord.Default;

        /// <summary>
        /// The starter custom commands, as the recognition scenarios load them.
        /// </summary>
        private static CustomPhraseSet StarterPhrases()
        {
            return new CustomPhraseSet(new[]
            {
                new KeyValuePair<string, string>("gpt", "GPT listen"),
                new KeyValuePair<string, string>("claude", "Claude listen"),
                new KeyValuePair<string, string>("cursor", "Cursor listen")
            });
        }

        public static int Run(out string report)
        {
            StringBuilder text = new StringBuilder();
            int failures = 0;
            text.AppendLine("KINECT-OS voice self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine();

            failures += TestNumbers(text);
            failures += TestParser(text);
            failures += TestCustomCommands(text);
            failures += TestKeyChords(text);
            failures += TestGrammars(text);
            failures += TestSystemVolume(text);
            failures += TestMicrophones(text);
            failures += TestChime(text);
            failures += TestRecognition(text);

            text.AppendLine();
            text.AppendLine(failures == 0 ? "VOICE SELF-TEST PASSED" : "VOICE SELF-TEST FAILED (" + failures + ")");
            report = text.ToString();
            return failures == 0 ? 0 : 3;
        }

        // ---- Parser ---------------------------------------------------------------------------------

        private static int TestNumbers(StringBuilder text)
        {
            int failures = 0;
            for (int value = 0; value <= 999; value++)
            {
                int parsed;
                string words = SpokenNumber.ToWords(value);
                if (!SpokenNumber.TryParse(words, out parsed) || parsed != value)
                {
                    failures++;
                    text.AppendLine("  FAIL number round trip " + value + " '" + words + "'");
                }
            }

            string[][] cases =
            {
                new[] { "seventy five", "75" }, new[] { "one hundred", "100" }, new[] { "a hundred", "100" },
                new[] { "two hundred and five", "205" }, new[] { "43", "43" }, new[] { "zero", "0" },
                new[] { "fifty something", "-" }, new[] { "forty forty", "-" }, new[] { "hundred", "-" },
                new[] { "two hundred zero", "-" }, new[] { "1000", "-" }, new[] { "", "-" }, new[] { "seventy and", "-" }
            };

            foreach (string[] c in cases)
            {
                int parsed;
                bool ok = SpokenNumber.TryParse(c[0], out parsed);
                string actual = ok ? parsed.ToString(CultureInfo.InvariantCulture) : "-";
                if (actual != c[1])
                {
                    failures++;
                    text.AppendLine("  FAIL number '" + c[0] + "' -> " + actual + ", expected " + c[1]);
                }
            }

            text.AppendLine((failures == 0 ? "PASS" : "FAIL") + "  numbers: 0-999 round trip + " + cases.Length + " spoken forms");
            return failures;
        }

        private static int TestParser(StringBuilder text)
        {
            string[][] cases =
            {
                // input, expected (feedback text, or "-" for refused)
                new[] { "volume 0", "Volume → 0%" },
                new[] { "volume 1", "Volume → 1%" },
                new[] { "volume 43", "Volume → 43%" },
                new[] { "volume 73", "Volume → 73%" },
                new[] { "volume 99", "Volume → 99%" },
                new[] { "volume 100", "Volume → 100%" },
                new[] { "volume forty three", "Volume → 43%" },
                new[] { "volume seventy three", "Volume → 73%" },
                new[] { "Volume forty-three.", "Volume → 43%" },
                new[] { "volume seventy five", "Volume → 75%" },
                new[] { "volume fifty", "Volume → 50%" },
                new[] { "volume one hundred", "Volume → 100%" },
                new[] { "volume a hundred", "Volume → 100%" },
                new[] { "volume zero", "Volume → 0%" },
                new[] { "volume 0", "Volume → 0%" },
                new[] { "volume 37%", "Volume → 37%" },
                new[] { "set volume to fifty percent", "Volume → 50%" },
                new[] { "set volume twenty five", "Volume → 25%" },
                new[] { "volume 200", "-" },
                new[] { "volume two hundred", "-" },
                new[] { "volume one hundred and one", "-" },
                new[] { "volume fifty something", "-" },
                new[] { "volume up", "Volume up" },
                new[] { "volume down", "Volume down" },
                new[] { "mute", "Mute" },
                new[] { "unmute", "Unmute" },
                new[] { "play", "Play / pause" },
                new[] { "pause", "Play / pause" },
                new[] { "next window", "Next window" },
                new[] { "previous window", "Previous window" },
                new[] { "control on", "Kinect control on" },
                new[] { "control off", "Kinect control off" },
                new[] { "compact mode", "Compact mode" },
                new[] { "cancel", "Cancelled" },
                new[] { "never mind", "Cancelled" },
                new[] { "I was playing something earlier and then paused it", "-" },
                new[] { "I'm playing the video but I paused it because the volume was too high.", "-" },
                new[] { "purple bananas", "-" },
                new[] { "wake up", "-" },
                new[] { "go to sleep", "-" },
                new[] { "louder", "-" },
                new[] { "mute please", "-" },
                new[] { "kinect volume fifty", "-" },
                new[] { "kinect", "-" },
                new[] { "jarvis volume fifty", "-" },
                new[] { "jarvis", "-" },
                new[] { "claude listen", "-" },
                new[] { "", "-" },
            };

            int failures = 0;
            foreach (string[] c in cases)
            {
                VoiceIntent intent;
                string reason;
                string actual = VoiceCommandParser.TryParse(c[0], out intent, out reason) ? intent.Feedback : "-";
                if (actual != c[1])
                {
                    failures++;
                    text.AppendLine("  FAIL parse '" + c[0] + "' -> " + actual + ", expected " + c[1] + (reason != null ? " (" + reason + ")" : ""));
                }
            }

            text.AppendLine((failures == 0 ? "PASS" : "FAIL") + "  parser: " + cases.Length + " phrases, including conversation that must be refused");
            return failures;
        }

        /// <summary>
        /// Custom phrases through the parser (built-ins still win, exact matching only), the
        /// phrase rules, the wake-word rules and acronym spelling.
        /// </summary>
        private static int TestCustomCommands(StringBuilder text)
        {
            int failures = 0;
            CustomPhraseSet custom = new CustomPhraseSet(new[]
            {
                new KeyValuePair<string, string>("gpt", "GPT listen"),
                new KeyValuePair<string, string>("claude", "Claude listen"),
                new KeyValuePair<string, string>("cursor", "Cursor listen"),
                new KeyValuePair<string, string>("work", "work mode")
            });

            string[][] parses =
            {
                new[] { "GPT listen", "GPT listen" },
                new[] { "G P T listen", "GPT listen" },
                new[] { "gpt listen", "GPT listen" },
                new[] { "Claude, listen.", "Claude listen" },
                new[] { "cursor listen", "Cursor listen" },
                new[] { "work mode", "work mode" },
                new[] { "mute", "Mute" },
                new[] { "volume fifty", "Volume → 50%" },
                new[] { "claude listen please", "-" },
                new[] { "please claude listen", "-" },
                new[] { "listen", "-" },
                new[] { "claude", "-" },
                new[] { "G P listen", "-" },
            };

            foreach (string[] c in parses)
            {
                VoiceIntent intent;
                string reason;
                string actual = VoiceCommandParser.TryParse(c[0], custom, out intent, out reason) ? intent.Feedback : "-";
                if (actual != c[1])
                {
                    failures++;
                    text.AppendLine("  FAIL custom parse '" + c[0] + "' -> " + actual + ", expected " + c[1]);
                }
            }

            // Phrase rules: null = allowed.
            string[][] phrases =
            {
                new[] { "Claude listen", "ok" },
                new[] { "GPT listen", "ok" },
                new[] { "open my resume", "ok" },
                new[] { "mute", "refused" },
                new[] { "never mind", "refused" },
                new[] { "volume boost", "refused" },
                new[] { "Jarvis listen", "refused" },
                new[] { "open tab 2", "refused" },
                new[] { "", "refused" },
                new[] { "one two three four five six seven", "refused" },
            };

            foreach (string[] c in phrases)
            {
                string actual = CustomCommandRules.CheckPhrase(c[0], Wake) == null ? "ok" : "refused";
                if (actual != c[1])
                {
                    failures++;
                    text.AppendLine("  FAIL custom phrase '" + c[0] + "' " + actual + ", expected " + c[1]);
                }
            }

            string[][] wakes =
            {
                new[] { "Jarvis", "ok" },
                new[] { "  hey   Jarvis ", "ok" },
                new[] { "Kinect", "ok" },
                new[] { "Computer", "ok" },
                new[] { "", "refused" },
                new[] { "mute", "refused" },
                new[] { "cancel", "refused" },
                new[] { "a b c d", "refused" },
                new[] { "R2D2", "refused" },
                new[] { "ok", "refused" },
            };

            foreach (string[] c in wakes)
            {
                string cleaned;
                string error;
                string actual = VoiceWakeWord.TryValidate(c[0], out cleaned, out error) ? "ok" : "refused";
                if (actual != c[1])
                {
                    failures++;
                    text.AppendLine("  FAIL wake word '" + c[0] + "' " + actual + ", expected " + c[1] + (error != null ? " (" + error + ")" : ""));
                }
            }

            string[][] spoken =
            {
                new[] { "GPT listen", "G P T listen" },
                new[] { "gpt listen", "G P T listen" },
                new[] { "Claude listen", "claude listen" },
                new[] { "AI mode", "A I mode" },
                new[] { "hey Jarvis", "hey jarvis" },
            };

            foreach (string[] c in spoken)
            {
                string actual = VoicePhrases.GrammarText(c[0]);
                if (actual != c[1] || VoicePhrases.MatchKey(actual) != VoicePhrases.MatchKey(c[0]))
                {
                    failures++;
                    text.AppendLine("  FAIL grammar text '" + c[0] + "' -> '" + actual + "', expected '" + c[1] + "' with the same match key");
                }
            }

            int total = parses.Length + phrases.Length + wakes.Length + spoken.Length;
            text.AppendLine((failures == 0 ? "PASS" : "FAIL") + "  custom commands: " + total + " checks (parser, phrase rules, wake-word rules, acronym spelling)");
            return failures;
        }

        private static int TestKeyChords(StringBuilder text)
        {
            int failures = 0;
            string[][] cases =
            {
                // typed, canonical ("-" = refused), virtual keys
                new[] { "Ctrl+Shift+D", "Ctrl+Shift+D", "11 10 44" },
                new[] { "left control + left shift + d", "Ctrl+Shift+D", "11 10 44" },
                new[] { "ctrl+shift+d", "Ctrl+Shift+D", "11 10 44" },
                new[] { "Shift+Ctrl+D", "Ctrl+Shift+D", "11 10 44" },
                new[] { "Win+H", "Win+H", "5B 48" },
                new[] { "F5", "F5", "74" },
                new[] { "Alt+Space", "Alt+Space", "12 20" },
                new[] { "Ctrl+5", "Ctrl+5", "11 35" },
                new[] { "Ctrl+Alt+PgDn", "Ctrl+Alt+PageDown", "11 12 22" },
                new[] { "Shift", "-", "" },
                new[] { "Ctrl+", "-", "" },
                new[] { "Ctrl+D+E", "-", "" },
                new[] { "Ctrl+Banana", "-", "" },
                new[] { "", "-", "" },
            };

            foreach (string[] c in cases)
            {
                KeyChord chord;
                string error;
                bool ok = KeyChord.TryParse(c[0], out chord, out error);
                string canonical = ok ? chord.Text : "-";
                string keys = "";
                if (ok)
                {
                    List<string> hex = new List<string>();
                    foreach (ushort vk in chord.VirtualKeys())
                    {
                        hex.Add(vk.ToString("X2", CultureInfo.InvariantCulture));
                    }

                    keys = string.Join(" ", hex.ToArray());
                }

                if (canonical != c[1] || keys != c[2])
                {
                    failures++;
                    text.AppendLine("  FAIL keys '" + c[0] + "' -> " + canonical + " [" + keys + "], expected " + c[1] + " [" + c[2] + "]");
                }
            }

            text.AppendLine((failures == 0 ? "PASS" : "FAIL") + "  key combinations: " + cases.Length + " typed forms parsed (nothing is pressed)");
            return failures;
        }

        private static int TestGrammars(StringBuilder text)
        {
            try
            {
                CultureInfo culture = new CultureInfo("en-US");
                using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine(culture))
                {
                    engine.LoadGrammar(VoiceGrammars.BuildWake(culture, Wake));
                    engine.LoadGrammar(VoiceGrammars.BuildWake(culture, "Kinect"));
                    engine.LoadGrammar(VoiceGrammars.BuildWake(culture, "hey Jarvis"));
                    engine.LoadGrammar(VoiceGrammars.BuildCommands(culture, StarterPhrases()));
                    text.AppendLine("PASS  grammars: wake words “" + Wake + "”, “Kinect” (explicit pronunciation) and “hey Jarvis”, and the command grammar with custom phrases load; engine CFG rejection threshold "
                        + engine.QueryRecognizerSetting("CFGConfidenceRejectionThreshold") + ", high-confidence band "
                        + engine.QueryRecognizerSetting("HighConfidenceThreshold"));
                }

                return 0;
            }
            catch (Exception ex)
            {
                text.AppendLine("FAIL  grammars: " + ex.Message);
                return 1;
            }
        }

        private static int TestSystemVolume(StringBuilder text)
        {
            int percent;
            bool muted;
            string error;
            if (!SystemVolume.TryGet(out percent, out muted, out error))
            {
                text.AppendLine("FAIL  system volume read: " + error);
                return 1;
            }

            string detail;
            bool rewritten = SystemVolume.TryRewriteCurrentLevel(out detail);

            // Setting mute to the state it is already in returns S_FALSE, which must count as
            // success (it is exactly what "volume N" does when the speakers are not muted).
            string muteError;
            bool muteSame = SystemVolume.TrySetMute(muted, out muteError);
            bool ok = rewritten && muteSame;
            text.AppendLine((ok ? "PASS" : "FAIL") + "  system volume: default endpoint at " + percent + "%"
                + (muted ? " (muted)" : "") + "; " + detail + "; mute re-set to its current state "
                + (muteSame ? "accepted" : "FAILED: " + muteError) + " (no audible change)");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Lists the capture devices (read-only) and checks that a device that is not connected
        /// is refused cleanly. No microphone is opened.
        /// </summary>
        private static int TestMicrophones(StringBuilder text)
        {
            string error;
            List<AudioInputDevice> devices = AudioInputDevices.List(out error);
            if (error != null)
            {
                text.AppendLine("FAIL  microphones: device list unavailable: " + error);
                return 1;
            }

            string defaultName = "none";
            foreach (AudioInputDevice device in devices)
            {
                if (device.IsDefault)
                {
                    defaultName = device.Name;
                }
            }

            WakeGatedVoiceEngine engine = new WakeGatedVoiceEngine();
            engine.InputDeviceId = "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}";
            engine.InputDeviceName = "Missing test microphone";
            string startError;
            bool started = engine.Start(out startError);
            bool refused = !started && startError != null && startError.Contains("not connected")
                && engine.ActiveInputId == null && engine.Phase == VoicePhase.Off;
            engine.Dispose();

            text.AppendLine((refused ? "PASS" : "FAIL") + "  microphones: " + devices.Count + " input device(s) listed, Windows default "
                + defaultName + "; a missing device is refused (" + (startError ?? "it started") + ")");
            return refused ? 0 : 1;
        }

        private static int TestChime(StringBuilder text)
        {
            short[] chime = VoiceFeedbackSounds.SynthesizeWakeChime(VoiceFeedbackSounds.PlaybackSampleRate);
            int peak = 0;
            for (int i = 0; i < chime.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs((int)chime[i]));
            }

            double seconds = chime.Length / (double)VoiceFeedbackSounds.PlaybackSampleRate;
            double peakDb = 20 * Math.Log10(Math.Max(1, peak) / 32767.0);
            bool ok = seconds <= 0.2 && peakDb < -6 && peakDb > -20 && chime[chime.Length - 1] == 0;
            text.AppendLine((ok ? "PASS" : "FAIL") + "  chime: " + (seconds * 1000).ToString("0") + " ms, peak "
                + peakDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS, ends at silence");
            return ok ? 0 : 1;
        }

        // ---- Recognition scenarios -----------------------------------------------------------------

        private sealed class Scenario
        {
            public string Name;
            public readonly List<Tuple<string, string, double>> Parts = new List<Tuple<string, string, double>>();
            public string[] ExpectedCommands = new string[0];
            public Func<ScenarioResult, string> ExtraCheck;
            public double Tail = 1.0;
            public string WakeWord = Wake;
            public CustomPhraseSet Custom = StarterPhrases();

            public Scenario Say(string words, string voice = David)
            {
                Parts.Add(Tuple.Create(words, voice, 0.0));
                return this;
            }

            public Scenario Wait(double seconds)
            {
                Parts.Add(Tuple.Create((string)null, (string)null, seconds));
                return this;
            }
        }

        private sealed class ScenarioResult
        {
            public readonly List<string> Commands = new List<string>();
            public readonly List<VoiceOutcome> Outcomes = new List<VoiceOutcome>();
            public readonly List<string> Decisions = new List<string>();
            public readonly List<double> GateLatencies = new List<double>();
            public readonly List<double> ChimeLatencies = new List<double>();
            public int Chimes;
            public VoiceCounters Counters;
            public string Error;
            public double AudioSeconds;
        }

        private static int TestRecognition(StringBuilder text)
        {
            if (!HasVoice(David) || !HasVoice(Zira))
            {
                text.AppendLine("SKIP  recognition scenarios: the Microsoft David/Zira desktop voices are not installed");
                return 0;
            }

            text.AppendLine();
            text.AppendLine("Recognition scenarios (synthesized speech at real-time pace, chime mixed back in as echo,");
            text.AppendLine("test thresholds wake " + TestWakeThreshold.ToString("0.00", CultureInfo.InvariantCulture)
                + " / command " + TestCommandThreshold.ToString("0.00", CultureInfo.InvariantCulture) + " - lower than the defaults):");

            List<Scenario> scenarios = new List<Scenario>();

            scenarios.Add(new Scenario { Name = "Background conversation, several minutes' worth of action words" }
                .Wait(0.8).Say("I was playing something earlier and then paused it.", Zira)
                .Wait(0.7).Say("Let's go to sleep, I'm tired.")
                .Wait(0.6).Say("Can you turn the volume down a bit? It's too loud.", Zira)
                .Wait(0.7).Say("The compact mode on that camera is really good.")
                .Wait(0.6).Say("Mute the TV when the ads come on, and then play the next episode.", Zira)
                .Wait(0.7).Say("I think the control is off, can you connect it again?")
                .Wait(0.6).Say("Next week we should get a new window for the kitchen.", Zira)
                .Wait(0.7).Say("Set the volume to fifty and then click play.")
                .Wait(0.6).Say("Did you cancel the order or should I?", Zira));

            scenarios.Add(new Scenario { Name = "Proper wake cycle: " + Wake + ", chime, volume forty three", ExpectedCommands = new[] { "Volume → 43%" } }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("volume forty three"));

            scenarios.Add(new Scenario { Name = "Custom command: Claude listen", ExpectedCommands = new[] { "Claude listen" } }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("Claude listen"));

            scenarios.Add(new Scenario { Name = "Custom command with a spelled acronym: GPT listen", ExpectedCommands = new[] { "GPT listen" } }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("GPT listen"));

            scenarios.Add(new Scenario { Name = "Custom command without the wake word: Cursor listen" }
                .Wait(0.8).Say("Cursor listen").Wait(0.8).Say("Claude listen", Zira));

            scenarios.Add(new Scenario
            {
                Name = "Another wake word still works: Kinect, chime, mute",
                WakeWord = "Kinect",
                ExpectedCommands = new[] { "Mute" }
            }.Wait(0.8).Say("Kinect").Wait(1.0).Say("mute"));

            scenarios.Add(new Scenario { Name = "The old wake word does nothing once changed: Kinect, mute" }
                .Wait(0.8).Say("Kinect").Wait(1.0).Say("mute"));

            scenarios.Add(new Scenario { Name = "Command without wake: volume one hundred" }
                .Wait(0.8).Say("volume one hundred").Wait(0.8).Say("mute"));

            scenarios.Add(new Scenario { Name = "Second command without another wake", ExpectedCommands = new[] { "Volume → 40%" } }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("volume forty").Wait(1.2).Say("mute"));

            scenarios.Add(new Scenario
            {
                Name = "Wake then silence: timeout",
                ExtraCheck = r => r.Outcomes.Contains(VoiceOutcome.TimedOut) ? null : "expected a timeout",
                Tail = 5.5
            }.Wait(0.8).Say(Wake));

            scenarios.Add(new Scenario { Name = "Unknown command: purple bananas" }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("purple bananas"));

            scenarios.Add(new Scenario { Name = "Conversation containing action words" }
                .Wait(0.8).Say("I'm playing the video but I paused it because the volume was too high."));

            scenarios.Add(new Scenario
            {
                Name = "Own-chime rejection: three wakes, silence after each",
                ExtraCheck = r =>
                {
                    if (r.Chimes != (r.Counters != null ? r.Counters.Wakes : -1))
                    {
                        return "chimes and wakes differ";
                    }

                    if (r.Outcomes.Contains(VoiceOutcome.NotRecognized) || r.Outcomes.Contains(VoiceOutcome.SpokeTooEarly))
                    {
                        return "the chime was treated as speech";
                    }

                    return r.Counters != null && r.Counters.Wakes >= 2 ? null : "fewer than two wakes recognized";
                },
                Tail = 5.0
            }.Wait(0.8).Say(Wake).Wait(5.2).Say(Wake).Wait(5.2).Say(Wake));

            scenarios.Add(new Scenario { Name = "Out of range: volume two hundred" }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("volume two hundred"));

            scenarios.Add(new Scenario { Name = "Wake word inside a sentence" }
                .Wait(0.8).Say("I think Jarvis is the computer in Iron Man.", Zira).Wait(0.6).Say("Yes, and Jarvis runs the whole house, play it."));

            scenarios.Add(new Scenario
            {
                Name = "Cancel",
                ExtraCheck = r => r.Outcomes.Contains(VoiceOutcome.Cancelled) || r.Outcomes.Contains(VoiceOutcome.NotRecognized) ? null : "session did not close"
            }.Wait(0.8).Say(Wake).Wait(1.0).Say("cancel"));

            scenarios.Add(new Scenario
            {
                Name = "Combined utterance without waiting: " + Wake + " volume fifty",
                ExtraCheck = r => r.Outcomes.Contains(VoiceOutcome.Executed) ? "executed" : null,
                Tail = 5.0
            }.Wait(0.8).Say(Wake + " volume fifty"));

            scenarios.Add(new Scenario
            {
                Name = "Wake word, then talking straight on without waiting",
                ExtraCheck = r => r.Counters == null || r.Counters.Wakes == 0 || r.Outcomes.Count > 0 ? null : "session left open",
                Tail = 5.0
            }.Wait(0.8).Say(Wake).Wait(0.15).Say("is it plugged in? Mute the TV and play the next one.", Zira));

            scenarios.Add(new Scenario { Name = "Two deliberate cycles", ExpectedCommands = new[] { "Mute", "Unmute" } }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("mute").Wait(1.5).Say(Wake).Wait(1.0).Say("unmute"));

            scenarios.Add(new Scenario { Name = "Prompt speaker: command 0.8 s after the wake word", ExpectedCommands = new[] { "Next window" } }
                .Wait(0.8).Say(Wake).Wait(0.8).Say("next window"));

            int failures = 0;
            List<double> latencies = new List<double>();
            List<double> chimeLatencies = new List<double>();
            foreach (Scenario scenario in scenarios)
            {
                ScenarioResult result = RunScenario(scenario, TestWakeThreshold, TestCommandThreshold);
                latencies.AddRange(result.GateLatencies);
                chimeLatencies.AddRange(result.ChimeLatencies);

                List<string> problems = new List<string>();
                if (result.Error != null)
                {
                    problems.Add(result.Error);
                }

                if (!SameList(result.Commands, scenario.ExpectedCommands))
                {
                    problems.Add("commands [" + string.Join(", ", result.Commands.ToArray()) + "], expected ["
                        + string.Join(", ", scenario.ExpectedCommands) + "]");
                }

                if (scenario.ExtraCheck != null && result.Error == null)
                {
                    string extra = scenario.ExtraCheck(result);
                    if (extra != null)
                    {
                        problems.Add(extra);
                    }
                }

                bool ok = problems.Count == 0;
                if (!ok)
                {
                    failures++;
                }

                VoiceCounters c = result.Counters ?? new VoiceCounters();
                text.AppendLine((ok ? "PASS" : "FAIL") + "  " + scenario.Name);
                text.AppendLine("        " + result.AudioSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s audio; commands "
                    + result.Commands.Count + "; wakes " + c.Wakes + "; chimes " + result.Chimes + "; outcomes ["
                    + string.Join(", ", result.Outcomes.ConvertAll(o => o.ToString()).ToArray()) + "]; background ignored "
                    + c.BackgroundUtterances + "; wake near-misses " + c.WakeCandidatesRejected + "; echo/noise ignored " + c.EchoOrNoiseIgnored);
                foreach (string problem in problems)
                {
                    text.AppendLine("        PROBLEM: " + problem);
                }

                foreach (string decision in result.Decisions)
                {
                    text.AppendLine("          " + decision);
                }
            }

            if (latencies.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Timing, in audio time from the end of the spoken wake word:");
                text.AppendLine("  chime starts          " + Describe(chimeLatencies));
                text.AppendLine("  command window opens  " + Describe(latencies) + "  (chime 150 ms + simulated 40 ms output latency + 80 ms guard)");
            }

            failures += TestDefaultThresholds(text);
            return failures;
        }

        private static string Describe(List<double> values)
        {
            List<double> sorted = new List<double>(values);
            sorted.Sort();
            return "median " + (sorted[sorted.Count / 2] * 1000).ToString("0") + " ms, range "
                + (sorted[0] * 1000).ToString("0") + "-" + (sorted[sorted.Count - 1] * 1000).ToString("0") + " ms";
        }

        /// <summary>
        /// Harder corpora at the shipped thresholds. Pure conversation - full of action words,
        /// sound-alikes and the wake word inside sentences - must produce no wake at all. Real
        /// wakes followed by people simply carrying on talking must produce no action.
        /// </summary>
        private static int TestDefaultThresholds(StringBuilder text)
        {
            text.AppendLine();
            text.AppendLine("At the shipped thresholds (wake " + WakeGatedVoiceEngine.DefaultWakeThreshold.ToString("0.00", CultureInfo.InvariantCulture)
                + " / command " + WakeGatedVoiceEngine.DefaultCommandThreshold.ToString("0.00", CultureInfo.InvariantCulture) + "):");

            Scenario conversation = new Scenario { Name = "Pure conversation: action words, sound-alikes, the wake word inside sentences", Tail = 1.5 }
                .Wait(0.8).Say("I was playing something earlier and then paused it.", Zira)
                .Wait(0.6).Say("Let's go to sleep, I'm tired.")
                .Wait(0.6).Say("Can you connect the speaker to the other computer?", Zira)
                .Wait(0.6).Say("Mute the TV when the ads come on, and then play the next episode.", Zira)
                .Wait(0.6).Say("Kenneth said he would come by later, and then we can eat.")
                .Wait(0.6).Say("That's correct, I think the connection is fine.", Zira)
                .Wait(0.6).Say("Set the volume to fifty and then click play.")
                .Wait(0.6).Say("The control is off, the Kinect needs to connect again.", Zira)
                .Wait(0.6).Say("Can I get a coffee, and can next week work for you?")
                .Wait(0.6).Say("Kinetic energy is what we covered in class.", Zira)
                .Wait(0.6).Say("Okay, and then what happened after that?", Zira)
                .Wait(0.6).Say("I connected it and it just kept disconnecting.")
                .Wait(0.6).Say("Is it connected? Can you check it?", Zira)
                .Wait(0.6).Say("Then we can talk about it, if that's okay.")
                .Wait(0.6).Say("Honestly I think we should just cancel it.")
                .Wait(0.6).Say("Please don't mute the TV, I'm watching.")
                .Wait(0.6).Say("It's in compact mode already.", Zira)
                .Wait(0.6).Say("I think the Kinect is on the shelf next to the TV.")
                .Wait(0.6).Say("Contact me tomorrow, I can explain.")
                .Wait(0.6).Say("Connect four is a fun game.", Zira)
                .Wait(0.6).Say("Volume up a bit, it's quiet.", Zira)
                .Wait(0.6).Say("I watched Iron Man again, and Jarvis is still the best part.")
                .Wait(0.6).Say("Travis said he would be here by eight.", Zira)
                .Wait(0.6).Say("Put the jars back in the service cupboard.")
                .Wait(0.6).Say("Can you tell Claude to listen to this one?", Zira)
                .Wait(0.6).Say("Harvest season is when the farm gets busy.")
                .Wait(0.6).Say("My cursor keeps jumping when I type.", Zira);

            ScenarioResult quiet = RunScenario(conversation, WakeGatedVoiceEngine.DefaultWakeThreshold, WakeGatedVoiceEngine.DefaultCommandThreshold);
            VoiceCounters c = quiet.Counters ?? new VoiceCounters();
            bool quietOk = quiet.Error == null && quiet.Commands.Count == 0 && c.Wakes == 0;
            text.AppendLine((quietOk ? "PASS" : "FAIL") + "  " + conversation.Name);
            text.AppendLine("        " + quiet.AudioSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s audio; commands "
                + quiet.Commands.Count + "; wakes " + c.Wakes + "; background ignored " + c.BackgroundUtterances
                + "; wake near-misses " + c.WakeCandidatesRejected);
            AppendDetail(text, quiet);

            Scenario carryOn = new Scenario { Name = "Real wakes, then people just carry on talking", Tail = 4.5 }
                .Wait(0.8).Say(Wake).Wait(1.0).Say("and then it connected, and then it dropped again.", Zira)
                .Wait(2.5).Say(Wake).Wait(0.9).Say("I was playing something earlier and then paused it.")
                .Wait(2.5).Say(Wake).Wait(1.1).Say("Mute the TV when the ads come on.", Zira)
                .Wait(2.5).Say(Wake).Wait(1.0).Say("Next, can I ask you something?")
                .Wait(2.5).Say(Wake).Wait(1.2).Say("Please, play it again.", Zira)
                .Wait(2.5).Say(Wake).Wait(1.0).Say("Claude is listening to music in the other room.");

            ScenarioResult talk = RunScenario(carryOn, WakeGatedVoiceEngine.DefaultWakeThreshold, WakeGatedVoiceEngine.DefaultCommandThreshold);
            VoiceCounters t = talk.Counters ?? new VoiceCounters();
            bool talkOk = talk.Error == null && talk.Commands.Count == 0;
            text.AppendLine((talkOk ? "PASS" : "FAIL") + "  " + carryOn.Name);
            text.AppendLine("        " + talk.AudioSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s audio; commands "
                + talk.Commands.Count + "; wakes " + t.Wakes + " of 6; outcomes ["
                + string.Join(", ", talk.Outcomes.ConvertAll(o => o.ToString()).ToArray()) + "]");
            AppendDetail(text, talk);

            return (quietOk ? 0 : 1) + (talkOk ? 0 : 1);
        }

        private static void AppendDetail(StringBuilder text, ScenarioResult result)
        {
            if (result.Error != null)
            {
                text.AppendLine("        PROBLEM: " + result.Error);
            }

            if (result.Commands.Count > 0)
            {
                text.AppendLine("        PROBLEM: commands [" + string.Join(", ", result.Commands.ToArray()) + "]");
            }

            foreach (string decision in result.Decisions)
            {
                text.AppendLine("          " + decision);
            }
        }

        private static bool SameList(List<string> actual, string[] expected)
        {
            if (actual.Count != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static ScenarioResult RunScenario(Scenario scenario, double wakeThreshold, double commandThreshold)
        {
            ScenarioResult result = new ScenarioResult();
            short[] pcm;
            try
            {
                pcm = Render(scenario);
            }
            catch (Exception ex)
            {
                result.Error = "speech synthesis failed: " + ex.Message;
                return result;
            }

            result.AudioSeconds = pcm.Length / (double)SimulatedMicrophone.SampleRate;
            SimulatedMicrophone microphone = new SimulatedMicrophone(pcm);
            LoopbackFeedback feedback = new LoopbackFeedback(microphone);
            WakeGatedVoiceEngine engine = new WakeGatedVoiceEngine();
            engine.Feedback = feedback;
            engine.InputConfigurator = recognizer => recognizer.SetInputToAudioStream(microphone, Format);
            engine.WakeWord = scenario.WakeWord;
            engine.CustomPhrases = scenario.Custom;
            engine.WakeThreshold = wakeThreshold;
            engine.CommandThreshold = commandThreshold;
            engine.DismissSoundEnabled = false;

            object gate = new object();
            ManualResetEvent finished = new ManualResetEvent(false);

            engine.CommandRecognized += (s, e) =>
            {
                // Recorded, never executed.
                if (e.Session.TryMarkExecuted())
                {
                    lock (gate)
                    {
                        result.Commands.Add(e.Intent.Feedback);
                    }
                }
            };
            engine.PhaseChanged += (s, e) =>
            {
                lock (gate)
                {
                    if (e.Outcome != VoiceOutcome.None)
                    {
                        result.Outcomes.Add(e.Outcome);
                    }

                    if (e.Phase == VoicePhase.Listening && e.Session != null && e.Detail != "extended")
                    {
                        result.GateLatencies.Add((e.Session.GatePosition - e.Session.WakeEnd).TotalSeconds);
                        result.ChimeLatencies.Add((e.Session.ChimePosition - e.Session.WakeEnd).TotalSeconds);
                    }
                }
            };
            engine.DecisionMade += (s, d) =>
            {
                lock (gate)
                {
                    result.Decisions.Add(d.ToString());
                }
            };
            engine.Stopped += (s, e) =>
            {
                if (e != null)
                {
                    lock (gate)
                    {
                        result.Error = "recognizer stopped: " + e.Message;
                    }
                }

                finished.Set();
            };

            string error;
            if (!engine.Start(out error))
            {
                result.Error = "engine did not start: " + error;
                return result;
            }

            finished.WaitOne(TimeSpan.FromSeconds(result.AudioSeconds + 15));
            result.Counters = engine.Counters;
            result.Chimes = feedback.Chimes;
            engine.Dispose();
            return result;
        }

        // ---- Audio ------------------------------------------------------------------------------------

        private static bool HasVoice(string name)
        {
            try
            {
                using (SpeechSynthesizer synthesizer = new SpeechSynthesizer())
                {
                    foreach (InstalledVoice voice in synthesizer.GetInstalledVoices())
                    {
                        if (voice.Enabled && voice.VoiceInfo.Name == name)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static short[] Render(Scenario scenario)
        {
            List<short> samples = new List<short>();
            Random noise = new Random(scenario.Name.Length * 7919);

            foreach (Tuple<string, string, double> part in scenario.Parts)
            {
                if (part.Item1 == null)
                {
                    AddSilence(samples, part.Item3, noise);
                }
                else
                {
                    short[] speech = Synthesize(part.Item1, part.Item2);
                    for (int i = 0; i < speech.Length; i++)
                    {
                        samples.Add(Mix(speech[i], noise.Next(-40, 41)));
                    }
                }
            }

            AddSilence(samples, scenario.Tail, noise);
            return samples.ToArray();
        }

        private static void AddSilence(List<short> samples, double seconds, Random noise)
        {
            int count = (int)(seconds * SimulatedMicrophone.SampleRate);
            for (int i = 0; i < count; i++)
            {
                // A faint noise floor (about -58 dBFS): digital silence is not what a room sounds like.
                samples.Add((short)noise.Next(-40, 41));
            }
        }

        private static short[] Synthesize(string words, string voice)
        {
            using (SpeechSynthesizer synthesizer = new SpeechSynthesizer())
            using (MemoryStream stream = new MemoryStream())
            {
                synthesizer.SelectVoice(voice);
                synthesizer.SetOutputToAudioStream(stream, Format);

                // "Kinect" with the same pronunciation its wake grammar gives it.
                PromptBuilder prompt = new PromptBuilder();
                string[] pieces = words.Split(' ');
                StringBuilder run = new StringBuilder();
                foreach (string piece in pieces)
                {
                    string bare = piece.Trim(',', '.', '?', '!');
                    if (bare.Equals("Kinect", StringComparison.OrdinalIgnoreCase))
                    {
                        if (run.Length > 0)
                        {
                            prompt.AppendText(run.ToString());
                            run.Clear();
                        }

                        prompt.AppendTextWithPronunciation("Kinect", VoiceWakeWord.KinectPronunciationIpa);
                        string trailing = piece.Substring(piece.IndexOf(bare, StringComparison.Ordinal) + bare.Length);
                        if (trailing.Length > 0)
                        {
                            run.Append(trailing).Append(' ');
                        }
                    }
                    else
                    {
                        run.Append(piece).Append(' ');
                    }
                }

                if (run.Length > 0)
                {
                    prompt.AppendText(run.ToString());
                }

                synthesizer.Speak(prompt);
                byte[] bytes = stream.ToArray();
                short[] samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
                return samples;
            }
        }

        private static short Mix(int a, int b)
        {
            int sum = a + b;
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, sum));
        }

        /// <summary>
        /// A microphone that plays back prepared audio at real-time pace. SAPI treats a short
        /// read as the end of the stream, so each read blocks until its whole chunk is "due".
        /// Audio can be mixed in ahead of the read position while it runs, which is how the
        /// chime's echo gets into the recognizer.
        /// </summary>
        private sealed class SimulatedMicrophone : Stream
        {
            public const int SampleRate = 16000;
            private const double Lead = 0.02;

            private readonly object gate = new object();
            private readonly short[] samples;
            private long position;
            private Stopwatch clock;

            public SimulatedMicrophone(short[] samples)
            {
                this.samples = samples;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int wanted = count / 2;
                long start;
                lock (gate)
                {
                    start = position;
                    if (clock == null)
                    {
                        clock = Stopwatch.StartNew();
                    }
                }

                if (wanted <= 0 || start >= samples.Length)
                {
                    return 0;
                }

                long end = Math.Min(samples.Length, start + wanted);
                while (clock.Elapsed.TotalSeconds + Lead < end / (double)SampleRate)
                {
                    Thread.Sleep(2);
                }

                lock (gate)
                {
                    for (long i = start; i < end; i++)
                    {
                        short sample = samples[i];
                        int at = offset + (int)(i - start) * 2;
                        buffer[at] = (byte)(sample & 0xFF);
                        buffer[at + 1] = (byte)((sample >> 8) & 0xFF);
                    }

                    position = end;
                }

                return (int)(end - start) * 2;
            }

            /// <summary>
            /// Mixes <paramref name="pcm"/> in as if it reached the microphone
            /// <paramref name="delay"/> from now.
            /// </summary>
            public void Inject(short[] pcm, TimeSpan delay, double gain)
            {
                lock (gate)
                {
                    double now = clock != null ? clock.Elapsed.TotalSeconds : 0;
                    long at = Math.Max(position, (long)((now + delay.TotalSeconds) * SampleRate));
                    for (int i = 0; i < pcm.Length && at + i < samples.Length; i++)
                    {
                        samples[at + i] = Mix(samples[at + i], (int)(pcm[i] * gain));
                    }
                }
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return samples.Length * 2L; } }

            public override long Position
            {
                get { lock (gate) { return position * 2; } }
                set { }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return Position;
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Instead of the speakers, "plays" the chime into the simulated microphone after a
        /// typical output latency, and takes as long as a real playback would.
        /// </summary>
        private sealed class LoopbackFeedback : IVoiceFeedback
        {
            private static readonly TimeSpan OutputLatency = TimeSpan.FromMilliseconds(40);

            private readonly SimulatedMicrophone microphone;
            private readonly short[] chime;
            private readonly short[] dismiss;
            private int chimes;

            public LoopbackFeedback(SimulatedMicrophone microphone)
            {
                this.microphone = microphone;
                chime = VoiceFeedbackSounds.SynthesizeWakeChime(SimulatedMicrophone.SampleRate);
                dismiss = VoiceFeedbackSounds.SynthesizeDismiss(SimulatedMicrophone.SampleRate);
            }

            public int Chimes
            {
                get { return Volatile.Read(ref chimes); }
            }

            public TimeSpan WakeChimeDuration
            {
                get { return TimeSpan.FromSeconds(chime.Length / (double)SimulatedMicrophone.SampleRate); }
            }

            public void PlayWakeChime()
            {
                Interlocked.Increment(ref chimes);
                microphone.Inject(chime, OutputLatency, EchoGain);
                Thread.Sleep(WakeChimeDuration + OutputLatency);
            }

            public void PlayDismiss()
            {
                microphone.Inject(dismiss, OutputLatency, EchoGain);
            }
        }
    }
}
