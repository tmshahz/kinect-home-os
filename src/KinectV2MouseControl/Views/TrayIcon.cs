using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Notification-area icon for KINECT-OS. In compact mode the main window is hidden and the
    /// floating widget can be dragged anywhere, so the tray icon is the one fixed place from
    /// which the control center can always be brought back. The icon is drawn at runtime (a
    /// ring with a dot, in the accent colour) so the project needs no image assets.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon icon;
        private readonly ToolStripMenuItem controlItem;
        private Icon activeIcon;
        private Icon idleIcon;

        public event EventHandler OpenRequested;
        public event EventHandler CompactRequested;
        public event EventHandler ToggleControlRequested;
        public event EventHandler QuitRequested;

        public TrayIcon()
        {
            activeIcon = Draw(Color.FromArgb(79, 195, 255), true);
            idleIcon = Draw(Color.FromArgb(127, 145, 171), false);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open KINECT-OS", null, (s, e) => Raise(OpenRequested));
            menu.Items.Add("Compact mode", null, (s, e) => Raise(CompactRequested));
            controlItem = new ToolStripMenuItem("Kinect control", null, (s, e) => Raise(ToggleControlRequested));
            menu.Items.Add(controlItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Raise(QuitRequested));

            icon = new NotifyIcon();
            icon.Icon = idleIcon;
            icon.Text = "KINECT-OS";
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += (s, e) => Raise(OpenRequested);
            icon.Visible = true;
        }

        /// <summary>
        /// Keeps the icon and the menu's control entry in step with the engine.
        /// </summary>
        public void Update(bool isActive, bool isControlEnabled, string headline)
        {
            icon.Icon = isActive ? activeIcon : idleIcon;
            controlItem.Text = isControlEnabled ? "Kinect control: on (switch off)" : "Kinect control: off (switch on)";

            string text = "KINECT-OS · " + headline;
            icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private static Icon Draw(Color accent, bool filled)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (Pen ring = new Pen(accent, 3.5f))
                {
                    g.DrawEllipse(ring, 4, 4, 24, 24);
                }

                using (Brush dot = new SolidBrush(filled ? accent : Color.FromArgb(120, accent)))
                {
                    g.FillEllipse(dot, 11, 11, 10, 10);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    // Icon.FromHandle does not own the handle; clone so the GDI handle can be
                    // released now instead of leaking for the life of the process.
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            if (activeIcon != null)
            {
                activeIcon.Dispose();
                activeIcon = null;
            }

            if (idleIcon != null)
            {
                idleIcon.Dispose();
                idleIcon = null;
            }
        }
    }
}
