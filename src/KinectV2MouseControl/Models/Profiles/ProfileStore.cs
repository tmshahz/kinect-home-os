using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Every user-facing value that shapes how control behaves: pointer tuning, activation,
    /// gestures, stationary lock and calibration. Runtime state (filter history, sessions,
    /// clutch) is never stored, and neither is the control mode - loading a profile changes
    /// how control feels, not whether it is on.
    ///
    /// Every member is nullable and optional, so a profile saved by an older build simply
    /// leaves newer settings unchanged when it is loaded, instead of zeroing them.
    /// </summary>
    [DataContract]
    public class TuningProfile
    {
        [DataMember(EmitDefaultValue = false)] public double? MoveScale;
        [DataMember(EmitDefaultValue = false)] public double? Smoothing;
        [DataMember(EmitDefaultValue = false)] public double? SpeedResponsiveness;
        [DataMember(EmitDefaultValue = false)] public double? JitterDeadzone;
        [DataMember(EmitDefaultValue = false)] public double? ClickFreezeDuration;
        [DataMember(EmitDefaultValue = false)] public double? PointerSettleTime;
        [DataMember(EmitDefaultValue = false)] public double? PointerCenterHeight;
        [DataMember(EmitDefaultValue = false)] public double? ActivationMinHeight;
        [DataMember(EmitDefaultValue = false)] public double? ForwardActivationDistance;
        [DataMember(EmitDefaultValue = false)] public double? ScrollSpeed;
        [DataMember(EmitDefaultValue = false)] public double? ScrollCurve;
        [DataMember(EmitDefaultValue = false)] public bool? InvertScroll;
        [DataMember(EmitDefaultValue = false)] public double? SwipeMinDisplacement;
        [DataMember(EmitDefaultValue = false)] public double? HoverRange;
        [DataMember(EmitDefaultValue = false)] public double? HoverDuration;
        [DataMember(EmitDefaultValue = false)] public bool? StationaryLockEnabled;
        [DataMember(EmitDefaultValue = false)] public double? StationaryLockRadius;
        [DataMember(EmitDefaultValue = false)] public double? StationaryLockDwell;
        [DataMember(EmitDefaultValue = false)] public double? StationaryBreakoutRadius;
        [DataMember(EmitDefaultValue = false)] public bool? UseCalibratedRange;
        [DataMember(EmitDefaultValue = false)] public double? HandRangeX;
        [DataMember(EmitDefaultValue = false)] public double? HandRangeY;
        [DataMember(EmitDefaultValue = false)] public double? HandCenterX;
        [DataMember(EmitDefaultValue = false)] public double? HandComfortCenterX;
        [DataMember(EmitDefaultValue = false)] public double? CalibrationSpreadX;
        [DataMember(EmitDefaultValue = false)] public double? CalibrationSpreadY;
    }

    [DataContract]
    public class ProfileSlot
    {
        [DataMember] public string Name;

        /// <summary>
        /// Local time the slot was last saved, as text, or null for an empty slot.
        /// </summary>
        [DataMember(EmitDefaultValue = false)] public string SavedAt;

        [DataMember(EmitDefaultValue = false)] public TuningProfile Settings;

        public bool IsEmpty
        {
            get
            {
                return Settings == null;
            }
        }
    }

    [DataContract]
    public class ProfileFile
    {
        public const int SlotCount = 3;

        [DataMember] public int Version = 1;
        [DataMember] public ProfileSlot[] Slots;

        /// <summary>
        /// Guarantees exactly SlotCount non-null slots with names, whatever was on disk.
        /// </summary>
        public void Normalize()
        {
            ProfileSlot[] normalized = new ProfileSlot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                ProfileSlot slot = (Slots != null && i < Slots.Length) ? Slots[i] : null;
                if (slot == null)
                {
                    slot = new ProfileSlot();
                }

                if (string.IsNullOrWhiteSpace(slot.Name))
                {
                    slot.Name = "Profile " + (i + 1);
                }

                normalized[i] = slot;
            }

            Slots = normalized;
        }
    }

    /// <summary>
    /// Reads and writes the three tuning profile slots.
    ///
    /// Stored as JSON at %LOCALAPPDATA%\KinectHomeOS\profiles.json. Unlike user.config, which
    /// .NET keys to the exe's path, this location is shared, so a profile saved from the Debug
    /// build can be loaded in the Release build and survives rebuilds and renames. Writes go to
    /// a temporary file first and are then swapped in, so a crash mid-save cannot corrupt the
    /// existing slots. A file that cannot be parsed is set aside as profiles.json.bad rather
    /// than overwritten, and the store starts empty.
    /// </summary>
    public class ProfileStore
    {
        private static readonly DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ProfileFile));

        public static string FilePath
        {
            get
            {
                return Path.Combine(RuntimeLog.DirectoryPath, "profiles.json");
            }
        }

        /// <summary>
        /// Loads the slots. Never throws; problems are reported through <paramref name="warning"/>.
        /// </summary>
        public ProfileFile Load(out string warning)
        {
            warning = null;
            ProfileFile file = null;
            string path = FilePath;

            try
            {
                if (File.Exists(path))
                {
                    using (FileStream stream = File.OpenRead(path))
                    {
                        file = (ProfileFile)serializer.ReadObject(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                warning = "Profiles file unreadable, set aside as profiles.json.bad (" + ex.GetType().Name + ")";
                RuntimeLog.Write(warning + ": " + ex.Message);
                TrySetAside(path);
                file = null;
            }

            if (file == null)
            {
                file = new ProfileFile();
            }

            file.Normalize();
            return file;
        }

        /// <summary>
        /// Saves the slots. Returns false, with a reason, if the file could not be written.
        /// </summary>
        public bool Save(ProfileFile file, out string error)
        {
            error = null;
            string path = FilePath;
            string temporary = path + ".tmp";

            try
            {
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
                RuntimeLog.Write("Profile save failed: " + ex.Message);
                return false;
            }
        }

        private static void TrySetAside(string path)
        {
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
                // Leave it; the next save will overwrite it.
            }
        }
    }
}
