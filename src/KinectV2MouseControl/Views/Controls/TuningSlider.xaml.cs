using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KinectV2MouseControl
{
    /// <summary>
    /// A labelled tuning slider with direct value entry and a help glyph. Hovering it previews
    /// its explanation in the help drawer (when open); the glyph opens the drawer on it.
    /// HelpKey defaults to the label and must match a ControlHelp entry title.
    /// </summary>
    public partial class TuningSlider : UserControl
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register("Label", typeof(string), typeof(TuningSlider), new PropertyMetadata("", OnLabelChanged));

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register("Unit", typeof(string), typeof(TuningSlider), new PropertyMetadata(""));

        public static readonly DependencyProperty HelpKeyProperty =
            DependencyProperty.Register("HelpKey", typeof(string), typeof(TuningSlider), new PropertyMetadata(null, OnLabelChanged));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double), typeof(TuningSlider),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register("Minimum", typeof(double), typeof(TuningSlider), new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register("Maximum", typeof(double), typeof(TuningSlider), new PropertyMetadata(1.0));

        public static readonly DependencyProperty SmallChangeProperty =
            DependencyProperty.Register("SmallChange", typeof(double), typeof(TuningSlider), new PropertyMetadata(0.01));

        public static readonly DependencyProperty LargeChangeProperty =
            DependencyProperty.Register("LargeChange", typeof(double), typeof(TuningSlider), new PropertyMetadata(0.1));

        public TuningSlider()
        {
            InitializeComponent();
        }

        public string Label
        {
            get { return (string)GetValue(LabelProperty); }
            set { SetValue(LabelProperty, value); }
        }

        public string Unit
        {
            get { return (string)GetValue(UnitProperty); }
            set { SetValue(UnitProperty, value); }
        }

        /// <summary>
        /// ControlHelp title to explain this control. Defaults to Label.
        /// </summary>
        public string HelpKey
        {
            get { return (string)GetValue(HelpKeyProperty); }
            set { SetValue(HelpKeyProperty, value); }
        }

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public double Minimum
        {
            get { return (double)GetValue(MinimumProperty); }
            set { SetValue(MinimumProperty, value); }
        }

        public double Maximum
        {
            get { return (double)GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); }
        }

        public double SmallChange
        {
            get { return (double)GetValue(SmallChangeProperty); }
            set { SetValue(SmallChangeProperty, value); }
        }

        public double LargeChange
        {
            get { return (double)GetValue(LargeChangeProperty); }
            set { SetValue(LargeChangeProperty, value); }
        }

        private string EffectiveHelpKey
        {
            get
            {
                return string.IsNullOrEmpty(HelpKey) ? Label : HelpKey;
            }
        }

        private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            TuningSlider slider = (TuningSlider)d;
            string tooltip = ControlHelp.BuildTooltip(slider.EffectiveHelpKey);
            slider.ToolTip = tooltip;
            if (tooltip != null)
            {
                ToolTipService.SetShowDuration(slider, 30000);
                ToolTipService.SetInitialShowDelay(slider, 600);
            }
        }

        private void Root_MouseEnter(object sender, MouseEventArgs e)
        {
            HelpHub.Show(EffectiveHelpKey, false);
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            HelpHub.Show(EffectiveHelpKey, true);
        }

        /// <summary>
        /// Enter commits a typed value without having to click elsewhere.
        /// </summary>
        private void ValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                ValueBox.GetBindingExpression(TextBox.TextProperty).UpdateSource();
                e.Handled = true;
            }
        }
    }
}
