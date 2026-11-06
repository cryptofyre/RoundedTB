using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace RoundedTB
{
    /// <summary>Borderless, topmost tool-window chrome shared by the flyout and the tray menu.</summary>
    internal static class PopupChrome
    {
        private static readonly Native.SubclassProc NoFrameProc = NoFrame;

        public static IntPtr Apply(Window window)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(window);
            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            window.AppWindow.SetPresenter(presenter);
            window.AppWindow.IsShownInSwitchers = false;

            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex);

            // The presenter re-adds WS_DLGFRAME on every show, leaving a 3px frame that DWM paints
            // white. Claim the whole window as client area instead of fighting over styles.
            Native.SetWindowSubclass(hwnd, NoFrameProc, 1, IntPtr.Zero);
            // Without this the clear parts of the window render black.
            Native.DwmExtendFrameIntoClientArea(hwnd, new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 });
            return hwnd;
        }

        public static void SetAttribute(IntPtr hwnd, int attribute, int value) =>
            Native.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

        public static void SetCloaked(IntPtr hwnd, bool cloaked) => SetAttribute(hwnd, Native.DWMWA_CLOAK, cloaked ? 1 : 0);

        private static IntPtr NoFrame(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, IntPtr refData)
        {
            if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// A Grid that reports height changes from its arrange pass. SizeChanged arrives after the frame
    /// with the new size has already been composed, which is a frame too late to start an animation
    /// from the old size.
    /// </summary>
    public sealed partial class CardPanel : Microsoft.UI.Xaml.Controls.Grid
    {
        private double arrangedHeight;

        public event Action<float, float> HeightChanging;

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            if (arrangedHeight > 0 && Math.Abs(finalSize.Height - arrangedHeight) > 0.5)
            {
                HeightChanging?.Invoke((float)arrangedHeight, (float)finalSize.Height);
            }
            arrangedHeight = finalSize.Height;
            return base.ArrangeOverride(finalSize);
        }
    }
}
