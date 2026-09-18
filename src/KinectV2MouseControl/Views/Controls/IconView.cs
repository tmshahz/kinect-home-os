using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Draws one of the stroke icons from Themes/Icons.xaml at a given size, in the inherited
    /// Foreground colour. The template lives in Controls.xaml. A control rather than a Path in
    /// every page so the stroke width and caps stay consistent everywhere.
    /// </summary>
    public class IconView : Control
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register("Icon", typeof(Geometry), typeof(IconView), new PropertyMetadata(null));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register("Size", typeof(double), typeof(IconView), new PropertyMetadata(18.0));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register("StrokeThickness", typeof(double), typeof(IconView), new PropertyMetadata(1.8));

        static IconView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(typeof(IconView)));
            IsTabStopProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(false));
            FocusableProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(false));
        }

        public Geometry Icon
        {
            get { return (Geometry)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        public double StrokeThickness
        {
            get { return (double)GetValue(StrokeThicknessProperty); }
            set { SetValue(StrokeThicknessProperty, value); }
        }
    }
}
