using System.Collections;
using UnityEngine;
 
/// <summary>
/// 미진이의 표시와 스스로 하는 움직임(대기, 걷기, 한발서기, 깜빡임)을 담당한다.
/// 창 전체를 투명하게 깔아두고, 창을 옮기는 대신 이 오브젝트를 화면 안에서 움직인다.
///
/// 씬 구성:
///   Mijin (이 스크립트 + BoxCollider2D + MijinDrag)
///    ├ Body   SpriteRenderer  Order 0
///    ├ Eyes   SpriteRenderer  Order 1
///    └ Mouth  SpriteRenderer  Order 2
///
/// ★ 모든 스프라이트의 Pivot은 Custom (0.5, 0.253)으로 같아야 한다.
///   눈·입이 별도 렌더러라서 스프라이트마다 피벗이 다르면 얼굴이 어긋난다.
///   자세별로 발 높이가 다른 문제는 피벗이 아니라 ApplyPoseOffset(세 파츠를
///   함께 위아래로 옮김)으로 맞춘다.
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
 
    [Header("한발서기")]
    public Sprite bodyStand01;
    public Sprite bodyStand02;
    [Range(0f, 1f)] public float standChance = 0.25f;
    public Vector2 standDurationRange = new Vector2(2f, 4f);
    public float standWobbleSeconds = 0.25f;
 
    [Header("들림")]
    public Sprite bodyHold01;
    public Sprite bodyHold02;
    [Tooltip("후드 꼭대기(손이 잡는 지점). 피벗 기준 로컬 좌표.")]
    public Vector2 hangPoint = new Vector2(-0.52f, 5.83f);
    public float hangSwapSeconds = 0.5f;
 
    [Header("비행 / 착지")]
    public Sprite bodyFly01;
    public Sprite bodyFly02;
    public Sprite bodyLand;
    public float flySwapSeconds = 0.18f;
    [Tooltip("착지 자세에서 몸이 펴지는 데 걸리는 시간")]
    public float landRecoverSeconds = 0.22f;
    [Tooltip("착지 직후 우쭐한 표정을 유지하는 시간")]
    public float smugHoldSeconds = 0.9f;
    [Tooltip("착지 순간 눌리는 정도. 0.22면 세로 78%까지 눌린다.")]
    [Range(0f, 0.5f)] public float squashAmount = 0.22f;
    [Tooltip("튕길 때의 짧은 찌그러짐 시간 (자세는 바꾸지 않는다)")]
    public float bounceSquashSeconds = 0.14f;
 
    [Header("찌르기")]
    [Tooltip("찌를 때 눌리는 정도. 튕김(squashAmount * 0.7)보다 약하게 둔다.")]
    [Range(0f, 0.5f)] public float pokeSquashAmount = 0.14f;
    [Tooltip("눌렸다 펴지는 시간. 충돌이 아니라 '눌림'이라 튕김보다 살짝 길다.")]
    public float pokeSquashSeconds = 0.18f;
    [Tooltip("삐진 표정을 유지할 시간. 이 안에 대사가 나오면 립싱크가 이어받는다.")]
    public float pokeExpressionSeconds = 2f;

    [Header("잠자기")]
    public Sprite bodySleep01;
    public Sprite bodySleep02;
    public Sprite eyesSleep;
    public Sprite mouthSleep;
    [Tooltip("두 프레임을 바꾸는 간격. 호흡이라 걷기(0.15초)와 달리 아주 느리다.")]
    public float sleepSwapSeconds = 1.6f;
    [Tooltip("자는 자세는 앉아 있어 몸통 밑선이 idle보다 100px 높다. 세 파츠를 함께 내린다.")]
    public float sleepOffsetY = -1f;
    [Tooltip("깨어날 때 눈을 감은 채로 버티는 시간. 바로 뜨면 스위치처럼 보인다.")]
    public float wakeBlinkSeconds = 0.25f;

    [Header("자세별 높이 보정 (세 파츠를 함께 옮긴다)")]
    [Tooltip("착지 자세는 발이 idle보다 50px 위에 그려져 있어 그만큼 내려야 바닥에 닿는다.")]
    public float landOffsetY = -0.50f;
    [Tooltip("비행 자세는 발이 idle보다 14px 아래에 그려져 있다.")]
    public float flyOffsetY = 0.14f;
 
    [Header("눈 스프라이트")]
    public Sprite eyesOpen;
    public Sprite eyesClosed;
    public Sprite eyesBlind;
    [Tooltip("신났을 때 눈. 들렸을 때나 기분 좋을 때 쓴다.")]
    public Sprite eyesExcited;
    [Tooltip("우쭐할 때 눈. 착지 직후에 쓴다.")]
    public Sprite eyesSmug;
    [Tooltip("삐졌을 때 눈. 너무 많이 찔렸을 때 쓴다.")]
    public Sprite eyesUpset;
 
    [Header("입 스프라이트")]
    public Sprite mouthClosed;
    public Sprite mouthHalf;
    public Sprite mouthOpen;
    [Tooltip("집중한 입. 생각하는 동안과 한발서기에 쓴다.")]
    public Sprite mouthFocus;
    public Sprite mouthSmug;
    public Sprite mouthUpset;
 
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
    [Tooltip("작업표시줄 높이만큼 띄운다 (월드 단위)")]
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
 
    /// <summary>착지 연출이 도는 중인지. 이 동안은 다른 곳에서 몸통을 건드리지 않는다.</summary>
    public bool IsLanding => _pose == PoseKind.Land;

    /// <summary>지금 자고 있는지. 혼잣말 쪽에서 말을 걸지 여부를 판단하는 데 쓴다.</summary>
    public bool IsSleeping => _pose == PoseKind.Sleep;

    // 몸통 자세. 코루틴이 하나만 살아 있도록 여기서 관리한다.
    private enum PoseKind { Idle, Hang, Fly, Land, Sleep }
    private PoseKind _pose = PoseKind.Idle;
    private Coroutine _poseRoutine;
    private Coroutine _squashRoutine;
    private Coroutine _pokeExpressionRoutine;
 
    // 걷기 프레임 순서. idle을 사이에 끼워 다리가 모이는 중간 단계를 만든다.
    private Sprite[] _walkCycle;
    private int _facing = 1;   // 1 = 오른쪽으로 이동, -1 = 왼쪽으로 이동
 
    private bool _isWalking;
    private bool _eyesCovered;
    private bool _focusMouth;
    private bool _talking;      // 음성이 매 프레임 입을 직접 몰고 있는 동안(립싱크)
    private Sprite _baseEyes;   // 깜빡임이 끝나면 돌아갈 눈
    private float _mouthHoldUntil;   // 이 시각까지는 집중・기본이 입을 못 건드린다 (립싱크는 예외)
    // 찌르기 홀드는 시간제, 잠은 무기한이라 여기서 합친다. 립싱크(SetMouthLevel)는 이 검사를 아예 안 거치므로 최우선은 유지된다.
    private bool MouthHeld => Time.time < _mouthHoldUntil || _pose == PoseKind.Sleep;
 
    // localScale에 세 가지가 겹쳐 있다. 따로 들고 있다가 한 번에 합쳐 적용한다.
    //   _scale  : 메뉴에서 정하는 크기
    //   _flip   : 바라보는 방향 (+1 / -1)
    //   _squash : 착지 순간의 찌그러짐
    private float _scale = 0.42f;
    private int _flip = 1;
    private float _squashX = 1f, _squashY = 1f;

    // 인스펙터에서 맞춰둔 눈·입 위치(Z로 세운 순서, Y 미세 조정)를 보존한다.
    private Vector3 _bodyHome, _eyesHome, _mouthHome;
 
    private void Start()
    {
        _walkCycle = new[] { bodyWalk01, bodyIdle, bodyWalk02, bodyIdle };
 
        Camera cam = Camera.main;
        HalfWidth = cam.orthographicSize * cam.aspect;
        GroundY = -cam.orthographicSize + groundOffset;
 
        _scale = Mathf.Abs(transform.localScale.y);
        _flip = artFacesRight ? 1 : -1;
        ApplyTransform();
        _bodyHome  = bodyRenderer.transform.localPosition;
        _eyesHome  = eyesRenderer.transform.localPosition;
        _mouthHome = mouthRenderer.transform.localPosition;
 
        transform.position = new Vector3(0f, GroundY, 0f);
        bodyRenderer.sprite = bodyIdle;
        _baseEyes = eyesOpen;
        eyesRenderer.sprite = _baseEyes;
        mouthRenderer.sprite = mouthClosed;
 
        StartCoroutine(BlinkLoop());
        StartCoroutine(BehaviourLoop());
        StartCoroutine(WalkAnimationLoop());
    }
 
    // ───────────────── 스케일 / 방향 / 찌그러짐 ─────────────────
 
    private void ApplyTransform()
    {
        transform.localScale = new Vector3(
            _scale * _squashX * _flip,
            _scale * _squashY,
            1f);
    }
 
    /// <summary>화면에 보이는 크기를 바꾼다. 바라보는 방향과 찌그러짐은 유지한다.</summary>
    public void SetScale(float scale)
    {
        _scale = scale;
        ApplyTransform();
    }
 
    private void ApplyFacing()
    {
        _flip = artFacesRight ? _facing : -_facing;
        ApplyTransform();
    }
 
    private void SetSquash(float sx, float sy)
    {
        _squashX = sx;
        _squashY = sy;
        ApplyTransform();
    }
 
    /// <summary>
    /// 자세별로 발 높이가 달라서 세 파츠를 함께 위아래로 옮긴다.
    /// 몸통만 옮기면 눈·입이 어긋나므로 반드시 셋 다 같은 값을 준다.
    /// </summary>
    private void ApplyPoseOffset(float y)
    {
        Vector3 d = new Vector3(0f, y, 0f);
        bodyRenderer.transform.localPosition  = _bodyHome  + d;
        eyesRenderer.transform.localPosition  = _eyesHome  + d;
        mouthRenderer.transform.localPosition = _mouthHome + d;
    }
 
    // ───────────────── 자세 ─────────────────
 
    private void StopPose()
    {
        if (_poseRoutine != null)
        {
            StopCoroutine(_poseRoutine);
            _poseRoutine = null;
        }
    }
 
    private void StopSquash()
    {
        if (_squashRoutine != null)
        {
            StopCoroutine(_squashRoutine);
            _squashRoutine = null;
        }
        SetSquash(1f, 1f);
    }
 
    /// <summary>두 프레임을 번갈아 재생한다. 첫 장이 없으면 idle로 버틴다.</summary>
    private IEnumerator TwoFrameLoop(Sprite a, Sprite b, float seconds)
    {
        if (a == null)
        {
            bodyRenderer.sprite = bodyIdle;
            yield break;
        }
        bool flip = false;
        while (true)
        {
            bodyRenderer.sprite = (flip && b != null) ? b : a;
            flip = !flip;
            yield return new WaitForSeconds(seconds);
        }
    }
 
    /// <summary>던져져 날아가는 중. 이미 비행 중이면 아무것도 하지 않는다.</summary>
    public void SetFlying()
    {
        if (_pose == PoseKind.Fly) return;
        WakeUp();

        StopPose();
        StopSquash();
        _pose = PoseKind.Fly;
        _isWalking = false;
        ApplyPoseOffset(flyOffsetY);
        _poseRoutine = StartCoroutine(TwoFrameLoop(bodyFly01, bodyFly02, flySwapSeconds));
    }
 
    /// <summary>
    /// 튕길 때. 자세는 그대로 두고 짧게만 찌그러뜨린다.
    /// 튕김은 한두 프레임 만에 다시 떠오르므로 착지 자세로 바꾸면 깜빡이기만 한다.
    /// </summary>
    public void PlayBounce()
    {
        StopSquash();
        _squashRoutine = StartCoroutine(SquashRoutine(bounceSquashSeconds, squashAmount * 0.7f));
    }
 
    /// <summary>찔렸을 때. 위아래로 살짝 눌리고 삐진 표정을 잠깐 유지한다.</summary>
    public void PlayPoke()
    {
        WakeUp();   // 깨우기가 먼저여야 삐진 눈이 자는 몸에 붙지 않는다
        StopSquash();
        _squashRoutine = StartCoroutine(SquashRoutine(pokeSquashSeconds, pokeSquashAmount));
 
        // 착지 연출 중에는 LandRoutine이 표정을 소유하므로 덮지 않는다.
        // 자세(_pose)와 걷기 코루틴은 그대로 둔다 — 찌르기는 제자리에서 일어나는 반응이다.
        if (CanAct())
        {
            SetExpression(eyesUpset, mouthUpset, pokeExpressionSeconds);
 
            if (_pokeExpressionRoutine != null) StopCoroutine(_pokeExpressionRoutine);
            _pokeExpressionRoutine = StartCoroutine(RevertPokeExpression(pokeExpressionSeconds));
        }
    }
 
    private IEnumerator RevertPokeExpression(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        _pokeExpressionRoutine = null;
        // 그 사이 들렸거나 자세가 바뀌었으면 그쪽 연출이 이긴다
        if (_pose == PoseKind.Idle && !IsHeld) ResetExpression();
    }
 
    /// <summary>바닥에 내려앉는 순간. 웅크린 자세 + 찌그러짐 + 우쭐한 표정.</summary>
    public void PlayLanding()
    {
        if (_pose == PoseKind.Land) return;
        WakeUp();

        StopPose();
        StopSquash();
        _pose = PoseKind.Land;
        _isWalking = false;
        _poseRoutine = StartCoroutine(LandRoutine());
    }
 
    private IEnumerator SquashRoutine(float seconds, float amount)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            // 닿는 순간 확 눌리고 빠르게 펴진다 (k가 1 -> 0)
            float k = 1f - Mathf.Clamp01(t / seconds);
            float s = amount * k * k;
            SetSquash(1f + s * 0.6f, 1f - s);
            yield return null;
        }
        SetSquash(1f, 1f);
        _squashRoutine = null;
    }
 
    private IEnumerator LandRoutine()
    {
        ApplyPoseOffset(landOffsetY);
        bodyRenderer.sprite = bodyLand != null ? bodyLand : bodyIdle;
        transform.rotation = Quaternion.identity;
 
        // 던져졌다가 아무렇지 않게 내려앉은 척하는 게 소마왕답다
        // 홀드를 걸어야 착지 직후 말을 걸어도 우쭐한 입이 SetFocused에 안 지워진다
        SetExpression(eyesSmug, mouthSmug, landRecoverSeconds + smugHoldSeconds);
 
        yield return SquashRoutine(landRecoverSeconds, squashAmount);
        yield return new WaitForSeconds(smugHoldSeconds);
 
        _pose = PoseKind.Idle;
        _poseRoutine = null;
        ApplyPoseOffset(0f);
 
        // 착지 연출이 끝나기 전에 다시 들렸으면 그쪽 자세를 건드리지 않는다
        if (!IsHeld)
        {
            bodyRenderer.sprite = bodyIdle;
            SetEyes(eyesOpen);
            if (!_talking) CloseMouth();
        }
    }

    // ───────────────── 잠자기 ─────────────────

    /// <summary>그 자리에 주저앉아 잔다. 자리 이동은 하지 않는다.</summary>
    public void EnterSleep()
    {
        if (_pose == PoseKind.Sleep) return;
        if (IsHeld || _pose != PoseKind.Idle) return;   // 들렸거나 날거나 착지 중에 잠들면 자세가 싸운다

        StopPose();
        StopSquash();
        _pose = PoseKind.Sleep;
        _isWalking = false;
        transform.rotation = Quaternion.identity;
        ApplyPoseOffset(sleepOffsetY);
        SetExpression(eyesSleep, mouthSleep);
        _poseRoutine = StartCoroutine(TwoFrameLoop(bodySleep01, bodySleep02, sleepSwapSeconds));
    }

    /// <summary>깨운다. 자고 있지 않으면 아무 일도 하지 않는다 — 아무 데서나 조건 없이 불러도 안전하다.</summary>
    public void WakeUp()
    {
        if (_pose != PoseKind.Sleep) return;

        StopPose();
        _pose = PoseKind.Idle;
        ApplyPoseOffset(0f);
        bodyRenderer.sprite = bodyIdle;
        StartCoroutine(WakeBlinkRoutine());
    }

    private IEnumerator WakeBlinkRoutine()
    {
        // 눈 비비는 한 박자. 바로 뜨면 전원 스위치처럼 보인다
        SetEyes(eyesClosed);
        yield return new WaitForSeconds(wakeBlinkSeconds);

        // 그 사이에 들렸거나 자세가 바뀌었으면 그쪽 표정(Hang의 eyesExcited 등)이 이미
        // 주인이므로 건드리지 않는다. 몸은 WakeUp()에서 이미 idle로 돌려놨으니 여기선 안 건드린다.
        // MouthHeld는 찌르기·착지처럼 SetExpression(holdSeconds)로 걸어둔 표정이 아직
        // 유효한지도 같이 알려준다 - 자는 미진이를 찌르면 WakeUp() 다음에 바로 삐진 표정이
        // 걸리는데, 그걸 0.25초짜리 이 코루틴이 먼저 끝나며 지워버리면 안 된다.
        if (!IsHeld && _pose == PoseKind.Idle && !MouthHeld) ResetExpression();
    }

    // ───────────────── 눈 깜빡임 ─────────────────
    private IEnumerator BlinkLoop()
    {
        while (true)
        {
            while (_eyesCovered) yield return null;
 
            yield return new WaitForSeconds(
                Random.Range(blinkIntervalRange.x, blinkIntervalRange.y));
 
            // 대기하는 동안 눈이 가려졌을 수 있다 - 그러면 이번 깜빡임은 건너뛴다
            if (_eyesCovered) continue;
            // 이미 감은 눈(신남·우쭐·잠)일 때는 깜빡여도 표가 안 나고 오히려 표정이 끊긴다
            if (_baseEyes == eyesClosed || _baseEyes == eyesExcited
                || _baseEyes == eyesSmug || _baseEyes == eyesSleep)
                continue;
 
            eyesRenderer.sprite = eyesClosed;
            yield return new WaitForSeconds(blinkDuration);
            if (!_eyesCovered) eyesRenderer.sprite = _baseEyes;
 
            // 가끔 두 번 연속으로 깜빡이면 더 살아 있어 보인다
            if (Random.value < 0.25f && !_eyesCovered)
            {
                yield return new WaitForSeconds(0.12f);
                if (_eyesCovered) continue;
                eyesRenderer.sprite = eyesClosed;
                yield return new WaitForSeconds(blinkDuration);
                if (!_eyesCovered) eyesRenderer.sprite = _baseEyes;
            }
        }
    }
 
    // ───────────────── 대기 / 걷기 전환 ─────────────────
    private IEnumerator BehaviourLoop()
    {
        while (true)
        {
            _isWalking = false;
            if (CanAct()) bodyRenderer.sprite = bodyIdle;
 
            yield return new WaitForSeconds(
                Random.Range(idleDurationRange.x, idleDurationRange.y));
 
            // 가끔 한발서기로 균형을 잡아 본다
            if (bodyStand01 != null && CanAct() && Random.value < standChance)
                yield return StandOnOneLeg();
 
            // 들려 있거나 착지 연출 중에는 걷기를 시작하지 않고 기다린다
            while (!CanAct() || !walkEnabled) yield return null;
 
            _facing = Random.value < 0.5f ? 1 : -1;
            ApplyFacing();
 
            _isWalking = true;
            yield return new WaitForSeconds(
                Random.Range(walkDurationRange.x, walkDurationRange.y));
        }
    }
 
    /// <summary>스스로 움직여도 되는 상태인지 (들림·비행·착지 중이 아님).</summary>
    private bool CanAct() => !IsHeld && _pose == PoseKind.Idle;
 
    private IEnumerator WalkAnimationLoop()
    {
        int frame = 0;
        while (true)
        {
            if (_isWalking && CanAct())
            {
                bodyRenderer.sprite = _walkCycle[frame % _walkCycle.Length];
                frame++;
            }
            yield return new WaitForSeconds(framePerSecond);
        }
    }
 
    private void Update()
    {
        if (!_isWalking || !CanAct()) return;
 
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
 
    private IEnumerator StandOnOneLeg()
    {
        // 말하는 동안은 립싱크가 매 프레임 입을 몰고 있으니 건드리지 않는다
        if (mouthFocus != null && !_talking) mouthRenderer.sprite = mouthFocus;
 
        float until = Time.time + Random.Range(standDurationRange.x, standDurationRange.y);
        bool flip = false;
        while (Time.time < until && CanAct())
        {
            bodyRenderer.sprite = flip ? bodyStand02 : bodyStand01;
            flip = !flip;
            yield return new WaitForSeconds(standWobbleSeconds);
        }
        // 서 있는 동안 들렸으면 몸은 이제 다른 자세 소관이니 건드리지 않는다
        if (CanAct()) bodyRenderer.sprite = bodyIdle;
 
        // 대화 중이 아니면 원래 입으로 되돌아간다 (말하는 중이면 립싱크가 알아서 정리한다)
        if (mouthFocus != null && !_talking) CloseMouth();
    }
 
    // ───────────────── 외부에서 부르는 것들 ─────────────────
 
    /// <summary>
    /// 들려 있거나 날아가는 동안, 혹은 그냥 잠시 멈춰 있어야 할 때(채팅창 등) 스스로 걷지 않게 한다.
    /// hangPose를 끄면 정지만 하고 들린 자세(몸 흔들림, 신난 눈)는 보여주지 않는다.
    /// </summary>
    public void SetHeld(bool held, bool hangPose = true)
    {
        if (held) WakeUp();   // 자는 채로 들리면 매달림 자세가 이겨야 하므로 먼저 깨운다

        IsHeld = held;
        if (held)
        {
            _isWalking = false;
            StopPose();
            StopSquash();
            ApplyPoseOffset(0f);   // 매달림은 hangPoint가 위치를 잡으므로 보정 없음
 
            if (hangPose && bodyHold01 != null)
            {
                _pose = PoseKind.Hang;
                _poseRoutine = StartCoroutine(
                    TwoFrameLoop(bodyHold01, bodyHold02, hangSwapSeconds));
            }
            else
            {
                _pose = PoseKind.Idle;
                bodyRenderer.sprite = bodyIdle;
                // WakeUp()의 눈 비비기(WakeBlinkRoutine)가 IsHeld를 보고 되돌리기를
                // 양보하는데, hangPose가 없으면 아무도 이어받지 않아 눈이 감긴 채 남는다.
                // hangPose가 있을 때는 바로 아래에서 eyesExcited가 넘겨받는다.
                SetEyes(eyesOpen);
            }

            if (hangPose && eyesExcited != null) SetEyes(eyesExcited);
        }
        else
        {
            // 착지 연출이 돌고 있으면 그게 알아서 끝내고 정리한다
            if (_pose == PoseKind.Land) return;
 
            StopPose();
            StopSquash();
            _pose = PoseKind.Idle;
            ApplyPoseOffset(0f);
            bodyRenderer.sprite = bodyIdle;
            transform.rotation = Quaternion.identity;
            SetEyes(eyesOpen);
        }
    }
 
    /// <summary>이동 방향을 바깥에서 정한다 (던져진 방향을 보게 할 때).</summary>
    public void FaceDirection(int dir)
    {
        if (dir == 0) return;
        _facing = dir > 0 ? 1 : -1;
        ApplyFacing();
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
 
    /// <summary>말이 끝났을 때 입을 닫는다. 집중 중이면 집중한 입으로 대신 둔다.</summary>
    public void CloseMouth()
    {
        if (MouthHeld) return;   // 표정 홀드가 끝나기 전까지는 집중・기본이 입을 못 건드린다
        mouthRenderer.sprite = (_focusMouth && mouthFocus != null) ? mouthFocus : mouthClosed;
    }
 
    /// <summary>집중한 표정으로 둔다. 말하는 동안은 립싱크가 우선한다.</summary>
    public void SetFocused(bool on)
    {
        _focusMouth = on;
        if (!MouthHeld) CloseMouth();
    }
 
    /// <summary>
    /// 음성 재생 중이라 립싱크가 매 프레임 입을 직접 몰고 있는지 알린다.
    /// 다른 곳(한발서기, 착지 등)에서 그동안은 입을 건드리지 않게 하는 용도.
    /// </summary>
    public void SetTalking(bool talking) => _talking = talking;
 
    /// <summary>평소 표정을 바꾼다. 깜빡임도 이 표정으로 되돌아온다.</summary>
    public void SetEyes(Sprite eyes)
    {
        _baseEyes = eyes != null ? eyes : eyesOpen;
        if (!_eyesCovered) eyesRenderer.sprite = _baseEyes;
    }
 
    /// <summary>
    /// 눈과 입을 한 번에 바꾼다. null은 건드리지 않는다 (삐짐·우쭐 같은 짝에 쓴다).
    /// holdSeconds를 주면 그 시간 동안은 집중・기본 입이 이 표정을 덮지 못한다
    /// (립싱크는 예외 — 말이 시작되면 곧바로 넘어간다).
    /// </summary>
    public void SetExpression(Sprite eyes, Sprite mouth, float holdSeconds = 0f)
    {
        if (eyes != null) SetEyes(eyes);
        if (mouth != null && !_talking)
        {
            _focusMouth = false;
            mouthRenderer.sprite = mouth;
        }
        if (holdSeconds > 0f) _mouthHoldUntil = Time.time + holdSeconds;
    }
 
    /// <summary>기본 표정으로 되돌린다.</summary>
    public void ResetExpression()
    {
        _focusMouth = false;
        SetEyes(eyesOpen);
        if (!_talking) mouthRenderer.sprite = mouthClosed;
    }
 
    /// <summary>가리기 중에는 blind 스프라이트로 바꾼다 (깜빡임도 멈춘다).</summary>
    public void SetEyesCovered(bool covered)
    {
        _eyesCovered = covered;
        if (!covered)
            eyesRenderer.sprite = _baseEyes;
        else
            eyesRenderer.sprite = eyesBlind != null ? eyesBlind : eyesClosed;
    }
}
 