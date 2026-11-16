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
/// therefore lives in the same layer as the icons, raised above them: transparent and
/// no-activate, so nothing shows and it never steals focus, and it stays below ordinary windows.
///
/// It paints nothing, lives exactly over the buttons, and only answers WM_LBUTTONDOWN. Alpha is
/// zero but it is not WS_EX_TRANSPARENT, so it is clickable while invisible.</summary>
public sealed class RecordHitWindow : Win32Window
{
    private const string ClassName = "PrismOSMediaHitTarget";
    private const int SwHide = 0, SwShow = 5;
    private const uint LwaAlpha = 0x2;

    private Rectangle _bounds;
    private readonly Action<int> _onButton;
    private IReadOnlyList<Rectangle> _buttons = [];

    /// <summary>Child of the desktop layer. Cool-down clicks pass to it before the
    /// parent deliberately swallows them.</summary>
    public RecordHitWindow(IntPtr parent, Action<int> onButton)
    {
        _onButton = onButton;
        CreateWindow(ClassName, WS_CHILD | WS_VISIBLE, WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            0, 0, 1, 1, parent);
        // Top of the parent's child order: above SHELLDLL_DefView, whose clicks would otherwise
        // reach the desktop list before ours.
        User32.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        User32.SetLayeredWindowAttributes(Hwnd, 0, 0, LwaAlpha);
    }

    /// <summary>Places the window over the buttons, in the parent's client coordinates.
    /// A zero-sized rectangle hides it.</summary>
    public void Reposition(Rectangle bounds, IReadOnlyList<Rectangle> parentButtons)
    {
        _bounds = bounds;
        _buttons = parentButtons;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            User32.ShowWindow(Hwnd, SwHide);
            return;
        }
        User32.SetWindowPos(Hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        User32.SetLayeredWindowAttributes(Hwnd, 0, 0, LwaAlpha);
    }

    public void Hide() => User32.ShowWindow(Hwnd, SwHide);

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
                    _onButton(i);
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }
        return base.HandleMessage(msg, wParam, lParam);
    }
}
