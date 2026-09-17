using UnityEngine;
 
/// <summary>
/// 미진이를 마우스로 집어 옮기고, 놓으면 떨어지고, 휙 던지면 날아가게 한다.
/// 물리 엔진을 쓰지 않고 직접 계산하므로 움직임을 값으로 조절할 수 있다.
///
/// 자세는 MijinCharacter가 관리한다:
///   잡음 -> SetHeld(true)   후드 꼭대기가 커서에 붙고 몸이 흔들린다
///   던짐 -> SetFlying()     fly_01/02
///   튕김 -> PlayBounce()    자세 유지 + 짧은 찌그러짐
///   착지 -> PlayLanding()   웅크린 자세 + 찌그러짐 + 우쭐한 표정
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
    [Tooltip("이보다 세로 속도가 남아 있으면 튕기고, 아니면 내려앉는다.")]
    public float bounceThreshold = 1.5f;
 
    [Header("매달림")]
    [Tooltip("클수록 몸이 커서를 빠르게 따라온다. 낮추면 흔들림이 커진다.")]
    public float hangFollow = 12f;
    public float hangTiltPerSpeed = 4f;
    public float maxHangTilt = 22f;
 
    [Header("비행 회전")]
    [Tooltip("날아가는 동안 속도 방향으로 기울어지는 정도. 0이면 회전하지 않는다.")]
    public float flyTiltPerSpeed = 2.5f;
    public float maxFlyTilt = 30f;
 
    [Header("클릭")]
    [Tooltip("이보다 적게 움직이고 떼면 제자리에 내려놓는다 (월드 단위)")]
    public float clickMaxDistance = 0.3f;
    [Tooltip("이 시간이 지나야 실제로 들어올린다. 그 전에 떼면 찌르기다. (초)")]
    public float clickMaxDuration = 0.4f;
    public UnityEngine.Events.UnityEvent onClick;
 
    private MijinCharacter _mijin;
    private BoxCollider2D _collider;
    private Camera _cam;
 
    private bool _pressed;      // 눌렀지만 아직 들어올리지는 않은 상태 (찌르기와 구분하는 대기 시간)
    private bool _isDragging;
    private bool _isFalling;
    private Vector2 _velocity;
    private Vector3 _prevMouseWorld;
    private float _tilt;
 
    private float _grabTime;
    private Vector3 _grabStartPos;
 
    private void Awake()
    {
        _mijin = GetComponent<MijinCharacter>();
        _collider = GetComponent<BoxCollider2D>();
        _cam = Camera.main;
    }
 
    private void Update()
    {
        if (Input.GetMouseButtonDown(0)) OnPress();
        else if (Input.GetMouseButton(0) && (_pressed || _isDragging)) OnHold();
        else if (Input.GetMouseButtonUp(0) && (_pressed || _isDragging)) Release();
 
        if (_isFalling) Fall();
    }
 
    private Vector3 MouseWorld()
    {
        Vector3 p = _cam.ScreenToWorldPoint(Input.mousePosition);
        p.z = 0f;
        return p;
    }
 
    // ───────────────── 집기 ─────────────────
 
    /// <summary>누른 순간에는 아직 들어올리지 않는다. 바로 들어올리면 찌르기 반응과 겹친다.</summary>
    private void OnPress()
    {
        Vector3 m = MouseWorld();
        if (!_collider.OverlapPoint(m)) return;   // 몸 밖을 클릭하면 무시
 
        _pressed = true;
        _isFalling = false;
        _velocity = Vector2.zero;
        _prevMouseWorld = m;
        _grabTime = Time.time;
        _grabStartPos = m;
    }
 
    /// <summary>클릭으로 볼 시간(clickMaxDuration)이 지나야 실제로 들어올린다.</summary>
    private void OnHold()
    {
        if (_pressed && Time.time - _grabTime >= clickMaxDuration)
        {
            _pressed = false;
            _isDragging = true;
            _mijin.SetHeld(true);
            _prevMouseWorld = MouseWorld();   // 대기하는 동안 멈춰 있던 값이라 여기서 다시 잡는다
        }
 
        if (_isDragging) Drag();
    }
 
    private void Drag()
    {
        Vector3 m = MouseWorld();
 
        // 움직이는 반대쪽으로 몸이 쏠린다. 스케일(좌우 반전)이 회전보다 먼저 적용되는
        // 순서라 기울임 방향은 바라보는 방향과 무관하다 - sign(localScale.x)로 보정하면
        // 오히려 방향별로 반대가 된다.
        float want = Mathf.Clamp(-_velocity.x * hangTiltPerSpeed, -maxHangTilt, maxHangTilt);
        _tilt = Mathf.Lerp(_tilt, want, Time.deltaTime * 6f);
        transform.rotation = Quaternion.Euler(0f, 0f, _tilt);
 
        // 후드 꼭대기가 커서에 붙게 한다. 몸에서 가장 큰 불투명 덩어리라
        // 커서가 투명한 곳으로 벗어나 드래그가 끊기지 않는다.
        Vector3 grab = transform.TransformPoint(_mijin.hangPoint);
        Vector3 target = transform.position + (m - grab);
        transform.position = Vector3.Lerp(
            transform.position, target, 1f - Mathf.Exp(-hangFollow * Time.deltaTime));
 
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
        bool wasDragging = _isDragging;
        _pressed = false;
        _isDragging = false;
        _tilt = 0f;
 
        // clickMaxDuration이 지나기 전에 뗐다 - 들어올려진 적이 없으니 찌르기다
        if (!wasDragging)
        {
            onClick.Invoke();
            return;
        }
 
        // 들어올린 채로 거의 안 움직이고 놓았으면 던지기가 아니라 제자리에 내려놓는다
        // (clickMaxDuration이 지나야 여기 올 수 있으니 시간 조건은 항상 참이라 뺐다)
        float moved = Vector3.Distance(MouseWorld(), _grabStartPos);
        if (moved < clickMaxDistance)
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
        _mijin.SetFlying();
 
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
 
            if (Mathf.Abs(_velocity.y) > bounceThreshold)
            {
                // 아직 튈 힘이 남았다. 자세는 그대로 두고 찌그러짐만 준다
                // (튕김은 곧 다시 떠오르므로 착지 자세로 바꾸면 깜빡이기만 한다)
                _velocity.y = -_velocity.y * bounciness;
                _velocity.x *= 0.8f;
                _tilt = 0f;
                transform.rotation = Quaternion.identity;
                _mijin.PlayBounce();
            }
            else
            {
                // 바닥에서 미끄러지다 멈춘다
                _velocity.y = 0f;
                _velocity.x = Mathf.MoveTowards(
                    _velocity.x, 0f, groundFriction * Time.deltaTime);
 
                _tilt = 0f;
                transform.rotation = Quaternion.identity;
                _mijin.PlayLanding();
 
                if (Mathf.Abs(_velocity.x) < 0.05f)
                {
                    _velocity = Vector2.zero;
                    _isFalling = false;
                    // 착지 연출이 끝나면 MijinCharacter가 스스로 idle로 돌아간다
                    _mijin.SetHeld(false);
                }
            }
        }
        else if (!_mijin.IsLanding)
        {
            // 공중에서는 속도 방향으로 기울어진다
            _mijin.SetFlying();
            float want = Mathf.Clamp(
                -_velocity.x * flyTiltPerSpeed, -maxFlyTilt, maxFlyTilt);
            _tilt = Mathf.Lerp(_tilt, want, Time.deltaTime * 5f);
            transform.rotation = Quaternion.Euler(0f, 0f, _tilt);
        }
 
        transform.position = p;
    }
}
 