using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 파트너님이 미진이의 눈을 가린다. 켜져 있는 동안 화면을 전혀 캡처하지 않는다.
///
/// 캡처를 막는 것만으로 끝내지 않고 "가려졌다"는 사실은 미진이에게 알려 준다.
/// 그래야 미진이가 반응할 수 있고, 기능이 켜져 있다는 걸 파트너님도 계속 인지한다.
/// 가린 동안에는 화면 정보가 한 톨도 나가지 않는다 (알리는 건 문장 한 줄뿐).
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinBlind : MonoBehaviour
{
    [Header("연결")]
    public MijinTalk talk;
    public MijinCharacter mijin;

    [Header("단축키")]
    [Tooltip("메뉴를 열지 않고도 바로 가릴 수 있어야 한다 (비밀번호 입력 직전 등)")]
    public bool useHotkey = true;
    public bool useCtrl = true;
    public bool useAlt = true;
    [Tooltip("가상 키 코드. 0x42=B")]
    public int hotkeyVirtualKey = 0x42;

    [Header("반응")]
    [Tooltip("가릴 때 미진이에게 알릴 문장")]
    [TextArea] public string onBlindText = "(파트너님이 손으로 미진이의 눈을 가렸다. 아무것도 볼 수 없다.)";
    [Tooltip("풀 때 미진이에게 알릴 문장")]
    [TextArea] public string onRevealText = "(파트너님이 손을 치웠다. 다시 화면이 보인다.)";
    [Tooltip("끄면 조용히 가려지기만 한다.")]
    public bool reactWhenToggled = true;

    public bool IsBlind { get; private set; }

    /// <summary>상태가 바뀔 때 알린다. 메뉴 토글을 맞춰 두는 데 쓴다.</summary>
    public System.Action<bool> OnChanged;

    private bool _prevPressed;

    private void Update()
    {
        if (!useHotkey) return;

        bool pressed = HotkeyDown();
        if (pressed && !_prevPressed) Toggle();
        _prevPressed = pressed;
    }

    // ───────────────── 켜고 끄기 ─────────────────

    public void Toggle() => SetBlind(!IsBlind);

    public void SetBlind(bool blind, bool react = true)
    {
        if (IsBlind == blind) return;
        IsBlind = blind;

        // 눈을 감은 모습으로 보여 준다. 기능이 켜져 있다는 걸 한눈에 알 수 있다.
        if (mijin != null) mijin.SetEyesCovered(blind);

        OnChanged?.Invoke(blind);

        if (react && reactWhenToggled && talk != null)
            talk.Talk("event", blind ? onBlindText : onRevealText);
    }

    /// <summary>저장된 설정을 불러올 때. 시작하자마자 말을 걸지 않는다.</summary>
    public void RestoreSilently(bool blind)
    {
        if (IsBlind == blind) return;
        IsBlind = blind;
        if (mijin != null) mijin.SetEyesCovered(blind);
        OnChanged?.Invoke(blind);
    }

    // ───────────────── 전역 단축키 ─────────────────
    private bool HotkeyDown()
    {
        if (!IsDown(hotkeyVirtualKey)) return false;
        if (useCtrl && !IsDown(VK_CONTROL)) return false;
        if (useAlt && !IsDown(VK_MENU)) return false;
        return true;
    }

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
