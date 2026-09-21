using System.Collections;
using UnityEngine;

/// <summary>
/// 주기적으로 화면을 보고 스스로 한 마디 한다.
///
/// 마스코트는 말이 많으면 금방 피곤해진다. 그래서 "언제 말할까"보다
/// "언제 조용히 있을까"를 정하는 게 이 클래스의 일이다:
///   - 파트너님이 자리를 비웠을 때
///   - 전체 화면(게임·영상)을 보고 있을 때
///   - 미진이를 들고 있거나 던지는 중일 때
///   - 이미 말하고 있을 때
///   - 화면이 계속 그대로일 때 (간격을 점점 늘린다)
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinMonologue : MonoBehaviour
{
    [Header("연결")]
    public MijinTalk talk;
    public MijinCharacter mijin;
    public MijinScreenCapture capture;
    public MijinBlind blind;

    [Header("켜기/끄기")]
    public bool enableMonologue = true;

    [Header("간격")]
    [Tooltip("혼잣말 사이 간격 (초). 이 범위에서 무작위로 정해진다.")]
    public Vector2 intervalRange = new Vector2(70f, 120f);
    [Tooltip("시작 후 첫 혼잣말까지 기다릴 시간 (초)")]
    public float startDelay = 10f;
    [Tooltip("전체 화면일 때 간격에 곱할 값. skipWhenFullscreen을 끈 상태에서만 쓰인다.")]
    public float fullscreenIntervalMultiplier = 1.3f;

    [Header("조용히 있을 조건")]
    [Tooltip("이 시간만큼 키보드·마우스 입력이 없으면 자리를 비운 것으로 본다 (초)")]
    public float idleSkipSeconds = 180f;
    [Tooltip("전체 화면 앱(게임·영상)을 보고 있으면 말을 걸지 않는다")]
    public bool skipWhenFullscreen = true;

    [Header("같은 화면이 이어질 때")]
    [Tooltip("할 말이 없었을 때 다음 간격에 곱할 값. 1이면 늘리지 않는다.")]
    public float backoffMultiplier = 1.6f;
    [Tooltip("간격이 이보다 길어지지는 않는다 (초)")]
    public float maxInterval = 1200f;

    private float _backoff = 1f;

    private void Start()
    {
        StartCoroutine(Loop());
    }

    private IEnumerator Loop()
    {
        yield return new WaitForSeconds(startDelay);

        while (true)
        {
            float wait = Random.Range(intervalRange.x, intervalRange.y) * _backoff;
            if (!skipWhenFullscreen && capture != null && capture.IsForegroundFullscreen())
                wait *= fullscreenIntervalMultiplier;
            yield return new WaitForSeconds(Mathf.Min(wait, maxInterval));

            if (!enableMonologue) continue;
            if (!ShouldSpeak()) continue;

            talk.LookAtScreen();

            // 응답이 올 때까지 기다렸다가 결과를 본다
            while (talk.IsBusy) yield return null;

            // 할 말이 없었다면(화면 그대로, 차단 창 등) 다음 간격을 늘린다.
            // 정지 화면을 몇 시간씩 띄워두는 경우 요청 자체를 줄여준다.
            _backoff = talk.LastSpoke ? 1f : Mathf.Min(_backoff * backoffMultiplier, 8f);

            // 말풍선이 사라질 때까지 기다린다 (다음 대사가 겹치지 않게)
            while (talk.LastSpoke && GetComponentBubbleShowing()) yield return null;
        }
    }

    private bool GetComponentBubbleShowing()
    {
        return talk != null && talk.bubble != null && talk.bubble.IsShowing;
    }

    private bool ShouldSpeak()
    {
        if (talk == null || capture == null) return false;
        if (talk.IsBusy) return false;
        if (blind != null && blind.IsBlind) return false;
        if (mijin != null && mijin.IsHeld) return false;
        if (mijin != null && mijin.IsSleeping) return false;   // 자는 동안은 말하지 않는다

        if (capture.IdleSeconds() > idleSkipSeconds)
        {
            // 자리를 비운 사이에 혼잣말을 쌓아둘 이유가 없다.
            // 돌아왔을 때 간격이 길어져 있지 않도록 백오프도 되돌린다.
            _backoff = 1f;
            return false;
        }

        if (skipWhenFullscreen && capture.IsForegroundFullscreen())
            return false;

        return true;
    }

    /// <summary>지금 바로 한 마디 하게 한다. 확인용.</summary>
    public void SpeakNow()
    {
        if (ShouldSpeak()) talk.LookAtScreen();
    }
}
