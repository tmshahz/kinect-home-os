using System.Windows;
using System.Windows.Controls;

namespace KinectV2MouseControl
{
    public partial class GesturesPage : UserControl
    {
        public GesturesPage()
        {
            InitializeComponent();
            HelpBinding.Attach(StationaryLockCheck, "Stationary lock");
            HelpBinding.Attach(InvertScrollCheck, "Invert scroll");
        }
    }

    /// <summary>
    /// Gives a plain control (toggle, button) the same help behaviour a TuningSlider has:
    /// tooltip from ControlHelp and a hover preview in the help drawer.
    /// </summary>
    public static class HelpBinding
    {
        public static void Attach(FrameworkElement element, string helpTitle)
        {
            string tooltip = ControlHelp.BuildTooltip(helpTitle);
            if (tooltip != null)
            {
                element.ToolTip = tooltip;
                ToolTipService.SetShowDuration(element, 30000);
                ToolTipService.SetInitialShowDelay(element, 600);
            }

            element.MouseEnter += (s, e) => HelpHub.Show(helpTitle, false);
        }
    }
}
