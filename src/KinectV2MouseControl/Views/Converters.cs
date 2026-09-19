using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Picks one of two values from a bool. Declared as resources with the pair set in XAML
    /// (brushes, opacities, strings, thicknesses), so one class covers every "if on, look like
    /// this" binding in the control center.
    /// </summary>
    public class BoolToValueConverter : IValueConverter
    {
        public object TrueValue { get; set; }
        public object FalseValue { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool && (bool)value;
            return flag ? TrueValue : FalseValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }
    }

    /// <summary>
    /// Visible when the value is "truthy": a true bool, a non-empty string, a non-null object,
    /// a non-zero number. Parameter "invert" flips it.
    /// </summary>
    public class VisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool visible;
            if (value == null)
            {
                visible = false;
            }
            else if (value is bool)
            {
                visible = (bool)value;
            }
            else if (value is string)
            {
                visible = !string.IsNullOrEmpty((string)value);
            }
            else if (value is int)
            {
                visible = (int)value != 0;
            }
            else if (value is double)
            {
                visible = (double)value != 0;
            }
            else
            {
                visible = true;
            }

            if (parameter is string && string.Equals((string)parameter, "invert", StringComparison.OrdinalIgnoreCase))
            {
                visible = !visible;
            }

            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// ControlState → one of four values (brushes, glow effects...), so the status orb and
    /// pills share one mapping. Properties are objects so the same class serves any target.
    /// </summary>
    public class ControlStateToBrushConverter : IValueConverter
    {
        public object Off { get; set; }
        public object Standby { get; set; }
        public object Ready { get; set; }
        public object Active { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is ControlState))
            {
                return Off;
            }

            switch ((ControlState)value)
            {
                case ControlState.Standby: return Standby;
                case ControlState.Ready: return Ready;
                case ControlState.Active: return Active;
                default: return Off;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// VoiceHudState → one of five values (brushes, glows), for the voice HUD everywhere.
    /// </summary>
    public class VoiceStateToValueConverter : IValueConverter
    {
        public object Hidden { get; set; }
        public object Wake { get; set; }
        public object Listening { get; set; }
        public object Executed { get; set; }
        public object Rejected { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is VoiceHudState))
            {
                return Hidden;
            }

            switch ((VoiceHudState)value)
            {
                case VoiceHudState.Wake: return Wake;
                case VoiceHudState.Listening: return Listening;
                case VoiceHudState.Executed: return Executed;
                case VoiceHudState.Rejected: return Rejected;
                default: return Hidden;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    public class MicrophoneStatusToValueConverter : IValueConverter
    {
        public object Off { get; set; }
        public object Ready { get; set; }
        public object Reinitializing { get; set; }
        public object Unavailable { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is MicrophoneStatus))
            {
                return Off;
            }

            switch ((MicrophoneStatus)value)
            {
                case MicrophoneStatus.Ready: return Ready;
                case MicrophoneStatus.Reinitializing: return Reinitializing;
                case MicrophoneStatus.Unavailable: return Unavailable;
                default: return Off;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Microphone level zone (0 too quiet, 1 good, 2 very loud) to a value.
    /// </summary>
    public class MicZoneToValueConverter : IValueConverter
    {
        public object Quiet { get; set; }
        public object Good { get; set; }
        public object Loud { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int)
            {
                switch ((int)value)
                {
                    case 1: return Good;
                    case 2: return Loud;
                }
            }

            return Quiet;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    public class ActivityKindToBrushConverter : IValueConverter
    {
        public Brush Action { get; set; }
        public Brush Control { get; set; }
        public Brush Tracking { get; set; }
        public Brush Sensor { get; set; }
        public Brush Calibration { get; set; }
        public Brush Profile { get; set; }
        public Brush Voice { get; set; }
        public Brush System { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is ActivityKind))
            {
                return System;
            }

            switch ((ActivityKind)value)
            {
                case ActivityKind.Action: return Action;
                case ActivityKind.Control: return Control;
                case ActivityKind.Tracking: return Tracking;
                case ActivityKind.Sensor: return Sensor;
                case ActivityKind.Calibration: return Calibration;
                case ActivityKind.Profile: return Profile;
                case ActivityKind.Voice: return Voice;
                default: return System;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Resource key (string) → the Geometry registered under it, so a view model can name an
    /// icon without referencing WPF types.
    /// </summary>
    public class IconKeyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value as string;
            if (string.IsNullOrEmpty(key) || Application.Current == null)
            {
                return null;
            }

            return Application.Current.TryFindResource(key) as Geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Multiplies a number by the parameter (e.g. a 0-1 progress into a pixel width).
    /// </summary>
    public class ScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double number = ToDouble(value);
            double factor = ToDouble(parameter, 1);
            return number * factor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }

        internal static double ToDouble(object value, double fallback = 0)
        {
            if (value == null)
            {
                return fallback;
            }

            double result;
            return double.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
    }

    /// <summary>
    /// 0-1 → "42%".
    /// </summary>
    public class PercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (ScaleConverter.ToDouble(value) * 100).ToString("0") + "%";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Offsets a number by half the parameter (centres a dot of that diameter on a coordinate).
    /// </summary>
    public class CenterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return ScaleConverter.ToDouble(value) - ScaleConverter.ToDouble(parameter) * 0.5;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// Converts a number to text with the format given as the parameter. StringFormat in a
    /// binding would do the same, except that it does not work on ContentPresenter targets.
    /// </summary>
    public class FormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string format = parameter as string ?? "0.##";
            return ScaleConverter.ToDouble(value).ToString(format, CultureInfo.CurrentCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
