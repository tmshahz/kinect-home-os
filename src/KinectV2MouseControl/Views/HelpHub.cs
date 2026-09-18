using System;

namespace KinectV2MouseControl
{
    /// <summary>
    /// Routes "what does this control do?" requests from any control to the shell's help
    /// drawer. Controls call Show when hovered or when their help glyph is clicked; the shell
    /// listens once. Static, like the log, so a slider deep inside a page needs no reference
    /// to the shell to ask for help.
    /// </summary>
    public static class HelpHub
    {
        public sealed class HelpRequest : EventArgs
        {
            public HelpEntry Entry { get; private set; }

            /// <summary>
            /// True when the user explicitly asked (clicked the glyph), which opens the drawer.
            /// False for hover previews, which only update it when it is already open.
            /// </summary>
            public bool IsExplicit { get; private set; }

            public HelpRequest(HelpEntry entry, bool isExplicit)
            {
                Entry = entry;
                IsExplicit = isExplicit;
            }
        }

        public static event EventHandler<HelpRequest> Requested;

        public static void Show(string title, bool isExplicit)
        {
            HelpEntry entry = ControlHelp.Find(title);
            if (entry == null)
            {
                return;
            }

            EventHandler<HelpRequest> handler = Requested;
            if (handler != null)
            {
                handler.Invoke(null, new HelpRequest(entry, isExplicit));
            }
        }
    }
}
