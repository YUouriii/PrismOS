using System.Runtime.InteropServices;
using PrismOS.Common;
using PrismOS.Interop;
using static PrismOS.Interop.Win32Constants;

namespace PrismOS.Widgets;

/// <summary>An invisible, clickable window over the transport buttons of the music pill.
///
/// The wallpaper visual (composed by DirectComposition) is WS_EX_NOREDIRECTIONBITMAP with
/// nothing to catch clicks, and it deliberately sits <em>below</em> the desktop icons so they stay
/// usable — so a button drawn there is under the mouse but never under the click. The hit target
/// is therefore a small top-level window inserted just above the desktop layer: clicks on the
/// wallpaper reach it, while ordinary windows, which sit above it, keep theirs.
///
/// It paints nothing, lives exactly over the buttons, and only answers WM_LBUTTONDOWN. Alpha is
/// zero but it is not WS_EX_TRANSPARENT, so it is clickable while invisible.</summary>
public sealed class RecordHitWindow : Win32Window
{
    private const string ClassName = "PrismOSMediaHitTarget";
    private const int SwHide = 0;
    private const uint LwaAlpha = 0x2;

    private readonly IntPtr _desktopLayer;
    private Rectangle _bounds;
    private readonly Action<int> _onButton;
    private IReadOnlyList<Rectangle> _buttons = [];
    private bool _logged;

    /// <summary><paramref name="desktopLayer"/> is the top-level desktop window (Progman on a
    /// raised desktop); the hit window is inserted directly above it.</summary>
    public RecordHitWindow(IntPtr desktopLayer, Action<int> onButton)
    {
        _onButton = onButton;
        _desktopLayer = desktopLayer;
        CreateWindow(ClassName, WS_POPUP | WS_VISIBLE, WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            0, 0, 1, 1, IntPtr.Zero);
        User32.SetLayeredWindowAttributes(Hwnd, 0, 0, LwaAlpha);
        PlaceInDesktopLayer();
        Log.Info($"Media hit window 0x{Hwnd:X} above desktop 0x{desktopLayer:X}");
    }

    /// <summary>Places the window over the buttons, in virtual-screen coordinates.
    /// A zero-sized rectangle hides it.</summary>
    public void Reposition(Rectangle bounds, IReadOnlyList<Rectangle> screenButtons)
    {
        _bounds = bounds;
        _buttons = screenButtons;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            User32.ShowWindow(Hwnd, SwHide);
            return;
        }
        User32.SetWindowPos(Hwnd, AboveDesktop(), bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        User32.SetLayeredWindowAttributes(Hwnd, 0, 0, LwaAlpha);
    }

    public void Hide() => User32.ShowWindow(Hwnd, SwHide);

    /// <summary>The window preceding the desktop in the top-level z-order. Our window goes behind
    /// that one — above the desktop, below every app the user opens.</summary>
    private IntPtr AboveDesktop()
    {
        IntPtr above = _desktopLayer != IntPtr.Zero ? User32.GetWindow(_desktopLayer, GW_HWNDPREV) : IntPtr.Zero;
        return above == _desktopLayer || above == Hwnd ? HWND_TOP : above;
    }

    private void PlaceInDesktopLayer() => User32.SetWindowPos(Hwnd, AboveDesktop(), 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    protected override IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_LBUTTONDOWN)
        {
            int packed = lParam.ToInt32();
            int x = _bounds.X + (short)(packed & 0xFFFF);
            int y = _bounds.Y + (short)((packed >> 16) & 0xFFFF);
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].Contains(new Point(x, y)))
                {
                    if (!_logged) { _logged = true; Log.Info($"Media button {i} clicked at ({x},{y})"); }
                    _onButton(i);
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }
        return base.HandleMessage(msg, wParam, lParam);
    }
}
