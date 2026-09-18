using System.Windows.Controls;

namespace KinectV2MouseControl
{
    public partial class DisplaysPage : UserControl
    {
        public DisplaysPage()
        {
            InitializeComponent();
            HelpBinding.Attach(CalibrateButton, "Calibrate");
            HelpBinding.Attach(CalibratedRangeCheck, "Calibrated range");
        }
    }
}
