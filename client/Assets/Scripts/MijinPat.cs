using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 버튼을 누르지 않고 머리 위에서 좌우로 문지르면 쓰다듬는 것으로 본다.
///
/// 미진이 창은 평소 포커스가 없어서 Unity의 Input으로는 커서 위치가 안 잡힌다
/// (MijinChatInput이 단축키를 GetAsyncKeyState로 읽는 것과 같은 이유). GetCursorPos로
/// 직접 읽고, 버튼 상태도 GetAsyncKeyState로 확인한다.
///
/// 커서가 머리 위에 있다는 것만으로는 쓰다듬기로 보지 않는다 - 지나가는 것과
/// 구분이 안 된다. 좌우 방향 전환(획)을 세어, 왕복이 있어야만 쓰다듬기로 인정한다.
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinPat : MonoBehaviour
{
    [Header("연결")]
    public MijinCharacter mijin;
    public MijinTalk talk;

    [Header("판정")]
    [Tooltip("한 방향으로 이만큼은 움직여야 획으로 센다 (로컬 단위). 손떨림을 거른다.")]
    public float minStrokeDistance = 0.35f;
    [Tooltip("이만큼 왕복해야 쓰다듬기로 본다. 2면 좌우 한 번씩이다.")]
    public int strokesToStart = 2;
    [Tooltip("획들이 이 시간 안에 모여야 한 번의 쓰다듬기로 본다.")]
    public float strokeWindowSeconds = 1.2f;
    [Tooltip("마지막 획 뒤 이만큼 지나면 끝난 것으로 본다.")]
    public float patTimeoutSeconds = 0.5f;

    [Header("서버 이벤트")]
    [Tooltip("이 시간 안에는 다시 이벤트를 보내지 않는다. 쓰다듬기는 길게 이어지는 행동이라 " +
             "획마다 보내면 요금이 샌다.")]
    public float eventCooldownSeconds = 60f;
    [TextArea]
    public string onPatText = "(파트너님이 미진이의 머리를 쓰다듬는다.)";

    private Camera _cam;

    private float _lastLocalX;
    private int _strokeDir;        // 지금 잡고 있는 획의 방향 (+1/-1), 0 = 아직 없음
    private float _strokeAccum;    // 그 방향으로 누적된 이동 거리
    private int _strokes;
    private float _firstStrokeTime;
    private float _lastStrokeTime;
    private bool _isPatting;
    private float _lastEventTime = -999f;

    private void Awake()
    {
        _cam = Camera.main;
    }

    private void Update()
    {
        if (mijin == null) return;

        // 들려 있으면 커서가 후드 꼭대기에 붙어 계속 머리 영역 안에서 흔들리므로
        // 판정 자체를 하지 않는다. 버튼이 눌려 있으면 드래그·찌르기가 주인이다.
        if (mijin.IsHeld || IsMouseButtonDown())
        {
            CancelPat();
            SyncLastCursor();
            return;
        }

        Vector3 local = mijin.transform.InverseTransformPoint(CursorWorld());
        bool inHead = mijin.IsPointInHead(local);

        float dx = local.x - _lastLocalX;
        _lastLocalX = local.x;

        if (inHead)
        {
            TrackStroke(dx);
        }
        else
        {
            // 머리 밖으로 나가면 누적 거리만 초기화한다. strokes·시각은 유지해서
            // 잠깐 벗어났다 돌아와도 하나의 쓰다듬기로 이어진다.
            _strokeAccum = 0f;
            _strokeDir = 0;
        }

        if (_isPatting && Time.time - _lastStrokeTime > patTimeoutSeconds)
            EndPatting();
    }

    private void SyncLastCursor()
    {
        _lastLocalX = mijin.transform.InverseTransformPoint(CursorWorld()).x;
    }

    private void TrackStroke(float dx)
    {
        if (Mathf.Abs(dx) < 1e-5f) return;

        int dir = dx > 0f ? 1 : -1;
        if (_strokeDir == 0)
        {
            _strokeDir = dir;
            _strokeAccum = Mathf.Abs(dx);
            return;
        }

        if (dir == _strokeDir)
        {
            _strokeAccum += Mathf.Abs(dx);
            return;
        }

        // 방향이 바뀌었다 - 직전 방향으로 충분히 움직였으면 획 하나로 센다.
        // 못 미쳤으면(손떨림) 그냥 방향만 새로 잡고 넘어간다.
        if (_strokeAccum >= minStrokeDistance) RegisterStroke();
        _strokeDir = dir;
        _strokeAccum = Mathf.Abs(dx);
    }

    private void RegisterStroke()
    {
        float now = Time.time;
        if (_strokes == 0 || now - _firstStrokeTime > strokeWindowSeconds)
        {
            // 너무 오래전 획이면 새 시도로 본다
            _strokes = 0;
            _firstStrokeTime = now;
        }
        _strokes++;
        _lastStrokeTime = now;

        mijin.PlayPatStroke();   // 찌그러짐은 자세·상태와 무관하게 매 획마다 준다

        if (mijin.CanAct())
        {
            // 홀드를 매 획마다 갱신해서, 쓰다듬는 동안은 표정이 유지되다가
            // 손을 떼면 patTimeoutSeconds 뒤 알아서 풀린다.
            mijin.SetExpression(mijin.eyesExcited, mijin.mouthPat, patTimeoutSeconds + 0.3f);
        }

        if (!_isPatting && _strokes >= strokesToStart) StartPatting();
    }

    private void StartPatting()
    {
        _isPatting = true;
    }

    private void EndPatting()
    {
        _isPatting = false;
        _strokes = 0;
        if (!mijin.CanAct())
        {
            // 자는 중·들림·비행·착지 중에는 깨우거나 이벤트를 보내지 않는다
            // (찌그러짐은 RegisterStroke에서 이미 줬다)
            return;
        }

        mijin.ResetExpression();

        if (Time.time - _lastEventTime < eventCooldownSeconds) return;
        _lastEventTime = Time.time;
        if (talk != null) talk.Talk("event", onPatText);
    }

    private void CancelPat()
    {
        _strokeDir = 0;
        _strokeAccum = 0f;
        if (_isPatting) EndPatting();
    }

    // ───────────────── 커서 (포커스 없이 읽기) ─────────────────

    private Vector3 CursorWorld()
    {
        GetCursorPos(out POINT p);
        // Windows 좌표는 좌상단 원점이라 Unity(좌하단 원점)로 넘길 때 Y를 뒤집는다.
        // 창이 데스크톱 (0,0)에서 화면 전체를 덮고 있다는 전제다 - 디스플레이
        // 배율이 100%가 아니면 어긋날 수 있다 (설계서 §0에서 실측 확인함).
        Vector3 screen = new Vector3(p.x, Screen.height - p.y, 0f);
        Vector3 world = _cam.ScreenToWorldPoint(screen);
        world.z = 0f;
        return world;
    }

    private const int VK_LBUTTON = 0x01;
    private static bool IsMouseButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
