using UnityEngine;

/// <summary>
/// 미진이를 마우스로 집어 옮기고, 놓으면 떨어지고, 휙 던지면 날아가게 한다.
/// 물리 엔진을 쓰지 않고 직접 계산하므로 움직임을 값으로 조절할 수 있다.
///
/// 붙이는 곳: Mijin 오브젝트 (MijinCharacter, BoxCollider2D와 같이)
/// BoxCollider2D는 집을 수 있는 영역을 정한다. 권장값:
///   Offset (-0.87, 2.44)   Size (2.76, 4.89)
/// </summary>
[RequireComponent(typeof(MijinCharacter))]
[RequireComponent(typeof(BoxCollider2D))]
public class MijinDrag : MonoBehaviour
{
    [Header("던지기")]
    [Tooltip("중력 가속도 (월드 단위/초²). 클수록 빨리 떨어진다.")]
    public float gravity = 14f;
    [Tooltip("바닥에 부딪힐 때 남는 속도 비율. 0이면 튀지 않는다.")]
    [Range(0f, 0.8f)] public float bounciness = 0.25f;
    [Tooltip("벽에 부딪힐 때 남는 속도 비율.")]
    [Range(0f, 0.9f)] public float wallBounciness = 0.9f;
    [Tooltip("던지는 속도의 상한. 너무 멀리 날아가지 않게 막는다.")]
    public float maxThrowSpeed = 14f;
    [Tooltip("착지 후 바닥에서 미끄러지는 정도. 클수록 빨리 멈춘다.")]
    public float groundFriction = 10f;

    [Header("집기")]
    [Tooltip("마우스와 몸의 간격 상한. 커지면 커서가 투명한 곳으로 벗어나 드래그가 끊길 수 있다.")]
    public float maxGrabOffset = 0.6f;

    [Header("클릭")]
    [Tooltip("이보다 적게 움직이고 떼면 클릭으로 본다 (월드 단위)")]
    public float clickMaxDistance = 0.3f;
    [Tooltip("이보다 짧게 누르고 떼면 클릭으로 본다 (초)")]
    public float clickMaxDuration = 0.4f;
    public UnityEngine.Events.UnityEvent onClick;

    private float _grabTime;
    private Vector3 _grabStartPos;

    private MijinCharacter _mijin;
    private BoxCollider2D _collider;
    private Camera _cam;

    private bool _isDragging;
    private bool _isFalling;
    private Vector2 _grabOffset;     // 잡은 지점과 원점의 차이
    private Vector2 _velocity;
    private Vector3 _prevMouseWorld;

    private void Awake()
    {
        _mijin = GetComponent<MijinCharacter>();
        _collider = GetComponent<BoxCollider2D>();
        _cam = Camera.main;
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0)) TryGrab();
        else if (Input.GetMouseButton(0) && _isDragging) Drag();
        else if (Input.GetMouseButtonUp(0) && _isDragging) Release();

        if (_isFalling) Fall();
    }

    private Vector3 MouseWorld()
    {
        Vector3 p = _cam.ScreenToWorldPoint(Input.mousePosition);
        p.z = 0f;
        return p;
    }

    // ───────────────── 집기 ─────────────────
    private void TryGrab()
    {
        Vector3 m = MouseWorld();
        if (!_collider.OverlapPoint(m)) return;   // 몸 밖을 클릭하면 무시

        _isDragging = true;
        _isFalling = false;
        _velocity = Vector2.zero;
        _prevMouseWorld = m;

        // 잡은 지점을 유지하되, 너무 멀면 당겨서 커서가 몸 위에 남게 한다
        Vector2 offset = (Vector2)(transform.position - m);
        _grabOffset = Vector2.ClampMagnitude(offset, maxGrabOffset);

        _mijin.SetHeld(true);
        _grabTime = Time.time;
        _grabStartPos = m;
    }
    
    private void Drag()
    {
        Vector3 m = MouseWorld();
        transform.position = (Vector2)m + _grabOffset;

        // 최근 마우스 움직임을 속도로 기록한다 (급격한 튐을 막으려고 살짝 부드럽게)
        if (Time.deltaTime > 0f)
        {
            Vector2 raw = (m - _prevMouseWorld) / Time.deltaTime;
            _velocity = Vector2.Lerp(_velocity, raw, 0.5f);
        }
        _prevMouseWorld = m;
    }

   private void Release()
    {
        _isDragging = false;

        // 거의 안 움직이고 짧게 눌렀다 뗐으면 던지기가 아니라 클릭이다
        float moved = Vector3.Distance(MouseWorld(), _grabStartPos);
        bool isClick = moved < clickMaxDistance
                       && Time.time - _grabTime < clickMaxDuration;
        if (isClick)
        {
            _velocity = Vector2.zero;
            _isFalling = false;
            transform.position = new Vector3(
                transform.position.x, _mijin.GroundY, 0f);
            _mijin.SetHeld(false);
            onClick.Invoke();
            return;
        }

        _isFalling = true;
        _velocity = Vector2.ClampMagnitude(_velocity, maxThrowSpeed);

        if (Mathf.Abs(_velocity.x) > 0.5f)
            _mijin.FaceDirection(_velocity.x > 0f ? 1 : -1);
    }

    // ───────────────── 낙하와 착지 ─────────────────
    private void Fall()
    {
        _velocity.y -= gravity * Time.deltaTime;

        Vector3 p = transform.position;
        p += (Vector3)_velocity * Time.deltaTime;

        // 좌우 벽
        float limit = _mijin.HalfWidth - _mijin.edgeMargin;
        if (p.x > limit || p.x < -limit)
        {
            p.x = Mathf.Clamp(p.x, -limit, limit);
            _velocity.x = -_velocity.x * wallBounciness;
            _mijin.FaceDirection(_velocity.x > 0f ? 1 : -1);
        }

        // 바닥
        if (p.y <= _mijin.GroundY)
        {
            p.y = _mijin.GroundY;

            if (Mathf.Abs(_velocity.y) > 1.5f)
            {
                // 아직 튈 힘이 남았다
                _velocity.y = -_velocity.y * bounciness;
                _velocity.x *= 0.8f;
            }
            else
            {
                // 바닥에서 미끄러지다 멈춘다
                _velocity.y = 0f;
                _velocity.x = Mathf.MoveTowards(
                    _velocity.x, 0f, groundFriction * Time.deltaTime);

                if (Mathf.Abs(_velocity.x) < 0.05f)
                {
                    _velocity = Vector2.zero;
                    _isFalling = false;
                    _mijin.SetHeld(false);   // 다시 스스로 걷기 시작
                }
            }
        }

        transform.position = p;
    }
}
