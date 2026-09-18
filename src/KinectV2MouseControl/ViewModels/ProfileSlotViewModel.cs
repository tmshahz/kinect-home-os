using System.Text;

namespace KinectV2MouseControl
{
    /// <summary>
    /// One of the three profile slots as the Profiles page sees it. Wraps the ProfileSlot
    /// stored on disk; renaming writes straight through to the slot (and is persisted by the
    /// next save, exactly as before).
    /// </summary>
    public class ProfileSlotViewModel : ObservableObject
    {
        private readonly ProfileSlot slot;
        private readonly KinectCursorViewModel owner;

        public int Index { get; private set; }

        public ProfileSlotViewModel(int index, ProfileSlot slot, KinectCursorViewModel owner)
        {
            Index = index;
            this.slot = slot;
            this.owner = owner;
            SaveCommand = new RelayCommand(() => owner.RequestSaveProfile(Index));
            LoadCommand = new RelayCommand(() => owner.LoadProfile(Index), () => !IsEmpty);
        }

        public string SlotLabel
        {
            get
            {
                return "SLOT " + (Index + 1);
            }
        }

        public string Name
        {
            get
            {
                return slot.Name;
            }
            set
            {
                string name = string.IsNullOrWhiteSpace(value) ? "Profile " + (Index + 1) : value;
                if (slot.Name != name)
                {
                    slot.Name = name;
                    Raise("Name");
                    owner.OnProfileRenamed(Index);
                }
            }
        }

        public bool IsEmpty
        {
            get
            {
                return slot.IsEmpty;
            }
        }

        public string SavedAtText
        {
            get
            {
                return slot.IsEmpty ? "Empty slot" : "Saved " + slot.SavedAt;
            }
        }

        private bool isActive;
        public bool IsActive
        {
            get
            {
                return isActive;
            }
            set
            {
                if (Set(ref isActive, value))
                {
                    Raise("StateText");
                }
            }
        }

        private bool isModified;
        public bool IsModified
        {
            get
            {
                return isModified;
            }
            set
            {
                if (Set(ref isModified, value))
                {
                    Raise("StateText");
                }
            }
        }

        public string StateText
        {
            get
            {
                if (!isActive)
                {
                    return slot.IsEmpty ? "Available" : "Saved";
                }

                return isModified ? "Active · modified" : "Active";
            }
        }

        /// <summary>
        /// The values most people tune, so a slot can be told apart without loading it.
        /// </summary>
        public string SummaryText
        {
            get
            {
                TuningProfile p = slot.Settings;
                if (p == null)
                {
                    return "Save the current tuning here to keep a second setup - for a different chair, distance or person.";
                }

                StringBuilder text = new StringBuilder();
                Append(text, "Move", p.MoveScale, "0.00");
                Append(text, "Smooth", p.Smoothing, "0.00");
                Append(text, "Response", p.SpeedResponsiveness, "0");
                Append(text, "Dead zone", p.JitterDeadzone, "0.#", " px");
                Append(text, "Pointer h", p.PointerCenterHeight, "0.00", " m");
                Append(text, "Scroll", p.ScrollSpeed, "0");
                if (p.UseCalibratedRange.HasValue)
                {
                    if (text.Length > 0)
                    {
                        text.Append("   ·   ");
                    }

                    text.Append(p.UseCalibratedRange.Value ? "Calibrated range" : "Uniform mapping");
                }

                return text.ToString();
            }
        }

        private static void Append(StringBuilder text, string label, double? value, string format, string unit = "")
        {
            if (!value.HasValue)
            {
                return;
            }

            if (text.Length > 0)
            {
                text.Append("   ·   ");
            }

            text.Append(label).Append(' ').Append(value.Value.ToString(format)).Append(unit);
        }

        public RelayCommand SaveCommand { get; private set; }
        public RelayCommand LoadCommand { get; private set; }

        /// <summary>
        /// Re-reads everything from the slot after a save or load.
        /// </summary>
        public void Refresh()
        {
            Raise("Name");
            Raise("IsEmpty");
            Raise("SavedAtText");
            Raise("SummaryText");
            Raise("StateText");
        }
    }
}
