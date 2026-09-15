using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// 윈도우 화면을 캡처해 JPEG base64로 만들고, 캡처 시점의 활성 창 제목도 같이 얻는다.
///
/// 미진이 자신이 찍히면 자기 말풍선을 읽고 또 반응하는 고리가 생긴다.
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)로 자기 창을 캡처 대상에서
/// 빼두면 사용자 눈에는 보이면서 캡처에는 안 잡힌다 (숨겼다 찍는 방식과 달리 깜빡임 없음).
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinScreenCapture : MonoBehaviour
{
    [Header("캡처")]
    [Tooltip("이 너비로 줄여서 캡처한다. 0이면 원본 해상도. OCR 정확도를 생각하면 0 권장.")]
    public int captureWidth = 0;
    [Range(1, 100)]
    [Tooltip("JPEG 품질. 낮추면 전송은 빨라지지만 작은 글자 인식이 떨어진다.")]
    public int jpegQuality = 85;

    [Header("자기 창 제외")]
    [Tooltip("끄면 미진이도 캡처에 찍힌다. 문제가 생겼을 때만 꺼서 확인용으로 쓴다.")]
    public bool excludeSelfFromCapture = true;

    private IntPtr _ownWindow = IntPtr.Zero;
    private uint _ownPid;
    private Texture2D _buffer;
     private const uint GW_HWNDNEXT = 2;
     private const uint GW_OWNER = 4;
     private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int valueSize);

    private void Start()
    {
        _ownPid = GetCurrentProcessId();
        _ownWindow = FindOwnWindow();

        var sb = new StringBuilder(512);
        GetWindowText(_ownWindow, sb, sb.Capacity);
        Debug.Log($"[Capture] 자기 창으로 인식한 핸들: {_ownWindow}, 제목: \"{sb}\"");

        if (excludeSelfFromCapture && _ownWindow != IntPtr.Zero)
        {
            bool ok = SetWindowDisplayAffinity(_ownWindow, WDA_EXCLUDEFROMCAPTURE);
            Debug.Log(ok
                ? "[Capture] 미진이 창을 캡처 대상에서 제외했습니다."
                : "[Capture] 캡처 제외 설정 실패 - 미진이가 화면에 같이 찍힐 수 있습니다.");
        }
    }
    /// <summary>미진이 창을 맨 앞으로 가져온다. 키보드 입력을 받으려면 필요하다.</summary>
    public void FocusSelf()
    {
        if (_ownWindow != IntPtr.Zero) SetForegroundWindow(_ownWindow);
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void OnDestroy()
    {
        if (_buffer != null) Destroy(_buffer);
    }

    // ───────────────── 외부에서 부르는 것들 ─────────────────

    /// <summary>지금 화면을 캡처해 JPEG base64로 돌려준다. 실패하면 null.</summary>
    [Header("가리기")]
    public MijinBlind blind;
    public string CaptureBase64()
    {
        // 가린 동안은 캡처 자체를 하지 않는다. 부르는 쪽마다 확인하면
        // 나중에 호출 지점이 늘 때 빠뜨리기 쉬우므로 여기서 막는다.
        if (blind != null && blind.IsBlind) return null;
        IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;

        try
        {
            int srcW = GetSystemMetrics(SM_CXSCREEN);
            int srcH = GetSystemMetrics(SM_CYSCREEN);
            if (srcW <= 0 || srcH <= 0) return null;

            int dstW = srcW, dstH = srcH;
            if (captureWidth > 0 && captureWidth < srcW)
            {
                dstW = captureWidth;
                dstH = Mathf.RoundToInt(srcH * (float)captureWidth / srcW);
            }

            screenDC = GetDC(IntPtr.Zero);
            memDC = CreateCompatibleDC(screenDC);
            bitmap = CreateCompatibleBitmap(screenDC, dstW, dstH);
            oldBitmap = SelectObject(memDC, bitmap);

            SetStretchBltMode(memDC, STRETCH_HALFTONE);
            bool copied = StretchBlt(memDC, 0, 0, dstW, dstH,
                                     screenDC, 0, 0, srcW, srcH,
                                     SRCCOPY | CAPTUREBLT);
            if (!copied) return null;

            // GetDIBits는 대상 비트맵이 DC에서 빠져 있어야 안전하다
            SelectObject(memDC, oldBitmap);
            oldBitmap = IntPtr.Zero;

            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.bmiHeader.biWidth = dstW;
            info.bmiHeader.biHeight = dstH;   // 양수 = 아래에서 위로 (Texture2D와 같은 순서)
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            byte[] pixels = new byte[dstW * dstH * 4];
            int lines = GetDIBits(memDC, bitmap, 0, (uint)dstH, pixels, ref info, DIB_RGB_COLORS);
            if (lines == 0) return null;

            // GDI는 BGRA 순서로 준다. Texture2D가 읽을 수 있게 B와 R을 맞바꾼다.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = b;
                pixels[i + 3] = 255;   // 알파는 항상 불투명으로
            }

            if (_buffer == null || _buffer.width != dstW || _buffer.height != dstH)
            {
                if (_buffer != null) Destroy(_buffer);
                _buffer = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
            }
            _buffer.LoadRawTextureData(pixels);
            _buffer.Apply(false);

            byte[] jpg = _buffer.EncodeToJPG(jpegQuality);
            return Convert.ToBase64String(jpg);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Capture] 캡처 실패: {e.Message}");
            return null;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
        }
    }
        /// <summary>마지막 키보드·마우스 입력 이후 지난 시간(초). 자리 비움 판단에 쓴다.</summary>
    public float IdleSeconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref info)) return 0f;
        return (GetTickCount() - info.dwTime) / 1000f;
    }

    /// <summary>맨 앞 창이 화면을 꽉 채우고 있는지. 게임이나 전영상 감상 중 판단에 쓴다.</summary>
    public bool IsForegroundFullscreen()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == _ownWindow) return false;
        if (!GetWindowRect(hwnd, out RECT r)) return false;

        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        return r.left <= 0 && r.top <= 0 && r.right >= sw && r.bottom >= sh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    /// <summary>
    /// 지금 맨 앞에 있는 창의 제목. 미진이 자신이 맨 앞이면 빈 문자열을 돌려준다.
    ///
    /// 이걸 캡처와 같은 시점에 읽어 프록시로 보내야 한다. 프록시가 나중에
    /// 따로 읽으면 그 사이에 창이 바뀌어, 차단해야 할 화면이 엉뚱한 이름표를 달고
    /// 필터를 통과할 수 있다.
    /// </summary>
        public string ActiveWindowTitle()
    {
        string result = "";

        // EnumWindows는 Z 순서(위에서 아래)로 훑는다. GW_HWNDNEXT 사슬을 직접 타면
        // 제목 없는 시스템 창이 수십 개씩 끼어 있어 중간에 포기하게 된다.
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return true;                       // 미진이 자신
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;  // 툴팁·IME 등
            if (IsCloaked(hwnd)) return true;

            var sb = new StringBuilder(512);
            if (GetWindowText(hwnd, sb, sb.Capacity) <= 0) return true;

            string title = sb.ToString().Trim();
            if (title.Length == 0 || title == "Program Manager") return true;

            result = title;
            return false;   // 첫 번째로 걸린 창이 지금 작업 중인 창이다
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// UWP 앱이나 다른 가상 데스크톱의 창은 IsWindowVisible이 true를 줘도
    /// 실제 화면엔 안 보이는(cloaked) 경우가 있다. 그대로 두면 엉뚱한 창 제목이
    /// "진짜 작업 중인 창"으로 잘못 뽑힌다.
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    // ───────────────── 자기 창 찾기 ─────────────────
    private IntPtr FindOwnWindow()
    {
        uint myPid = GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == myPid && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;   // 첫 번째 보이는 창이면 충분하다
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
            Debug.LogWarning("[Capture] 자기 창을 찾지 못했습니다.");
        return found;
    }

    // ───────────────── Win32 ─────────────────
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int STRETCH_HALFTONE = 4;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
                                          IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines,
                                        byte[] bits, ref BITMAPINFO info, uint usage);
}
