using System;
using System.Windows;
using System.Windows.Input;

namespace KillerScan.Controls
{
    // ============================================================
    // The family grab cursors: an open hand while hovering something
    // draggable, a closed hand while the drag is live. The two art
    // files share a hotspot (16,22) and a wrist line, so the swap on
    // mousedown reads as the fingers closing rather than the pointer
    // jumping - which is the whole point of the gesture.
    //
    // These replace Cursors.SizeAll everywhere a surface is grabbed and
    // MOVED. A resize (splitter, corner handle) keeps its directional
    // cursor: the hand says "carry this", not "stretch this".
    // ============================================================
    internal static class DragCursors
    {
        private static Cursor? _open;
        private static Cursor? _closed;

        /// <summary>Hover state: shown on any surface that can be picked up.</summary>
        public static Cursor Open => _open ??= Load("open_hand.cur");

        /// <summary>Held state: shown for the whole duration of a drag.</summary>
        public static Cursor Closed => _closed ??= Load("closed_hand.cur");

        /// <summary>Takes the cursor over for the duration of a drag. Global rather than
        /// per-element because a drag runs under a mouse capture, and the pointer regularly
        /// leaves the grabbed element while it is being carried.</summary>
        public static void BeginDrag()
        {
            ArmSafetyNet();
            Mouse.OverrideCursor = Closed;
        }

        /// <summary>Hands the cursor back. Safe to call when no drag is running, so it can be
        /// wired to LostMouseCapture as well as to the button-up path.
        ///
        /// Only clears an override that is OURS. A blanket null would also wipe a wait cursor
        /// some other operation had set, and the drag paths call this defensively from several
        /// places that can run while no drag is in progress.</summary>
        public static void EndDrag()
        {
            if (ReferenceEquals(Mouse.OverrideCursor, _closed)) Mouse.OverrideCursor = null;
        }

        // The override is app-wide, so ONE drag path that fails to release it leaves the closed
        // hand on screen for the rest of the session and hovering never shows the open hand
        // again. Losing activation means no drag of ours can still be running, so it is a safe
        // and total backstop for any path that slips through - including the app being switched
        // away from mid-gesture.
        private static bool _netArmed;
        private static void ArmSafetyNet()
        {
            if (_netArmed || Application.Current is null) return;
            _netArmed = true;
            Application.Current.Deactivated += (_, _) => EndDrag();
        }

        // Falls back to SizeAll rather than throwing: a missing Resource entry in the csproj
        // should cost the nicety, not take the app down on first hover.
        private static Cursor Load(string file)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Resources/" + file, UriKind.Absolute);
                var info = Application.GetResourceStream(uri);
                if (info?.Stream is null) return Cursors.SizeAll;
                using var s = info.Stream;
                return new Cursor(s);
            }
            catch { return Cursors.SizeAll; }
        }
    }
}
