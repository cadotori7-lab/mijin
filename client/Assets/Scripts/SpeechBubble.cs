using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 미진이 머리 위에 떠 있는 말풍선.
///
/// 캐릭터의 자식으로 두면 왼쪽을 볼 때 스케일이 뒤집히면서 글자까지 거울처럼
/// 뒤집힌다. 그래서 별개 오브젝트로 두고 위치만 따라간다.
///
/// 씬 구성:
///   SpeechBubble        Canvas(World Space) + CanvasGroup + 이 스크립트
///    └ Panel            Image (9-slice)              ← bubbleRect
///       └ Text          TextMeshProUGUI              ← label
/// </summary>
public class SpeechBubble : MonoBehaviour
{
    [Header("연결")]
    public Transform target;          // Mijin 오브젝트
    public RectTransform bubbleRect;  // Panel
    public TextMeshProUGUI label;     // Text
    public CanvasGroup canvasGroup;

    [Header("위치")]
    [Tooltip("캐릭터 발밑 기준 오프셋 (월드 단위). x는 바라보는 방향에 따라 뒤집힌다.")]
    public Vector2 offset = new Vector2(-0.31f, 1.9f);
    [Tooltip("offset을 정할 때 기준으로 삼은 캐릭터 크기")]
    public float baseScale = 0.42f;
    [Header("크기")]
    [Tooltip("말풍선 최대 너비 (px). 넘으면 줄바꿈된다.")]
    public float maxWidth = 260f;
    [Tooltip("글자 주변 여백 (px)")]
    public Vector2 padding = new Vector2(28f, 20f);

    [Header("연출")]
    [Tooltip("초당 출력 글자 수. 0이면 한 번에 다 나온다.")]
    public float charsPerSecond = 22f;
    [Tooltip("다 출력한 뒤 유지할 최소/최대 시간 (초)")]
    public Vector2 holdRange = new Vector2(1.8f, 6f);
    public float fadeDuration = 0.15f;

    [Header("테스트")]
    [Tooltip("스페이스바로 테스트 대사를 띄운다. 완성 후에는 꺼둔다.")]
    public bool testWithSpaceKey = true;

    public bool IsShowing { get; private set; }

    private Coroutine _routine;
    private float _halfScreenWidth;
    private float _holdUntil;

    private void Start()
    {
        Camera cam = Camera.main;
        _halfScreenWidth = cam.orthographicSize * cam.aspect;

        canvasGroup.alpha = 0f;
        label.text = "";
    }

    private void Update()
    {
        FollowTarget();

        if (testWithSpaceKey && Input.GetKeyDown(KeyCode.Space))
            Show("파트너님, 방금 그 페이지 데이터 수집 완료했습니다. 미진이 용량이 조금 뿌듯해졌어요.");
    }

    private void FollowTarget()
    {
        if (target == null) return;

        // 바라보는 방향에 맞춰 x 오프셋을 뒤집는다
        float dir = Mathf.Sign(target.localScale.x);
        float k = Mathf.Abs(target.localScale.x) / baseScale;   // 크기 변화만큼 같이 움직인다
        Vector3 p = target.position + new Vector3(offset.x * dir * k, offset.y * k, 0f);

        // 화면 밖으로 나가지 않게 좌우를 눌러준다
        float halfBubble = bubbleRect.sizeDelta.x * 0.5f * 0.01f;  // px -> 월드
        float limit = _halfScreenWidth - halfBubble - 0.1f;
        p.x = Mathf.Clamp(p.x, -limit, limit);

        transform.position = p;
    }

    // ───────────────── 외부에서 부르는 것들 ─────────────────

    /// <summary>대사를 띄운다. 이미 말하는 중이면 새 대사로 바꾼다.</summary>
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(ShowRoutine(text));
    }

    /// <summary>말풍선을 즉시 닫는다.</summary>
    public void Hide()
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(FadeOut());
    }
    /// <summary>말풍선을 이만큼 더 띄워 둔다. 음성 재생 길이를 맞추는 데 쓴다.</summary>
    public void ExtendHold(float seconds)
    {
        _holdUntil = Mathf.Max(_holdUntil, Time.time + seconds);
    }

    private IEnumerator ShowRoutine(string text)
    {
        IsShowing = true;
        _holdUntil = 0f;   // 이전 대사의 연장 요청을 물려받지 않게 초기화

        // 먼저 전체 문장으로 크기를 잡아둔다 (글자가 늘어날 때마다 말풍선이 들썩이지 않게)
        label.text = text;
        ResizeToText();

        // 한 글자씩 출력
        if (charsPerSecond > 0f)
        {
            label.maxVisibleCharacters = 0;
            yield return FadeIn();

            int total = label.textInfo.characterCount;
            float perChar = 1f / charsPerSecond;
            for (int i = 1; i <= total; i++)
            {
                label.maxVisibleCharacters = i;
                yield return new WaitForSeconds(perChar);
            }
        }
        else
        {
            label.maxVisibleCharacters = int.MaxValue;
            yield return FadeIn();
        }

        // 길이에 비례해 유지 시간을 정한다
        // 길이에 비례해 정하되, 음성이 더 길면 ExtendHold가 늘려 준다
        float hold = Mathf.Clamp(text.Length * 0.09f, holdRange.x, holdRange.y);
        _holdUntil = Mathf.Max(_holdUntil, Time.time + hold);
        while (Time.time < _holdUntil) yield return null;

        yield return FadeOut();
    }

    private void ResizeToText()
    {
        // 줄바꿈 없이 필요한 너비를 재고, 최대 너비를 넘으면 거기서 자른다
        float w = Mathf.Min(label.preferredWidth, maxWidth);
        label.rectTransform.sizeDelta = new Vector2(w, 0f);

        // 그 너비에서 실제로 필요한 높이를 다시 잰다
        label.ForceMeshUpdate();
        float h = label.preferredHeight;

        label.rectTransform.sizeDelta = new Vector2(w, h);
        bubbleRect.sizeDelta = new Vector2(w + padding.x * 2f, h + padding.y * 2f);
        label.ForceMeshUpdate();
    }

    private IEnumerator FadeIn()
    {
        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            canvasGroup.alpha = Mathf.Clamp01(t / fadeDuration);
            yield return null;
        }
        canvasGroup.alpha = 1f;
    }

    private IEnumerator FadeOut()
    {
        float start = canvasGroup.alpha;
        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(start, 0f, t / fadeDuration);
            yield return null;
        }
        canvasGroup.alpha = 0f;
        label.text = "";
        IsShowing = false;
        _routine = null;
    }
}
