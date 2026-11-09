using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>
/// Small always-on-top counter window drawn with plain Win32/GDI (no WinForms needed).
/// Left-drag to move it, right-click to hide it (Ctrl+Shift+O or the counter's menu shows it again).
/// Visible over Fortnite when the game runs in "windowed fullscreen" (not exclusive fullscreen).
/// </summary>
/// <summary>What the overlay shows. <see cref="Current"/> null = no match in progress.</summary>
/// <param name="Detail">Optional secondary text drawn small and grey after the value.</param>
public sealed record OverlayRow(string Label, string Value, bool Treasure = false, string? Detail = null);

/// <param name="Current">Right counter (match in progress, or last match); null = "—".</param>
/// <param name="CurrentLive">Right counter is the match in progress (green).</param>
/// <param name="Badge">Header badge ("● EN DIRECT" / "● LIVE", "● PARTIE EN COURS" / "● MATCH IN PROGRESS") or null.</param>
public sealed record OverlayView(string Total, string? Current, string CurrentCaption, bool CurrentLive, string? Badge, IReadOnlyList<OverlayRow> Rows, string Treasures = "0", string Session = "0", string? Last = null);

[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static volatile OverlayView _view = new("0", null, OverlayModel.LastMatchCaption, false, null, Array.Empty<OverlayRow>());

    // Layout (computed by Relayout, used by Paint; both on the overlay thread).
    private static int _headerH, _bigH, _captionH, _rowH, _col1W, _col2W, _labelW;
    private static IntPtr _fontHeader, _fontCaption, _fontLabel;
    private static IntPtr _brushBorder, _brushLine;
    private static string Header => L.T("COFFRES", "CHESTS", "COFRES");
    private const string CaptionTotal = "total";
    private static Action<int, int>? _onMoved;
    private static Action<bool>? _onLockChanged;
    private static volatile bool _locked;
    private static bool _offscreen;
    private static bool _autoPosition;
    private static float _hudScale;
    private static int _hudCellW, _hudRowH;
    private static int _stableW;
    private static RECT _lockRect; // lock button, client coordinates
    private static IntPtr _fontIcon;
    private static WndProcDelegate? _wndProc; // keep the delegate alive
    private static IntPtr _fontBig, _fontSmall, _brushBg;
    private static float _scale = 1f;    // display scaling × size chosen by the player
    private static float _dpiScale = 1f; // display scaling only
    private static volatile int _sizePercent = 100;
    private static volatile int _opacityPercent = 100;

    /// <summary>"hud" (figures only, under the minimap, like the game's HUD) or "classic" (dark card).</summary>
    private static volatile string _style = "classic";
    private static IntPtr _fontHud;

    // Reset WS_EX_LAYERED when switching from uniform to per-pixel alpha.
    private static bool _usesUniformAlpha;

    public static string[] Styles => new[] { "hud", "classic" };

    /// <summary>Changes the look of the overlay at once.</summary>
    public static void SetStyle(string style)
    {
        _style = Array.IndexOf(Styles, style) >= 0 ? style : "hud";
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static bool IsHud => _style != "classic";

    private const int MinWidth = 240, Pad = 16, ColGap = 36;

    /// <summary>Size and opacity of the overlay (percent). Applied at once when the overlay is shown.</summary>
    public static void SetAppearance(int sizePercent, int opacityPercent)
    {
        _sizePercent = OverlayAppearance.ClampSize(sizePercent);
        _opacityPercent = OverlayAppearance.ClampOpacity(opacityPercent);
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static byte Alpha => (byte)Math.Round(_opacityPercent * 255 / 100.0);

    public static bool IsRunning => _hwnd != IntPtr.Zero;

    /// <summary>True when the overlay window is shown (not hidden with the shortcut or the menu).</summary>
    public static bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Closes the overlay window and its thread (overlay turned off in the settings). Start() can open it again.</summary>
    public static void Stop()
    {
        var thread = _thread;
        if (thread == null)
        {
            return;
        }
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>True when the global Ctrl+Shift+O shortcut could be registered.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>True when the global Ctrl+Shift+L shortcut (lock/unlock) could be registered.</summary>
    public static bool LockHotkeyRegistered { get; private set; }

    /// <summary>Locked = click-through: the mouse goes through the overlay and it cannot be moved.</summary>
    public static bool IsLocked => _locked;

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged, bool autoPosition = true)
    {
        if (_thread != null)
        {
            return;
        }

        _onMoved = onMoved;
        _autoPosition = autoPosition;
        _offscreen = x <= -10000; // --overlay-preview: never brought back on screen
        _onLockChanged = onLockChanged;
        _locked = locked;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                Run(x, y, ready);
            }
            catch (Exception ex)
            {
                Console.WriteLine(L.T($"Overlay indisponible : {ex.Message}", $"Overlay unavailable: {ex.Message}", $"Overlay no disponible: {ex.Message}"));
                ready.Set();
            }
            finally
            {
                _hwnd = IntPtr.Zero;
                HotkeyRegistered = LockHotkeyRegistered = false;
                _thread = null;
            }
        })
        {
            IsBackground = true,
            Name = "Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Replaces what is shown. The window resizes itself to fit.</summary>
    public static void Update(OverlayView view)
    {
        _view = view;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Saves the overlay as it is drawn to a PNG file (works off-screen). Used by --overlay-preview.</summary>
    public static bool Snapshot(string path)
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r))
        {
            return false;
        }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        var ok = IsHud
            ? SendMessageW(_hwnd, WM_PRINTCLIENT, mem, IntPtr.Zero) != IntPtr.Zero
            : PrintWindow(_hwnd, mem, 1 /* PW_CLIENTONLY */);
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bgra = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, bgra, ref bi, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        Png.Write(path, w, h, bgra);
        return ok;
    }

    public static void ToggleLock()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_LOCK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Toggle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void SnapToMinimap()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_SNAP, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Run(int? x, int? y, ManualResetEventSlim ready)
    {
        try { SetProcessDPIAware(); } catch { /* old Windows */ }

        var screenDc = GetDC(IntPtr.Zero);
        _dpiScale = GetDeviceCaps(screenDc, LOGPIXELSY) / 96f;
        ReleaseDC(IntPtr.Zero, screenDc);

        int S(int v) => (int)Math.Round(v * _scale);

        _stableW = 0;
        CreateFonts();
        _brushBg = CreateSolidBrush(Rgb(18, 18, 22));
        _brushBorder = CreateSolidBrush(Rgb(58, 58, 66));
        _brushLine = CreateSolidBrush(Rgb(44, 44, 52));

        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_SIZEALL),
            hbrBackground = _brushBg,
            lpszClassName = "ChestCounterOverlay",
        };
        // Already registered when the overlay is turned off and on again in the same run (ERROR_CLASS_ALREADY_EXISTS).
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException("RegisterClassEx failed");
        }

        int w = IsHud ? 1 : S(MinWidth), h = S(90);
        var posX = x ?? S(24);
        var posY = y ?? S(24);

        _hwnd = CreateWindowExW(
            // Opaque (no WS_EX_LAYERED alpha): nothing for the compositor to blend every frame.
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, L.T("Coffres", "Chests", "Cofres"), WS_POPUP,

            posX, posY, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }

        _usesUniformAlpha = false;
        ApplyLock(_hwnd, _locked, notify: false);
        Relayout(_hwnd);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        // No timer at all: the thread sleeps in GetMessage and only wakes up when the counter changes,
        // when you drag/hide the window or press the shortcut.
        // Global shortcut, works even when the game has the focus.
        HotkeyRegistered = RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
        LockHotkeyRegistered = RegisterHotKey(_hwnd, 2, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_L);
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>(Re)creates the fonts for the current display scaling and chosen size.</summary>
    private static void CreateFonts()
    {
        foreach (var old in new[] { _fontBig, _fontSmall, _fontHeader, _fontCaption, _fontLabel, _fontIcon, _fontHud })
        {
            if (old != IntPtr.Zero) DeleteObject(old);
        }
        _scale = _dpiScale * _sizePercent / 100f;
        // The HUD follows the viewport height, independent of Windows text scaling.
        _hudScale = Math.Max(0.6f, GetSystemMetrics(SM_CYSCREEN) / 1152f) * _sizePercent / 100f;
        int S(int v) => (int)Math.Round(v * _scale);
        _fontBig = CreateFontW(-S(38), 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontSmall = CreateFontW(-S(14), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontHeader = CreateFontW(-S(12), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontCaption = CreateFontW(-S(11), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontLabel = CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontIcon = CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
        var hudFont = Environment.GetEnvironmentVariable("CHESTCOUNTER_HUD_FONT")?.Trim();
        if (!string.IsNullOrEmpty(hudFont) && hudFont.Length < 32)
        {
            var candidate = CreateFontW(-Sc(16), 0, 0, 0, 900, 0, 0, 0, 1, 0, 0, 4, 0, hudFont);
            if (candidate != IntPtr.Zero)
            {
                var dc = GetDC(IntPtr.Zero);
                var previous = SelectObject(dc, candidate);
                var selectedFace = new System.Text.StringBuilder(64);
                GetTextFaceW(dc, selectedFace.Capacity, selectedFace);
                SelectObject(dc, previous);
                ReleaseDC(IntPtr.Zero, dc);
                if (string.Equals(hudFont, selectedFace.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _fontHud = candidate;
                    return;
                }
                DeleteObject(candidate);
            }
        }

        // These HUD numerals are upright, wide and heavy at small sizes.
        _fontHud = CreateFontW(-Sc(16), 0, 0, 0, 900, 0, 0, 0, 1, 0, 0, 4, 0, "Arial Black");
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // Lock button = normal client area (clickable); everywhere else = caption (drag to move).
                // When locked the window is WS_EX_TRANSPARENT, so it never even receives this message.
                GetWindowRect(hwnd, out var wr);
                var px = (short)((long)lParam & 0xFFFF) - wr.Left;
                var py = (short)(((long)lParam >> 16) & 0xFFFF) - wr.Top;
                var onButton = px >= _lockRect.Left && px < _lockRect.Right && py >= _lockRect.Top && py < _lockRect.Bottom;
                return (IntPtr)(onButton ? HTCLIENT : HTCAPTION);
            }

            case WM_SETCURSOR:
                if (((long)lParam & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                    return (IntPtr)1;
                }
                break;

            case WM_LBUTTONUP:
                ApplyLock(hwnd, true, notify: true);
                return IntPtr.Zero;

            case WM_APP_LOCK:
                ApplyLock(hwnd, !_locked, notify: true);
                return IntPtr.Zero;

            case WM_NCRBUTTONUP:
                SetVisible(hwnd, false);
                return IntPtr.Zero;

            case WM_HOTKEY:
                if ((long)wParam == 2)
                {
                    ApplyLock(hwnd, !_locked, notify: true);
                }
                else
                {
                    SetVisible(hwnd, !IsWindowVisible(hwnd));
                }
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                if (GetWindowRect(hwnd, out var r))
                {
                    if (IsHud)
                    {
                        _autoPosition = false;
                    }
                    _onMoved?.Invoke(r.Left, r.Top);
                }
                return IntPtr.Zero;

            case WM_APP_STOP:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_APP_TOGGLE:
                SetVisible(hwnd, !IsWindowVisible(hwnd));
                return IntPtr.Zero;

            case WM_APP_RELAYOUT:
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_APP_SNAP:
                _autoPosition = true;
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
            case WM_APP_APPEARANCE:
                CreateFonts();
                _stableW = 0; // the width may shrink when the overlay gets smaller
                ApplyLock(hwnd, _locked, notify: false); // also applies the opacity
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;

            case WM_PRINTCLIENT: // snapshot (--overlay-preview): draw into the given DC
                if (IsHud)
                {
                    return RenderHud(hwnd, wParam) ? (IntPtr)1 : IntPtr.Zero;
                }
                PaintTo(hwnd, wParam);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Locked: click-through (WS_EX_LAYERED + WS_EX_TRANSPARENT). Unlocked: normal window. Layered also when the
    /// player made it see-through; a fully opaque unlocked overlay stays a plain window (nothing to blend).
    /// </summary>
    private static void ApplyLock(IntPtr hwnd, bool locked, bool notify)
    {
        _locked = locked;
        var alpha = Alpha;
        var layered = locked || alpha < 255 || IsHud;
        var ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if (IsHud && _usesUniformAlpha)
        {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~(long)WS_EX_LAYERED));
        }
        ex = layered ? ex | WS_EX_LAYERED : ex & ~(long)WS_EX_LAYERED;
        ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        _usesUniformAlpha = layered && !IsHud;
        if (_usesUniformAlpha)
        {
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (IsHud && notify)
        {
            RenderHud(hwnd);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);

        if (notify)
        {
            _onLockChanged?.Invoke(locked);
            Console.WriteLine(locked
                ? L.T($"Overlay verrouillé : la souris passe au travers. {(LockHotkeyRegistered ? "Ctrl+Maj+L" : "« Déverrouiller l'overlay » dans le compteur")} pour le déverrouiller.",
                      $"Overlay locked: the mouse goes through it. {(LockHotkeyRegistered ? "Ctrl+Shift+L" : "\"Unlock the overlay\" in the counter")} to unlock it.",
                      $"Overlay bloqueado: el ratón lo atraviesa. {(LockHotkeyRegistered ? "Ctrl+Mayús+L" : "«Desbloquear el overlay» en el contador")} para desbloquearlo.")
                : L.T("Overlay déverrouillé : clic gauche maintenu pour le déplacer, cadenas pour le reverrouiller.",
                      "Overlay unlocked: hold the left mouse button to move it, click the padlock to lock it again.",
                      "Overlay desbloqueado: arrástralo con el clic izquierdo; el candado lo vuelve a bloquear."));
        }
    }

    private static void SetVisible(IntPtr hwnd, bool visible)
    {
        if (visible)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else
        {
            ShowWindow(hwnd, SW_HIDE);
            Console.WriteLine(HotkeyRegistered
                ? L.T("Overlay masqué : Ctrl+Maj+O (n'importe où) pour le réafficher.", "Overlay hidden: Ctrl+Shift+O (anywhere) to show it again.", "Overlay oculto: Ctrl+Mayús+O (desde cualquier sitio) para volver a mostrarlo.")
                : L.T("Overlay masqué : « Afficher l'overlay » dans le compteur pour le réafficher.", "Overlay hidden: \"Show the overlay\" in the counter to show it again.", "Overlay oculto: «Mostrar el overlay» en el contador para volver a mostrarlo."));
        }
    }

    private static int TextWidth(IntPtr hdc, IntPtr font, string text, out int height)
    {
        SelectObject(hdc, font);
        var r = new RECT();
        DrawTextW(hdc, text.Length == 0 ? " " : text, -1, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        height = r.Bottom - r.Top;
        return r.Right - r.Left;
    }

    /// <summary>Measures the text and resizes the window (and its rounded region) to fit it.</summary>
    private static void Relayout(IntPtr hwnd)
    {
        if (IsHud)
        {
            RelayoutStyled(hwnd);
            return;
        }
        int S(int v) => (int)Math.Round(v * _scale);
        var v = _view;
        var hdc = GetDC(hwnd);
        try
        {
            // Header: "COFFRES" ... "● EN DIRECT" [lock]
            var headerW = TextWidth(hdc, _fontHeader, Header, out _headerH) + S(16) + (v.Badge != null ? TextWidth(hdc, _fontHeader, v.Badge, out _) : 0) + S(30);

            // Two counters side by side.
            _col1W = Math.Max(TextWidth(hdc, _fontBig, v.Total, out _bigH), TextWidth(hdc, _fontCaption, CaptionTotal, out _captionH));
            _col2W = Math.Max(TextWidth(hdc, _fontBig, v.Current ?? "—", out _), Math.Max(TextWidth(hdc, _fontCaption, v.CurrentCaption, out _), TextWidth(hdc, _fontCaption, OverlayModel.LastMatchCaption, out _)));
            var countersW = _col1W + S(ColGap) + _col2W;

            // Detail rows: grey label column + value.
            _labelW = 0;
            var valueW = 0;
            _rowH = 0;
            foreach (var row in v.Rows)
            {
                _labelW = Math.Max(_labelW, TextWidth(hdc, _fontLabel, row.Label, out var lh));
                valueW = Math.Max(valueW, TextWidth(hdc, _fontSmall, row.Value, out var vh)
                    + (row.Detail != null ? S(6) + TextWidth(hdc, _fontCaption, row.Detail, out _) : 0));
                _rowH = Math.Max(_rowH, Math.Max(lh, vh));
            }
            var rowsW = v.Rows.Count > 0 ? _labelW + S(12) + valueW : 0;

            // Never shrinks: the overlay keeps the same size between a match in progress and the end of the match.
            var w = Math.Max(_stableW, Math.Max(S(MinWidth), Math.Max(headerW, Math.Max(countersW, rowsW)) + 2 * S(Pad)));
            _stableW = w;
            var h = S(12) + _headerH + S(6) + _bigH + _captionH + S(12);
            if (v.Rows.Count > 0)
            {
                h += 1 + S(10) + v.Rows.Count * (_rowH + S(4)) + S(4);
            }
            _lockRect = new RECT { Left = w - S(Pad) - S(18), Top = S(6), Right = w - S(6), Bottom = S(12) + _headerH + S(6) };
            // Keep it fully on its screen: on the right half of the screen it grows to the left, and it is
            // pushed back inside when it would overflow (a wider overlay used to be cut by the screen edge).
            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // anchored on the right edge
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(18), S(18)), true); // window owns the region
            if (!_autoPosition && (nx != cur.Left || ny != cur.Top))
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd); // paint now (one repaint per update, no timer)
    }

    private static void Text(IntPtr hdc, IntPtr font, uint color, string text, int left, int top, int right, int bottom, uint align = DT_LEFT)
    {
        SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        DrawTextW(hdc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX | align);
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        try
        {
            if (!IsHud) PaintTo(hwnd, hdc);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void PaintTo(IntPtr hwnd, IntPtr hdc)
    {
        var v = _view;
        {
            GetClientRect(hwnd, out var rc);
            FillRect(hdc, ref rc, _brushBg);
            SetBkMode(hdc, TRANSPARENT);

            int S(int n) => (int)Math.Round(n * _scale);
            var x = S(Pad);
            var right = rc.Right - S(Pad);
            var y = S(12);

            // Thin border following the rounded shape.
            var border = CreateRoundRectRgn(0, 0, rc.Right + 1, rc.Bottom + 1, S(18), S(18));
            FrameRgn(hdc, border, _brushBorder, 1, 1);
            DeleteObject(border);

            // Header
            Text(hdc, _fontHeader, Rgb(150, 150, 162), Header, x, y, right, y + _headerH);
            if (v.Badge != null)
            {
                Text(hdc, _fontHeader, Rgb(96, 214, 128), v.Badge, x, y, _lockRect.Left - S(6), y + _headerH, DT_RIGHT);
            }
            // Lock button (Segoe MDL2 Assets: E785 = open padlock, E72E = closed padlock).
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, _locked ? Rgb(96, 96, 106) : Rgb(200, 200, 208));
            var lr = _lockRect;
            DrawTextW(hdc, _locked ? "" : "", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
            y += _headerH + S(6);

            // Counters: total | match in progress
            var x2 = x + _col1W + S(ColGap);
            Text(hdc, _fontBig, Rgb(242, 184, 75), v.Total, x - S(1), y, x + _col1W + S(4), y + _bigH);
            Text(hdc, _fontBig, v.Current == null ? Rgb(80, 80, 92) : v.CurrentLive ? Rgb(110, 224, 150) : Rgb(226, 226, 232), v.Current ?? "—", x2 - S(1), y, right + S(4), y + _bigH);
            var sep = new RECT { Left = x + _col1W + S(ColGap) / 2, Top = y + S(8), Right = x + _col1W + S(ColGap) / 2 + 1, Bottom = y + _bigH + _captionH };
            FillRect(hdc, ref sep, _brushLine);
            y += _bigH;
            Text(hdc, _fontCaption, Rgb(130, 130, 142), CaptionTotal, x, y, x + _col1W + S(4), y + _captionH);
            Text(hdc, _fontCaption, Rgb(130, 130, 142), v.CurrentCaption, x2, y, right + S(4), y + _captionH);
            y += _captionH + S(12);

            if (v.Rows.Count > 0)
            {
                var line = new RECT { Left = x, Top = y, Right = right, Bottom = y + 1 };
                FillRect(hdc, ref line, _brushLine);
                y += 1 + S(10);
                foreach (var row in v.Rows)
                {
                    Text(hdc, _fontLabel, Rgb(130, 130, 142), row.Label, x, y, x + _labelW, y + _rowH);
                    var vx = x + _labelW + S(12);
                    Text(hdc, _fontSmall, row.Treasure ? Rgb(196, 160, 255) : Rgb(226, 226, 232), row.Value, vx, y, right + S(4), y + _rowH);
                    if (row.Detail != null)
                    {
                        var dx = vx + TextWidth(hdc, _fontSmall, row.Value, out _) + S(6);
                        Text(hdc, _fontCaption, Rgb(130, 130, 142), row.Detail, dx, y, right + S(4), y + _rowH - S(2), DT_BOTTOM);
                    }
                    y += _rowH + S(4);
                }
            }
        }
    }


    // ---- "hud" style: figures only, outlined like the game's own HUD, right-aligned under the minimap -------------
    //
    //   [chest] 3 934        total (gold while a match is counted live)
    //   Dernière • 14        last match / "● En partie • …" / "● En direct • 12"
    //    Session • 83
    //      [star] 36         treasures

    private enum HudIcon { Chest, Treasure, Session, MatchHistory }

    private readonly record struct HudStat(string Value, HudIcon Icon);

    private static HudStat[] HudStats(OverlayView v) =>
    [
        new(v.Total, HudIcon.Chest),
        new(v.Treasures, HudIcon.Treasure),
        new(v.Session, HudIcon.Session),
        new((v.CurrentLive ? v.Current : v.Last) is { } count && count != "…" ? count : "–", HudIcon.MatchHistory),
    ];

    private static int Sc(double value) => Math.Max(1, (int)Math.Round(value * _hudScale));

    private static void RelayoutStyled(IntPtr hwnd)
    {
        var hdc = GetDC(hwnd);
        try
        {
            var textW = HudStats(_view).Max(stat => TextWidth(hdc, _fontHud, stat.Value, out _));
            var cellW = Math.Max(Sc(75), textW + Sc(35));
            var gap = Sc(15);
            var lockW = Sc(23);
            var w = Math.Max(_stableW, lockW + 2 * cellW + gap);
            _stableW = w;
            _hudCellW = (w - lockW - gap) / 2;
            _hudRowH = Sc(27);
            var h = 2 * _hudRowH;
            _lockRect = new RECT { Left = 0, Top = 0, Right = lockW, Bottom = _hudRowH };

            GetWindowRect(hwnd, out var cur);
            var nx = cur.Left;
            var ny = cur.Top;
            if (!_offscreen && _autoPosition)
            {
                // Reference screenshot: map right at 2021, bottom at 212 on a 2048 × 1152 viewport.
                // Leave an 8 px gap at that height. The primary monitor origin is (0, 0).
                var height = GetSystemMetrics(SM_CYSCREEN);
                nx = GetSystemMetrics(SM_CXSCREEN) - (int)Math.Round(height * (27.0 / 1152)) - w;
                ny = (int)Math.Round(height * (220.0 / 1152));
            }
            else if (!_offscreen)
            {
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                {
                    var bounds = mi.rcMonitor;
                    if (cur.Right - cur.Left > 1 && cur.Left + (cur.Right - cur.Left) / 2 > bounds.Left + (bounds.Right - bounds.Left) / 2)
                    {
                        nx = cur.Right - w;
                    }
                    nx = Math.Max(bounds.Left, Math.Min(nx, bounds.Right - w));
                    ny = Math.Max(bounds.Top, Math.Min(ny, bounds.Bottom - h));
                }
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, IntPtr.Zero, true);
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        RenderHud(hwnd);
    }

    private static bool RenderHud(IntPtr hwnd, IntPtr previewDc = default)
    {
        GetClientRect(hwnd, out var rc);
        var size = new SIZE { Width = rc.Right, Height = rc.Bottom };
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        var dc = CreateCompatibleDC(IntPtr.Zero);
        var header = new BITMAPINFOHEADER
        {
            biSize = 40, biWidth = size.Width, biHeight = -size.Height, biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(dc);
            return false;
        }

        var previousBitmap = SelectObject(dc, bitmap);
        try
        {
            // Draw the white foreground on black to get an antialiased coverage mask.
            FillRect(dc, ref rc, GetStockObject(BLACK_BRUSH));
            SetBkMode(dc, TRANSPARENT);
            var white = Rgb(255, 255, 255);
            var locked = _locked;
            if (!locked)
            {
                Text(dc, _fontIcon, white, "\uE785", 0, 0,
                    _lockRect.Right, _lockRect.Bottom, DT_CENTER | DT_VCENTER);
            }

            var badge = Sc(25);
            var inset = (_hudRowH - badge) / 2;
            var rightBadge = size.Width - badge;
            var leftBadge = rightBadge - _hudCellW - Sc(15);
            var stats = HudStats(_view);
            var numberLift = Sc(2);
            for (var i = 0; i < stats.Length; i++)
            {
                var x = i % 2 == 0 ? leftBadge : rightBadge;
                var y = i / 2 * _hudRowH;
                Text(dc, _fontHud, white, stats[i].Value, x - _hudCellW, y - numberLift,
                    x - Sc(5.5), y + _hudRowH - numberLift, DT_RIGHT | DT_VCENTER | DT_NOCLIP);
            }

            GdiFlush();
            var coverage = new int[size.Width * size.Height];
            Marshal.Copy(bits, coverage, 0, coverage.Length);
            if (!PaintHudIcons(stats, coverage, size.Width, badge, leftBadge, rightBadge, inset))
            {
                return false;
            }
            var pixels = new int[coverage.Length];
            for (var y = 0; y < size.Height; y++)
            {
                var withinBadgeRow = (uint)(y % _hudRowH - inset) < (uint)badge;
                for (var x = 0; x < size.Width; x++)
                {
                    var index = y * size.Width + x;
                    var glyph = coverage[index] & 255;
                    var withinBadge = withinBadgeRow && ((uint)(x - leftBadge) < (uint)badge || x >= rightBadge);
                    var backAlpha = !locked ? 200 : withinBadge ? 88 : 0;
                    if (locked && !withinBadge && x > 0 && x < size.Width - 1 && y > 1)
                    {
                        // A one-pixel drop shadow with a soft 3x3 falloff behind the numerals.
                        var shadow = index - size.Width;
                        var nearby = (coverage[shadow - 1] & 255) + 2 * (coverage[shadow] & 255)
                            + (coverage[shadow + 1] & 255);
                        var upper = (coverage[shadow - size.Width - 1] & 255)
                            + 2 * (coverage[shadow - size.Width] & 255) + (coverage[shadow - size.Width + 1] & 255);
                        var lower = (coverage[index - 1] & 255) + 2 * glyph + (coverage[index + 1] & 255);
                        backAlpha = (upper + 2 * nearby + lower) / 30;
                    }
                    // White over neutral black, stored as premultiplied BGRA.
                    var alpha = glyph + backAlpha * (255 - glyph) / 255;
                    pixels[index] = alpha << 24 | glyph << 16 | glyph << 8 | glyph;
                }
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var blend = new BLENDFUNCTION { SourceConstantAlpha = Alpha, AlphaFormat = 1 };
            if (previewDc != IntPtr.Zero)
            {
                var previewBg = CreateSolidBrush(Rgb(64, 87, 67));
                FillRect(previewDc, ref rc, previewBg);
                DeleteObject(previewBg);
                return AlphaBlend(previewDc, 0, 0, size.Width, size.Height, dc, 0, 0, size.Width, size.Height, blend);
            }

            var origin = new POINT();
            return UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref origin, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(dc, previousBitmap);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static bool PaintHudIcons(HudStat[] stats, int[] coverage, int width, int badge,
        int leftBadge, int rightBadge, int inset)
    {
        // GDI polygons and ellipses use hard edges. Draw the four glyphs in a single
        // oversampled strip, then average each block into the window's coverage mask.
        const int samples = 4;
        var tile = badge * samples;
        var stripWidth = tile * stats.Length;
        var strip = new RECT { Right = stripWidth, Bottom = tile };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var header = new BITMAPINFOHEADER
        {
            biSize = 40, biWidth = stripWidth, biHeight = -tile, biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(dc);
            return false;
        }

        var previousBitmap = SelectObject(dc, bitmap);
        try
        {
            FillRect(dc, ref strip, GetStockObject(BLACK_BRUSH));
            for (var i = 0; i < stats.Length; i++)
            {
                PaintHudIcon(dc, stats[i].Icon, i * tile, tile);
            }
            GdiFlush();

            var mask = new int[stripWidth * tile];
            Marshal.Copy(bits, mask, 0, mask.Length);
            for (var i = 0; i < stats.Length; i++)
            {
                var badgeX = i % 2 == 0 ? leftBadge : rightBadge;
                var badgeY = i / 2 * _hudRowH + inset;
                for (var y = 0; y < badge; y++)
                {
                    for (var x = 0; x < badge; x++)
                    {
                        var start = y * samples * stripWidth + i * tile + x * samples;
                        var sum = 0;
                        for (var dy = 0; dy < samples; dy++)
                        {
                            for (var dx = 0; dx < samples; dx++)
                            {
                                sum += mask[start + dy * stripWidth + dx] & 255;
                            }
                        }
                        coverage[(badgeY + y) * width + badgeX + x] = sum / (samples * samples);
                    }
                }
            }
            return true;
        }
        finally
        {
            SelectObject(dc, previousBitmap);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    // Minimap-style counter silhouettes on a 25-unit grid.
    private static void PaintHudIcon(IntPtr hdc, HudIcon icon, int x, int tile)
    {
        var white = GetStockObject(WHITE_BRUSH);
        var black = GetStockObject(BLACK_BRUSH);
        var oldBrush = SelectObject(hdc, white);
        var oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
        // Center the glyph within the badge while leaving the same inset on each edge.
        int Y(double v) => (int)Math.Round((12.5 + (v - 12.5) * 0.875) * tile / 25);
        int X(double v) => x + Y(v);

        void Shape(IntPtr brush, params (double X, double Y)[] points)
        {
            var pixels = points.Select(p => new POINT { X = X(p.X), Y = Y(p.Y) }).ToArray();
            SelectObject(hdc, brush);
            Polygon(hdc, pixels, pixels.Length);
        }

        void Oval(IntPtr brush, double l, double t, double r, double b)
        {
            SelectObject(hdc, brush);
            Ellipse(hdc, X(l), Y(t), X(r), Y(b));
        }

        switch (icon)
        {
            case HudIcon.Chest:
                Shape(white, (3, 10), (5, 6), (8, 4.5), (17, 4.5), (20, 6), (22, 10), (22, 18),
                    (20.5, 20.5), (4.5, 20.5), (3, 18));
                Shape(black, (3, 10.5), (22, 10.5), (22, 12), (3, 12));
                Shape(white, (10, 9.5), (15, 9.5), (15, 14.5), (10, 14.5));
                Shape(black, (11.7, 11.2), (13.3, 11.2), (13.3, 12.8), (11.7, 12.8));
                break;

            case HudIcon.Treasure:
                Shape(white, (7, 4.5), (18, 4.5), (22, 9), (12.5, 21), (3, 9));
                Shape(black, (5, 8.6), (20, 8.6), (19, 10), (15, 10), (12.5, 17.5),
                    (10, 10), (6, 10));
                break;

            case HudIcon.Session:
                Shape(white, (4, 5), (6.4, 5), (6.4, 20), (4, 20));
                Shape(white, (8.7, 5), (11.1, 5), (11.1, 20), (8.7, 20));
                Shape(white, (13.4, 5), (15.8, 5), (15.8, 20), (13.4, 20));
                Shape(white, (18.1, 5), (20.5, 5), (20.5, 20), (18.1, 20));
                Shape(white, (2.5, 15.5), (21.4, 7.2), (22.5, 9.5), (3.6, 17.8));
                break;

            case HudIcon.MatchHistory:
                Oval(white, 3.5, 3.5, 22, 22);
                Oval(black, 5.6, 5.6, 19.9, 19.9);
                Shape(black, (2.5, 3), (10, 3), (10, 8), (2.5, 10));
                Shape(white, (2.5, 4.5), (4.6, 4.5), (4.6, 8), (8.1, 8), (8.1, 10.1), (2.5, 10.1));
                Shape(white, (11.7, 7.7), (13.6, 7.7), (13.6, 12), (17, 13.9),
                    (16, 15.6), (11.7, 13.1));
                break;
        }
        SelectObject(hdc, oldPen);
        SelectObject(hdc, oldBrush);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, NULL_PEN = 8, WHITE_BRUSH = 0, BLACK_BRUSH = 4;
    private const uint WM_DISPLAYCHANGE = 0x007E, WM_APP_SNAP = 0x8006;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20, HTCLIENT = 1;
    private const uint WM_SETCURSOR = 0x0020, WM_LBUTTONUP = 0x0202, WM_APP_LOCK = 0x8003, VK_L = 0x4C, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr IDC_HAND = new(32649);
    private const uint LWA_ALPHA = 0x2, ULW_ALPHA = 0x2;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_NCHITTEST = 0x0084, WM_NCRBUTTONUP = 0x00A5,
        WM_EXITSIZEMOVE = 0x0232, WM_APP_TOGGLE = 0x8001, WM_PRINTCLIENT = 0x0318, WM_APP_RELAYOUT = 0x8002, WM_HOTKEY = 0x0312, WM_APP_APPEARANCE = 0x8004, WM_APP_STOP = 0x8005;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F;
    private const int HTCAPTION = 2;
    private const int TRANSPARENT = 1, LOGPIXELSY = 90;
    private const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_BOTTOM = 0x8, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_NOCLIP = 0x100;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr IDC_SIZEALL = new(32646);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, IntPtr position, ref SIZE size,
        IntPtr src, ref POINT origin, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);
    [DllImport("msimg32.dll")]
    private static extern bool AlphaBlend(IntPtr dst, int dx, int dy, int dw, int dh,
        IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION blend);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool FrameRgn(IntPtr hdc, IntPtr rgn, IntPtr brush, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr hdc, int count, System.Text.StringBuilder face);
    [DllImport("gdi32.dll")] private static extern bool Polygon(IntPtr hdc, POINT[] points, int count);
}
