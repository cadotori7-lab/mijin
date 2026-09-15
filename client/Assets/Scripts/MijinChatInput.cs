using System;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;

/// <summary>
/// 전역 단축키를 누르면 입력창이 열리고, 적어 보내면 미진이가 대답한다.
///
/// 미진이 창은 평소 포커스를 갖고 있지 않으므로 유니티의 Input으로는 단축키를
/// 받을 수 없다. GetAsyncKeyState로 직접 키 상태를 읽고, 눌리면 창을 앞으로
/// 가져와 키보드 입력을 받을 수 있게 만든다.
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinChatInput : MonoBehaviour
{
    [Header("연결")]
    public MijinTalk talk;
    public MijinScreenCapture capture;
    [Tooltip("입력창 전체를 담은 오브젝트. 평소에는 꺼져 있다.")]
    public GameObject panel;
    public TMP_InputField inputField;

    [Header("단축키")]
    public bool useCtrl = true;
    public bool useAlt = true;
    public bool useShift = false;
    [Tooltip("가상 키 코드. 0x4D=M, 0x20=스페이스, 0xC0=` (물결표 키)")]
    public int hotkeyVirtualKey = 0x42; // B 10진법으로는 66

    [Header("동작")]
    [Tooltip("창이 포커스를 잃으면 입력창을 닫는다.")]
    public bool closeOnFocusLost = true;

    [Header("위치")]
    [Tooltip("미진이를 따라다닌다. 끄면 UI에 지정한 자리에 고정된다.")]
    public bool followCharacter = true;
    public RectTransform panelRect;
    [Tooltip("캐릭터 발밑 기준 오프셋 (화면 픽셀)")]
    public Vector2 screenOffset = new Vector2(0f, 20f);

    private bool _prevPressed;

    private void Start()
    {
        if (panel != null) panel.SetActive(false);
    }

    private void Update()
    {
        if (followCharacter && panel != null && panel.activeSelf) FollowCharacter();

        bool pressed = HotkeyDown();
        if (pressed && !_prevPressed)   // 눌린 순간에만 반응 (누르고 있는 동안 반복 방지)
        {
            if (panel != null && panel.activeSelf) Close();
            else Open();
        }
        _prevPressed = pressed;

        if (panel == null || !panel.activeSelf) return;

        if (Input.GetKeyDown(KeyCode.Escape)) Close();
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Send();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused && closeOnFocusLost && panel != null && panel.activeSelf)
            Close();
    }

    // ───────────────── 열고 닫기 ─────────────────

    public void Open()
    {
        if (panel == null || inputField == null) return;

        // 키보드 입력을 받으려면 미진이 창이 맨 앞에 와야 한다
        if (capture != null) capture.FocusSelf();

        panel.SetActive(true);
        inputField.text = "";
        inputField.ActivateInputField();

        if (followCharacter) FollowCharacter();
        if (talk != null && talk.mijin != null) talk.mijin.SetHeld(true);
    }

    public void Close()
    {
        if (panel == null) return;
        inputField.DeactivateInputField();
        panel.SetActive(false);

        if (talk != null && talk.mijin != null) talk.mijin.SetHeld(false);
    }

    /// <summary>패널을 미진이 발밑 위쪽, 화면 안에 들어오게 옮긴다.</summary>
    private void FollowCharacter()
    {
        if (panelRect == null || talk == null || talk.mijin == null) return;

        Camera cam = Camera.main;
        Vector3 p = cam.WorldToScreenPoint(talk.mijin.transform.position);
        p.x += screenOffset.x;
        p.y += screenOffset.y;

        Vector2 size = panelRect.sizeDelta;
        p.x = Mathf.Clamp(p.x, size.x * 0.5f, Screen.width - size.x * 0.5f);
        p.y = Mathf.Clamp(p.y, size.y * 0.5f, Screen.height - size.y * 0.5f);
        panelRect.position = p;
    }

    /// <summary>적은 내용을 미진이에게 보낸다. 전송 버튼에 연결해도 된다.</summary>
    public void Send()
    {
        if (inputField == null || talk == null) return;

        string text = inputField.text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Close();
            return;
        }

        talk.Talk("chat", text);
        Close();
    }

    // ───────────────── 전역 단축키 ─────────────────
    private bool HotkeyDown()
    {
        if (!IsDown(hotkeyVirtualKey)) return false;
        if (useCtrl && !IsDown(VK_CONTROL)) return false;
        if (useAlt && !IsDown(VK_MENU)) return false;
        if (useShift && !IsDown(VK_SHIFT)) return false;
        return true;
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
