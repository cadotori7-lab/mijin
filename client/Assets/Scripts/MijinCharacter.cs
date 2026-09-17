using System.Collections;
using UnityEngine;

/// <summary>
/// 미진이의 표시와 스스로 하는 움직임(대기, 걷기, 깜빡임)을 담당한다.
/// 창 전체를 투명하게 깔아두고, 창을 옮기는 대신 이 오브젝트를 화면 안에서 움직인다.
///
/// 씬 구성:
///   Mijin (이 스크립트 + BoxCollider2D + MijinDrag)
///    ├ Body   SpriteRenderer  Order 0
///    ├ Eyes   SpriteRenderer  Order 1
///    └ Mouth  SpriteRenderer  Order 2
/// 모든 스프라이트는 Pivot Custom (0.5, 0.253) — 원점이 발밑에 온다.
/// </summary>
public class MijinCharacter : MonoBehaviour
{
    [Header("파츠")]
    public SpriteRenderer bodyRenderer;
    public SpriteRenderer eyesRenderer;
    public SpriteRenderer mouthRenderer;

    [Header("몸통 스프라이트")]
    public Sprite bodyIdle;
    public Sprite bodyWalk01;
    public Sprite bodyWalk02;

    [Header("눈 스프라이트")]
    public Sprite eyesOpen;
    public Sprite eyesClosed;
    public Sprite eyesBlind;

    [Header("입 스프라이트")]
    public Sprite mouthClosed;
    public Sprite mouthHalf;
    public Sprite mouthOpen;

    [Header("방향")]
    [Tooltip("그림이 오른쪽을 보고 있으면 체크. 미진이는 왼쪽을 보므로 해제해 둔다.")]
    public bool artFacesRight = false;

    [Header("걷기")]
    [Tooltip("초당 이동 거리 (월드 단위)")]
    public float walkSpeed = 0.6f;
     [Tooltip("끄면 제자리에 서 있는다.")]
    public bool walkEnabled = true;
    [Tooltip("걷기 프레임 하나당 유지 시간. 발이 미끄러져 보이면 이 값을 줄인다.")]
    public float framePerSecond = 0.15f;
    [Tooltip("작업표시줄 높이만큼 띄운다 (월드 단위, PPU 100 기준 0.48 = 48px)")]
    public float groundOffset = 0.7f;
    [Tooltip("화면 좌우 끝에서 남겨둘 여백")]
    public float edgeMargin = 0.5f;

    [Header("대기/걷기 전환")]
    public Vector2 idleDurationRange = new Vector2(3f, 8f);
    public Vector2 walkDurationRange = new Vector2(2f, 5f);

    [Header("깜빡임")]
    public Vector2 blinkIntervalRange = new Vector2(2.5f, 6f);
    public float blinkDuration = 0.12f;

    /// <summary>발이 닿는 높이 (월드 y). MijinDrag가 착지 판정에 쓴다.</summary>
    public float GroundY { get; private set; }

    /// <summary>카메라 기준 화면 절반 너비 (월드 단위).</summary>
    public float HalfWidth { get; private set; }

    /// <summary>지금 들려 있거나 날아가는 중인지. true면 스스로 움직이지 않는다.</summary>
    public bool IsHeld { get; private set; }

    // 걷기 프레임 순서. idle을 사이에 끼워 다리가 모이는 중간 단계를 만든다.
    private Sprite[] _walkCycle;
    private int _facing = 1;   // 1 = 오른쪽으로 이동, -1 = 왼쪽으로 이동

    private bool _isWalking;
    private bool _eyesCovered;

    private void Start()
    {
        _walkCycle = new[] { bodyWalk01, bodyIdle, bodyWalk02, bodyIdle };

        Camera cam = Camera.main;
        HalfWidth = cam.orthographicSize * cam.aspect;
        GroundY = -cam.orthographicSize + groundOffset;

        transform.position = new Vector3(0f, GroundY, 0f);
        bodyRenderer.sprite = bodyIdle;
        eyesRenderer.sprite = eyesOpen;
        mouthRenderer.sprite = mouthClosed;

        StartCoroutine(BlinkLoop());
        StartCoroutine(BehaviourLoop());
        StartCoroutine(WalkAnimationLoop());
    }

    // ───────────────── 눈 깜빡임 ─────────────────
    // 표정 시스템을 나중에 붙일 때는 eyesOpen 대신 현재 표정으로 되돌리면 된다.
    private IEnumerator BlinkLoop()
    {
        while (true)
        {
            while (_eyesCovered) yield return null;

            yield return new WaitForSeconds(
                Random.Range(blinkIntervalRange.x, blinkIntervalRange.y));

            // 대기하는 동안 눈이 가려졌을 수 있다 - 그러면 이번 깜빡임은 건너뛴다
            if (_eyesCovered) continue;

            eyesRenderer.sprite = eyesClosed;
            yield return new WaitForSeconds(blinkDuration);
            if (!_eyesCovered) eyesRenderer.sprite = eyesOpen;

            // 가끔 두 번 연속으로 깜빡이면 더 살아 있어 보인다
            if (Random.value < 0.25f && !_eyesCovered)
            {
                yield return new WaitForSeconds(0.12f);
                if (_eyesCovered) continue;
                eyesRenderer.sprite = eyesClosed;
                yield return new WaitForSeconds(blinkDuration);
                if (!_eyesCovered) eyesRenderer.sprite = eyesOpen;
            }
        }
    }

    // ───────────────── 대기 / 걷기 전환 ─────────────────
    private IEnumerator BehaviourLoop()
    {
        while (true)
        {
            _isWalking = false;
            if (!IsHeld) bodyRenderer.sprite = bodyIdle;

            yield return new WaitForSeconds(
                Random.Range(idleDurationRange.x, idleDurationRange.y));

            // 들려 있는 동안은 걷기를 시작하지 않고 기다린다
            while (IsHeld || !walkEnabled) yield return null;

            _facing = Random.value < 0.5f ? 1 : -1;
            ApplyFacing();

            _isWalking = true;
            yield return new WaitForSeconds(
                Random.Range(walkDurationRange.x, walkDurationRange.y));
        }
    }

    private IEnumerator WalkAnimationLoop()
    {
        int frame = 0;
        while (true)
        {
            if (_isWalking && !IsHeld)
            {
                bodyRenderer.sprite = _walkCycle[frame % _walkCycle.Length];
                frame++;
            }
            yield return new WaitForSeconds(framePerSecond);
        }
    }

    private void Update()
    {
        if (!_isWalking || IsHeld) return;
        
        Vector3 p = transform.position;
        p.x += _facing * walkSpeed * Time.deltaTime;

        // 화면 끝에 닿으면 방향을 바꾼다
        float limit = HalfWidth - edgeMargin;
        if (p.x > limit || p.x < -limit)
        {
            p.x = Mathf.Clamp(p.x, -limit, limit);
            _facing *= -1;
            ApplyFacing();
        }
        
        p.y = GroundY;
        transform.position = p;
    }

    private void ApplyFacing()
    {
        Vector3 s = transform.localScale;
        int flip = artFacesRight ? _facing : -_facing;
        s.x = Mathf.Abs(s.x) * flip;
        transform.localScale = s;
    }

    // ───────────────── 외부에서 부르는 것들 ─────────────────

    /// <summary>들려 있거나 날아가는 동안 스스로 걷지 않게 한다.</summary>
    public void SetHeld(bool held)
    {
        IsHeld = held;
        if (held)
        {
            _isWalking = false;
            bodyRenderer.sprite = bodyIdle;   // drag.png를 그리면 여기서 바꾼다
        }
    }

    /// <summary>이동 방향을 바깥에서 정한다 (던져진 방향을 보게 할 때).</summary>
    public void FaceDirection(int dir)
    {
        if (dir == 0) return;
        _facing = dir > 0 ? 1 : -1;
        ApplyFacing();
    }
    /// <summary>화면에 보이는 크기를 바꾼다. 바라보는 방향(부호)은 유지한다.</summary>
    public void SetScale(float scale)
    {
        Vector3 s = transform.localScale;
        s.x = scale * Mathf.Sign(s.x);
        s.y = scale;
        transform.localScale = s;
    }
    /// <summary>
    /// TTS 재생 중 음량(0~1)에 맞춰 입을 움직인다.
    /// 오디오 재생 쪽에서 매 프레임 호출하면 된다.
    /// </summary>
    public void SetMouthLevel(float level)
    {
        if (level < 0.15f)      mouthRenderer.sprite = mouthClosed;
        else if (level < 0.45f) mouthRenderer.sprite = mouthHalf;
        else                    mouthRenderer.sprite = mouthOpen;
    }

    /// <summary>말이 끝났을 때 입을 닫는다.</summary>
    public void CloseMouth() => mouthRenderer.sprite = mouthClosed;

    /// <summary>가리기 중에는 blind 스프라이트로 바꾼다 (깜빡임도 멈춘다).</summary>
        public void SetEyesCovered(bool covered)
    {
        _eyesCovered = covered;
        if (!covered)
            eyesRenderer.sprite = eyesOpen;
        else
            eyesRenderer.sprite = eyesBlind != null ? eyesBlind : eyesClosed;
    }
}
