using System;
using System.Windows.Data;

namespace KinectV2MouseControl
{
    public class RadioCheckedToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (int)value == int.Parse(parameter.ToString());
        }

        /// <summary>
        /// Only the radio being checked may write the mode. The radio being unchecked must write
        /// nothing: returning its parameter here too made every mode change also select the
        /// previous mode, so closing the app (which selects Disabled) immediately re-selected
        /// the old mode, reopening the sensor and restarting cursor output during shutdown.
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return (value is bool && (bool)value) ? int.Parse(parameter.ToString()) : Binding.DoNothing;
        }
    }
}
