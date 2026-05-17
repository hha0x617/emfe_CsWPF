using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace emfe;

public partial class FramebufferWindow : Window
{
    private readonly IntPtr _instance;
    private readonly PluginInterop _plugin;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _bitmap;
    private uint _lastWidth, _lastHeight, _lastBpp;
    private uint[]? _palette;
    private bool _inputCaptured;
    // True once the window has been resized to match the first observed
    // framebuffer dimensions (dot-by-dot client area).  One-shot: the user can
    // still resize the window manually afterwards.
    private bool _initialSizeApplied;

    // FPS tracking
    private readonly Stopwatch _fpsWatch = new();
    private int _frameCount;
    private double _currentFps;

    public FramebufferWindow(IntPtr instance, PluginInterop plugin)
    {
        _instance = instance;
        _plugin = plugin;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);
            StartTimer();
        };
        Closed += (_, _) =>
        {
            StopTimer();
            _initialSizeApplied = false;
        };
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)  // ~30fps
        };
        _timer.Tick += (_, _) => RefreshFrame();
        _fpsWatch.Start();
        _timer.Start();
        RefreshFrame();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void RefreshFrame()
    {
        if (_instance == IntPtr.Zero) return;

        var res = _plugin.emfe_get_framebuffer_info(_instance, out var info);
        if (res != EmfeResult.OK || info.pixels == IntPtr.Zero || info.width == 0 || info.height == 0)
        {
            FbImage.Source = null;
            DisabledText.Visibility = Visibility.Visible;
            StatusText.Text = "Framebuffer disabled";
            return;
        }

        DisabledText.Visibility = Visibility.Collapsed;

        // Recreate bitmap if format/size changed
        if (_bitmap == null || _lastWidth != info.width || _lastHeight != info.height || _lastBpp != info.bpp)
        {
            _bitmap = new WriteableBitmap((int)info.width, (int)info.height, 96, 96, PixelFormats.Bgra32, null);
            FbImage.Source = _bitmap;
            _lastWidth = info.width;
            _lastHeight = info.height;
            _lastBpp = info.bpp;
            if ((EmfeFramebufferFormat)info.bpp == EmfeFramebufferFormat.Indexed8)
            {
                _palette = new uint[256];
                _plugin.emfe_get_palette(_instance, _palette, 256);
            }
            else
            {
                _palette = null;
            }
        }

        // Resize the window so the image renders dot-by-dot (client area in
        // DIPs matches the bitmap's native size — which is the framebuffer's
        // pixel dimensions because the bitmap is created at 96 DPI).  Done
        // once on the first valid frame; subsequent user-driven resizes are
        // respected.  Reset by the Closed handler so a re-open re-fits.
        if (!_initialSizeApplied && _lastWidth > 0 && _lastHeight > 0)
        {
            _initialSizeApplied = true;
            // WPF reports the non-client (chrome) thickness of the standard
            // window frame.  Client size + this thickness == outer Width/Height.
            var nonClient = SystemParameters.WindowNonClientFrameThickness;
            // Status bar height in DIPs (matches XAML: Padding 8,4 + 12pt font).
            const double statusBarDip = 28.0;
            Width  = _lastWidth + nonClient.Left + nonClient.Right;
            Height = _lastHeight + statusBarDip + nonClient.Top + nonClient.Bottom;
        }

        int width = (int)info.width;
        int height = (int)info.height;
        int srcStride = (int)info.stride;
        int dstStride = width * 4;
        var dst = new byte[height * dstStride];

        ConvertToBgra32(info, dst, dstStride);

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), dst, dstStride, 0);

        // FPS calculation
        _frameCount++;
        if (_fpsWatch.ElapsedMilliseconds >= 1000)
        {
            _currentFps = _frameCount * 1000.0 / _fpsWatch.ElapsedMilliseconds;
            _frameCount = 0;
            _fpsWatch.Restart();
        }

        StatusText.Text = $"{width}x{height} {info.bpp}bpp  ${info.base_address:X8}  {_currentFps:F1} fps";
        InputStatusText.Text = _inputCaptured
            ? "Keyboard: captured (Esc to release)"
            : "Click framebuffer to capture keyboard";
    }

    private void ConvertToBgra32(EmfeFramebufferInfo info, byte[] dst, int dstStride)
    {
        int width = (int)info.width;
        int height = (int)info.height;
        int srcStride = (int)info.stride;
        var format = (EmfeFramebufferFormat)info.bpp;

        // Read source pixels into managed buffer
        int srcSize = srcStride * height;
        var src = new byte[srcSize];
        Marshal.Copy(info.pixels, src, 0, srcSize);

        switch (format)
        {
            case EmfeFramebufferFormat.Indexed8:
                ConvertIndexed8(src, dst, width, height, srcStride, dstStride);
                break;
            case EmfeFramebufferFormat.Rgb565:
                ConvertRgb565(src, dst, width, height, srcStride, dstStride);
                break;
            case EmfeFramebufferFormat.Rgb888:
                ConvertRgb888(src, dst, width, height, srcStride, dstStride);
                break;
            case EmfeFramebufferFormat.Rgba8888:
                ConvertRgba8888(src, dst, width, height, srcStride, dstStride);
                break;
        }
    }

    private void ConvertIndexed8(byte[] src, byte[] dst, int w, int h, int srcStride, int dstStride)
    {
        var palette = _palette ?? new uint[256];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < w; x++)
            {
                uint argb = palette[src[srcRow + x]];
                dst[dstRow + x * 4 + 0] = (byte)(argb & 0xFF);          // B
                dst[dstRow + x * 4 + 1] = (byte)((argb >> 8) & 0xFF);   // G
                dst[dstRow + x * 4 + 2] = (byte)((argb >> 16) & 0xFF);  // R
                dst[dstRow + x * 4 + 3] = 0xFF;                         // A
            }
        }
    }

    private static void ConvertRgb565(byte[] src, byte[] dst, int w, int h, int srcStride, int dstStride)
    {
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < w; x++)
            {
                // m68k is big-endian: high byte first
                ushort px = (ushort)((src[srcRow + x * 2] << 8) | src[srcRow + x * 2 + 1]);
                byte r = (byte)((px >> 11) & 0x1F);
                byte g = (byte)((px >> 5) & 0x3F);
                byte b = (byte)(px & 0x1F);
                // Expand 5/6/5 bits to 8 bits
                dst[dstRow + x * 4 + 0] = (byte)((b << 3) | (b >> 2));
                dst[dstRow + x * 4 + 1] = (byte)((g << 2) | (g >> 4));
                dst[dstRow + x * 4 + 2] = (byte)((r << 3) | (r >> 2));
                dst[dstRow + x * 4 + 3] = 0xFF;
            }
        }
    }

    private static void ConvertRgb888(byte[] src, byte[] dst, int w, int h, int srcStride, int dstStride)
    {
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < w; x++)
            {
                dst[dstRow + x * 4 + 0] = src[srcRow + x * 3 + 2]; // B
                dst[dstRow + x * 4 + 1] = src[srcRow + x * 3 + 1]; // G
                dst[dstRow + x * 4 + 2] = src[srcRow + x * 3 + 0]; // R
                dst[dstRow + x * 4 + 3] = 0xFF;                    // A
            }
        }
    }

    private static void ConvertRgba8888(byte[] src, byte[] dst, int w, int h, int srcStride, int dstStride)
    {
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * dstStride;
            for (int x = 0; x < w; x++)
            {
                dst[dstRow + x * 4 + 0] = src[srcRow + x * 4 + 2]; // B
                dst[dstRow + x * 4 + 1] = src[srcRow + x * 4 + 1]; // G
                dst[dstRow + x * 4 + 2] = src[srcRow + x * 4 + 0]; // R
                dst[dstRow + x * 4 + 3] = src[srcRow + x * 4 + 3]; // A
            }
        }
    }

    // ========================================================================
    // Input capture
    // ========================================================================

    // Mouse moves and clicks always flow to the guest when the cursor is over
    // FbGrid — the standalone em68030_WinUI3Cpp does the same, and the prior
    // capture-gate dropped every MouseMove before the first click, so X never
    // saw cursor motion.
    //
    // _inputCaptured still controls KEYBOARD forwarding so the user can press
    // Esc to release without the guest swallowing it, and acts as the
    // "hide host cursor + take focus" trigger on first click.
    //
    // Button codes are Linux BTN_LEFT/RIGHT/MIDDLE so the guest's em68030input
    // driver passes them via input_report_key() — the previous 0/1/2 codes
    // failed the kbit lookup on tablet_dev/mouse_dev and were silently dropped.

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_inputCaptured) return;
        if (e.Key == Key.Escape) { ReleaseInput(); e.Handled = true; return; }
        // Ctrl+Shift+V: paste clipboard text as synthetic key events.  Must
        // come before the normal VK→KEY dispatch so V is not also forwarded.
        if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            DoFramebufferPaste();
            e.Handled = true;
            return;
        }
        // MapVirtualKey(VK_TO_VSC) returns a PS/2 set-1 scan code; the guest
        // em68030input driver expects Linux KEY_* codes instead.
        ushort code = WpfKeyToLinuxKey(e);
        if (code != 0) { _plugin.emfe_push_key(_instance, code, true); e.Handled = true; }
    }

    // Paste the host clipboard into the guest as synthetic key events.  Mirrors
    // emfe_WinUI3Cpp::DoFramebufferPaste and matches em68030_CsWPF's
    // InputDevice.PushTextInput — the modifiers physically held by the user
    // are released in the guest first so each pasted char isn't modified.
    private void DoFramebufferPaste()
    {
        if (_instance == IntPtr.Zero) return;
        if (!Clipboard.ContainsText()) return;
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;

        const ushort KEY_LEFTSHIFT = 42;
        const ushort KEY_LEFTCTRL  = 29;
        _plugin.emfe_push_key(_instance, KEY_LEFTSHIFT, false);
        _plugin.emfe_push_key(_instance, KEY_LEFTCTRL,  false);

        foreach (char ch in text)
        {
            // '\n' alone produces KEY_ENTER, so skip CR in CRLF.
            if (ch == '\r') continue;
            if (ch > 0x7F) continue;  // non-ASCII silently dropped
            var (keyCode, needShift) = KeyMapping.CharToScancode(ch);
            if (keyCode == 0) continue;
            if (needShift) _plugin.emfe_push_key(_instance, KEY_LEFTSHIFT, true);
            _plugin.emfe_push_key(_instance, keyCode, true);
            _plugin.emfe_push_key(_instance, keyCode, false);
            if (needShift) _plugin.emfe_push_key(_instance, KEY_LEFTSHIFT, false);
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_inputCaptured) return;
        ushort code = WpfKeyToLinuxKey(e);
        if (code != 0) { _plugin.emfe_push_key(_instance, code, false); e.Handled = true; }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_inputCaptured)
        {
            CaptureInput();
        }
        int btn = MouseButtonToLinuxBtn(e.ChangedButton);
        if (btn >= 0) _plugin.emfe_push_mouse_button(_instance, btn, true);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        int btn = MouseButtonToLinuxBtn(e.ChangedButton);
        if (btn >= 0) _plugin.emfe_push_mouse_button(_instance, btn, false);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastWidth == 0 || _lastHeight == 0) return;
        var (x, y) = MapMouseToFramebuffer(e.GetPosition(FbGrid));
        _plugin.emfe_push_mouse_absolute(_instance, x, y);
    }

    private static int MouseButtonToLinuxBtn(MouseButton b) => b switch
    {
        MouseButton.Left   => KeyMapping.LINUX_BTN_LEFT,
        MouseButton.Right  => KeyMapping.LINUX_BTN_RIGHT,
        MouseButton.Middle => KeyMapping.LINUX_BTN_MIDDLE,
        _ => -1
    };

    private (int x, int y) MapMouseToFramebuffer(Point pos)
    {
        double gridW = FbGrid.ActualWidth;
        double gridH = FbGrid.ActualHeight;
        if (gridW <= 0 || gridH <= 0) return (0, 0);

        double imgAspect = (double)_lastWidth / _lastHeight;
        double gridAspect = gridW / gridH;

        double renderW, renderH, offX, offY;
        if (gridAspect > imgAspect)
        {
            renderH = gridH; renderW = gridH * imgAspect;
            offX = (gridW - renderW) / 2; offY = 0;
        }
        else
        {
            renderW = gridW; renderH = gridW / imgAspect;
            offX = 0; offY = (gridH - renderH) / 2;
        }

        int x = (int)((pos.X - offX) / renderW * _lastWidth);
        int y = (int)((pos.Y - offY) / renderH * _lastHeight);
        return (Math.Clamp(x, 0, (int)_lastWidth - 1), Math.Clamp(y, 0, (int)_lastHeight - 1));
    }

    private void CaptureInput()
    {
        _inputCaptured = true;
        FbGrid.CaptureMouse();
        FbGrid.Cursor = Cursors.None;
        Focus();
    }

    private void ReleaseInput()
    {
        _inputCaptured = false;
        FbGrid.ReleaseMouseCapture();
        FbGrid.Cursor = Cursors.Cross;
    }

    private static ushort WpfKeyToLinuxKey(KeyEventArgs e)
    {
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        return KeyMapping.WindowsVkToLinuxKey(vk);
    }
}
