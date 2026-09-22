using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KinectV2MouseControl
{
    /// <summary>
    /// One monitor drawn on the Displays page, in virtual-desktop pixels relative to the
    /// desktop's top-left corner (so the canvas can start at 0,0 whatever the real origin).
    /// </summary>
    public class MonitorShape : ObservableObject
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Label { get; set; }
        public string ResolutionText { get; set; }
        public bool IsPrimary { get; set; }
        public double LabelSize { get; set; }
        public double DetailSize { get; set; }
        public double Inset { get; set; }
    }

    /// <summary>
    /// Geometry for the Displays page: the monitor layout with the live cursor target, and the
    /// body-relative "hand space" showing the activation thresholds, the region that reaches
    /// the whole desktop, and where both hands are right now.
    ///
    /// Hand space is drawn in millimetres on a fixed canvas so the XAML needs no converters:
    /// X runs -750..+750 mm (left..right of the spine), height runs -250..+1050 mm above
    /// SpineBase, and canvas Y grows downward.
    /// </summary>
    public class DisplaysViewModel : ObservableObject
    {
        private readonly KinectCursorViewModel engine;

        public const double HandCanvasWidth = 1500;
        public const double HandCanvasHeight = 1300;
        private const double HandMinX = -0.75;
        private const double HandMaxHeight = 1.05;

        public DisplaysViewModel(KinectCursorViewModel engine)
        {
            this.engine = engine;
            Monitors = new ObservableCollection<MonitorShape>();
            RebuildMonitors();
            RefreshMapping();
            RefreshLive();

            engine.DesktopChanged += (s, e) => { RebuildMonitors(); RefreshMapping(); };
            engine.StatusRefreshed += (s, e) => RefreshLive();
            engine.PropertyChanged += Engine_PropertyChanged;
        }

        private void Engine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "MoveScale":
                case "UseCalibratedRange":
                case "HandRangeX":
                case "HandRangeY":
                case "HandCenterX":
                case "LeftHandCalibrated":
                case "LeftHandRangeX":
                case "LeftHandRangeY":
                case "LeftHandCenterX":
                case "LeftPointerCenterHeight":
                case "PointerHandName":
                case "IsPointerHandCalibrated":
                case "PointerCenterHeight":
                case "ActivationMinHeight":
                case "ForwardActivationDistance":
                    RefreshMapping();
                    break;
            }
        }

        // ---- Monitors ---------------------------------------------------------------------------

        public ObservableCollection<MonitorShape> Monitors { get; private set; }

        private double desktopWidth = 1920;
        public double DesktopWidth { get { return desktopWidth; } set { Set(ref desktopWidth, value); } }

        private double desktopHeight = 1080;
        public double DesktopHeight { get { return desktopHeight; } set { Set(ref desktopHeight, value); } }

        private string desktopSummary = "";
        public string DesktopSummary { get { return desktopSummary; } set { Set(ref desktopSummary, value); } }

        private string desktopShort = "";
        public string DesktopShort { get { return desktopShort; } set { Set(ref desktopShort, value); } }

        private string desktopDetail = "";
        public string DesktopDetail { get { return desktopDetail; } set { Set(ref desktopDetail, value); } }

        private double cursorX;
        public double CursorX { get { return cursorX; } set { Set(ref cursorX, value); } }

        private double cursorY;
        public double CursorY { get { return cursorY; } set { Set(ref cursorY, value); } }

        private double cursorSize = 40;
        public double CursorSize { get { return cursorSize; } set { Set(ref cursorSize, value); } }

        private bool hasCursor;
        public bool HasCursor { get { return hasCursor; } set { Set(ref hasCursor, value); } }

        /// <summary>
        /// Top-left of the cursor dot, so the canvas can place it without a converter.
        /// </summary>
        public double CursorLeft { get { return cursorX - cursorSize * 0.5; } }
        public double CursorTop { get { return cursorY - cursorSize * 0.5; } }

        private string cursorText = "";
        public string CursorText { get { return cursorText; } set { Set(ref cursorText, value); } }

        public const double HandDotSize = 44;

        public double RightHandLeft { get { return rightHandX - HandDotSize * 0.5; } }
        public double RightHandTop { get { return rightHandY - HandDotSize * 0.5; } }
        public double LeftHandLeft { get { return leftHandX - HandDotSize * 0.5; } }
        public double LeftHandTop { get { return leftHandY - HandDotSize * 0.5; } }

        /// <summary>
        /// Canvas Y of the hip line (height 0) and X of the spine, for the reference axes.
        /// </summary>
        public double HipLineY { get { return ToCanvasY(0); } }
        public double SpineX { get { return ToCanvasX(0); } }

        private void RebuildMonitors()
        {
            DesktopLayout desktop = engine.Desktop;
            MRect bounds = desktop.Bounds;
            double originX = Math.Min(bounds.Left, bounds.Right);
            double originY = Math.Min(bounds.Top, bounds.Bottom);

            DesktopWidth = Math.Max(1, bounds.Width);
            DesktopHeight = Math.Max(1, bounds.Height);
            CursorSize = Math.Max(12, DesktopHeight * 0.035);

            Monitors.Clear();
            MRect[] monitors = desktop.Monitors;
            if (monitors == null || monitors.Length == 0)
            {
                monitors = new MRect[] { bounds };
            }

            for (int i = 0; i < monitors.Length; i++)
            {
                MRect m = monitors[i];
                MonitorShape shape = new MonitorShape();
                shape.X = Math.Min(m.Left, m.Right) - originX;
                shape.Y = Math.Min(m.Top, m.Bottom) - originY;
                shape.Width = m.Width;
                shape.Height = m.Height;
                shape.Label = (i + 1).ToString();
                shape.ResolutionText = m.Width.ToString("0") + " × " + m.Height.ToString("0");
                // The primary monitor is the one whose rect contains the desktop origin (0,0).
                shape.IsPrimary = Math.Min(m.Left, m.Right) <= 0 && Math.Max(m.Left, m.Right) > 0
                    && Math.Min(m.Top, m.Bottom) <= 0 && Math.Max(m.Top, m.Bottom) > 0;
                shape.LabelSize = Math.Max(24, m.Height * 0.18);
                shape.DetailSize = Math.Max(14, m.Height * 0.07);
                shape.Inset = Math.Max(4, DesktopHeight * 0.008);
                Monitors.Add(shape);
            }

            DesktopShort = monitors.Length + (monitors.Length == 1 ? " monitor" : " monitors");
            DesktopDetail = bounds.Width.ToString("0") + " × " + bounds.Height.ToString("0") + " px"
                + " · origin (" + originX.ToString("0") + ", " + originY.ToString("0") + ")";
            DesktopSummary = DesktopShort + " · " + DesktopDetail;
        }

        // ---- Hand space -------------------------------------------------------------------------

        private double reachX;
        public double ReachX { get { return reachX; } set { Set(ref reachX, value); } }

        private double reachY;
        public double ReachY { get { return reachY; } set { Set(ref reachY, value); } }

        private double reachWidth;
        public double ReachWidth { get { return reachWidth; } set { Set(ref reachWidth, value); } }

        private double reachHeight;
        public double ReachHeight { get { return reachHeight; } set { Set(ref reachHeight, value); } }

        private double activationLineY;
        public double ActivationLineY { get { return activationLineY; } set { Set(ref activationLineY, value); } }

        private double pointerCentreY;
        public double PointerCentreY { get { return pointerCentreY; } set { Set(ref pointerCentreY, value); } }

        private double pointerCentreX;
        public double PointerCentreX { get { return pointerCentreX; } set { Set(ref pointerCentreX, value); } }

        private string mappingSummary = "";
        public string MappingSummary { get { return mappingSummary; } set { Set(ref mappingSummary, value); } }

        private string reachText = "";
        public string ReachText { get { return reachText; } set { Set(ref reachText, value); } }

        private string mappingModeText = "";
        public string MappingModeText { get { return mappingModeText; } set { Set(ref mappingModeText, value); } }

        private double rightHandX;
        public double RightHandX { get { return rightHandX; } set { Set(ref rightHandX, value); } }

        private double rightHandY;
        public double RightHandY { get { return rightHandY; } set { Set(ref rightHandY, value); } }

        private double leftHandX;
        public double LeftHandX { get { return leftHandX; } set { Set(ref leftHandX, value); } }

        private double leftHandY;
        public double LeftHandY { get { return leftHandY; } set { Set(ref leftHandY, value); } }

        private bool showRightHand;
        public bool ShowRightHand { get { return showRightHand; } set { Set(ref showRightHand, value); } }

        private bool showLeftHand;
        public bool ShowLeftHand { get { return showLeftHand; } set { Set(ref showLeftHand, value); } }

        private static double ToCanvasX(double metres)
        {
            return (metres - HandMinX) * 1000;
        }

        private static double ToCanvasY(double heightMetres)
        {
            return (HandMaxHeight - heightMetres) * 1000;
        }

        private void RefreshMapping()
        {
            MRect reach = engine.PointerReachRect;

            double left = Math.Max(HandMinX, Math.Min(reach.Left, reach.Right));
            double right = Math.Min(-HandMinX, Math.Max(reach.Left, reach.Right));
            double top = Math.Min(HandMaxHeight, Math.Max(reach.Top, reach.Bottom));
            double bottom = Math.Max(HandMaxHeight - HandCanvasHeight / 1000.0, Math.Min(reach.Top, reach.Bottom));

            ReachX = ToCanvasX(left);
            ReachY = ToCanvasY(top);
            ReachWidth = Math.Max(0, (right - left) * 1000);
            ReachHeight = Math.Max(0, (top - bottom) * 1000);

            ActivationLineY = ToCanvasY(engine.ActivationMinHeight);
            PointerCentreY = ToCanvasY(engine.PointerCenterHeight);
            PointerCentreX = ToCanvasX(reach.Center.X);

            double width = Math.Abs(reach.DeltaX);
            double height = Math.Abs(reach.DeltaY);
            ReachText = width.ToString("0.00") + " m wide × " + height.ToString("0.00") + " m tall, centred "
                + reach.Center.X.ToString("0.00") + " m right of the spine and " + reach.Center.Y.ToString("0.00") + " m above the hips";

            if (engine.IsPointerHandCalibrated)
            {
                bool isLeft = engine.PointerHandName == "Left";
                double rangeX = isLeft ? engine.LeftHandRangeX : engine.HandRangeX;
                double rangeY = isLeft ? engine.LeftHandRangeY : engine.HandRangeY;
                MappingModeText = engine.PointerHandName + " pointer · calibrated";
                MappingSummary = "Independent horizontal and vertical scaling. The "
                    + engine.PointerHandName.ToLowerInvariant() + "-hand rectangle ("
                    + rangeX.ToString("0.00") + " × " + rangeY.ToString("0.00")
                    + " m) maps onto the whole desktop; Movement scale is ignored.";
            }
            else
            {
                MappingModeText = engine.PointerHandName + " pointer · uniform mapping";
                MappingSummary = "The original mapping: one scale for both axes, aligned to the desktop's longer side, multiplied by Movement scale ("
                    + engine.MoveScale.ToString("0.00") + "). "
                    + (engine.PointerHandName == "Left" && !engine.LeftHandCalibrated
                        ? "The left hand has no captured geometry yet; choose Calibrate Left."
                        : "On a wide desktop this makes the vertical reach small - calibrate to fix that.");
            }
        }

        private void RefreshLive()
        {
            LiveStatus s = engine.Status;

            HasCursor = s.HasCursor;
            if (s.HasCursor)
            {
                MRect bounds = engine.Desktop.Bounds;
                CursorX = s.CursorX - Math.Min(bounds.Left, bounds.Right);
                CursorY = s.CursorY - Math.Min(bounds.Top, bounds.Bottom);
                Raise("CursorLeft");
                Raise("CursorTop");
                CursorText = "Cursor target " + s.CursorX.ToString("0") + ", " + s.CursorY.ToString("0") + " px";
            }
            else
            {
                CursorText = "Cursor target appears here while the pointer hand is active";
            }

            ShowRightHand = s.RightHandTracked;
            ShowLeftHand = s.LeftHandTracked;
            RightHandX = Clamp(ToCanvasX(s.HandX), 0, HandCanvasWidth);
            RightHandY = Clamp(ToCanvasY(s.HandHeight), 0, HandCanvasHeight);
            LeftHandX = Clamp(ToCanvasX(s.LeftHandX), 0, HandCanvasWidth);
            LeftHandY = Clamp(ToCanvasY(s.LeftHandHeight), 0, HandCanvasHeight);
            Raise("RightHandLeft");
            Raise("RightHandTop");
            Raise("LeftHandLeft");
            Raise("LeftHandTop");
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
