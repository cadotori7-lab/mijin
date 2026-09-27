using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 프록시 서버(/mijin/chat)와 통신해 대사를 받아 말풍선에 띄운다.
///
/// 지금은 클릭에만 반응한다. 나중에 자율 혼잣말과 화면 캡처도 이 클래스에
/// SendToProxy를 재사용해 얹으면 된다.
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinTalk : MonoBehaviour
{
    [Header("연결")]
    public SpeechBubble bubble;
    public MijinCharacter mijin;

    [Header("서버")]
    public string proxyUrl = "http://127.0.0.1:8000/mijin/chat";
    [Tooltip("응답을 기다리는 최대 시간 (초). 넘으면 포기한다.")]
    public int timeoutSeconds = 30;
    [Header("화면")]
    public MijinScreenCapture capture;
    public MijinBlind blind;
    [Tooltip("이 키를 누르면 화면을 캡처해 반응한다. 확인용.")]
    public KeyCode screenTestKey = KeyCode.F2;
    [Header("대사")]
    [Tooltip("미진이를 클릭했을 때 사용자 발화 대신 보낼 문장")]
    [TextArea]
    public string pokeText = "(파트너님이 미진이를 쿡 찌르며 장난을 건다.)";
    [Tooltip("서버에 연결되지 않을 때 띄울 대사. 비워두면 아무 말도 하지 않는다.")]
    public string offlineLine = "지금은 데이터 연결이 끊겼어요. 미진이는 잠깐 혼자 있을게요.";

    /// <summary>요청을 보내고 응답을 기다리는 중인지. 중복 호출을 막는다.</summary>
    public bool IsBusy { get; private set; }
    /// <summary>마지막 요청에서 실제로 말을 했는지. 혼잣말 간격 조절에 쓴다.</summary>
    public bool LastSpoke { get; private set; }
    /// <summary>음성 재생을 담당하는 컴포넌트.</summary>
    public MijinVoice voice;

    // ───────────────── 외부에서 부르는 것들 ─────────────────

    /// <summary>미진이를 클릭했을 때. MijinDrag의 onClick에 연결한다.</summary>
    private int _pokeCount;
    private float _lastPokeTime;
    private Coroutine _emotionRevertRoutine;

    public void OnPoked()
    {
        // PlayPoke()가 안에서 WakeUp()을 먼저 부르므로, 자고 있었는지는 그 전에 읽어둬야 한다.
        bool wasSleeping = mijin != null && mijin.IsSleeping;

        // 한동안 안 찔렀거나 자다가 깼으면 다시 센다 - 자다 깬 건 짜증의 누적이 아니다
        if (wasSleeping || Time.time - _lastPokeTime > 60f) _pokeCount = 0;
        _lastPokeTime = Time.time;
        _pokeCount++;

        // Talk()는 IsBusy면 조용히 반환하지만 모션은 응답 대기 중에도 나와야 한다 —
        // 찔렀는데 반응이 없으면 클릭이 씹힌 것처럼 느껴진다.
        if (mijin != null) mijin.PlayPoke();

        string text = wasSleeping
            ? "(파트너님이 자고 있던 미진이를 쿡 찔러 깨운다.)"
            : $"(파트너님이 미진이를 쿡 찌른다. 이번이 연속 {_pokeCount}번째다.)";
        Talk("event", text);
    }

    /// <summary>
    /// 프록시에 요청하고 받은 대사를 말풍선에 띄운다.
    /// kind: "chat"(말 걸기) | "monologue"(혼잣말) | "screen"(화면 반응) | "event"(찌르기·블라인드 등 클라이언트가 만든 지문)
    /// </summary>
    public void Talk(string kind, string text, string imageBase64 = null)
    {
        if (IsBusy) return;
        if (mijin != null) mijin.WakeUp();   // 말을 걸거나 이벤트가 오면 깬다
        StartCoroutine(TalkRoutine(kind, text, imageBase64));
    }
    /// <summary>맨 앞의 감정 태그를 떼어낸다. 태그는 TTS에만 보내고 말풍선에는 안 띄운다.</summary>
    private static string StripEmotionTag(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*\[[^\]]{1,20}\]\s*");
        return m.Success ? text.Substring(m.Length).Trim() : text;
    }

    // StripEmotionTag와 같은 태그를 보되, 내용을 캡처 그룹으로 꺼낸다 (표정 매핑용).
    // 서버(mijin.py의 EMOTION_TAG_PATTERN/KNOWN_EMOTION_TAGS)가 curious/excited/smug/upset
    // 네 개로 이미 닫아 뒀으므로 여기서도 그 넷만 안다.
    private static readonly System.Text.RegularExpressions.Regex EmotionTagPattern =
        new System.Text.RegularExpressions.Regex(@"^\s*\[([^\]]{1,20})\]");

    /// <summary>
    /// 대사 맨 앞 감정 태그로 눈만 바꾼다. 입은 립싱크(SetMouthLevel)가 말하는 동안
    /// 계속 몰고 있으니 건드리지 않는다 - SetExpression(eyes, null, hold)로 두면
    /// mouth 인자가 null이라도 알아서 건드리지 않는다.
    ///
    /// 정확한 재생 길이를 여기서는 몰라서 글자 수로 대략 추정한다(한국어 발화
    /// 속도 대략 5자/초). 조금 넘치더라도 말이 끝난 뒤 잠깐 표정이 남는 정도라
    /// CloseMouth처럼 눈에 띄게 어긋나진 않는다.
    /// </summary>
    private void ApplyEmotionExpression(string rawResult)
    {
        if (mijin == null) return;
        var m = EmotionTagPattern.Match(rawResult);
        if (!m.Success) return;

        string tag = m.Groups[1].Value.Trim().ToLowerInvariant();
        Sprite eyes;
        switch (tag)
        {
            case "excited": eyes = mijin.eyesExcited; break;
            case "smug":    eyes = mijin.eyesSmug;    break;
            case "upset":   eyes = mijin.eyesUpset;   break;
            default:        eyes = mijin.eyesOpen;    break;   // curious 등 - 기본 눈
        }

        float holdSeconds = Mathf.Clamp(rawResult.Length / 5f + 1.5f, 2f, 15f);
        mijin.SetExpression(eyes, null, holdSeconds);

        // SetExpression의 holdSeconds는 입(_mouthHoldUntil)만 막을 뿐, 눈은 아무도
        // 되돌려 주지 않는다 - 여기서 직접 타이머를 걸어야 holdSeconds가 지나면
        // 눈이 기본으로 돌아온다 (찌르기의 RevertPokeExpression과 같은 이유).
        if (_emotionRevertRoutine != null) StopCoroutine(_emotionRevertRoutine);
        _emotionRevertRoutine = StartCoroutine(RevertEmotionExpression(holdSeconds));
    }

    private IEnumerator RevertEmotionExpression(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        _emotionRevertRoutine = null;
        // 그 사이 들렸거나 자거나 다른 연출(찌르기·착지)이 자세를 가져갔으면 그쪽이 이긴다.
        // !MouthHeld는 그 사이 찌르기가 이 감정 표정보다 더 긴 홀드를 새로 걸어놓은
        // 경우를 막는다 - 안 그러면 이 타이머가 먼저 끝나며 아직 유효한 찌르기 표정을 지운다.
        if (mijin != null && mijin.CanAct() && !mijin.MouthHeld) mijin.ResetExpression();
    }

    private IEnumerator TalkRoutine(string kind, string text, string imageBase64)
    {
        IsBusy = true;
        bubble.Show("...");   // 기다리는 동안 생각하는 표시
        if (mijin != null) mijin.SetFocused(true);

        string result = null;
        yield return SendToProxy(kind, text, imageBase64, r => result = r);

        if (mijin != null) mijin.SetFocused(false);

         if (!string.IsNullOrEmpty(result))
        {
            bubble.Show(StripEmotionTag(result));   // 말풍선에는 태그 없이
            LastSpoke = true;
            ApplyEmotionExpression(result);         // 태그로 눈만 바꾼다 (말하는 동안 유지)
            // 음성에는 태그째로
            if (voice != null) voice.Speak(result);
        }
        else
        {
            LastSpoke = false;
            if (result == null && !string.IsNullOrEmpty(offlineLine))
                bubble.Show(offlineLine);
            else
                bubble.Hide();
        }

        IsBusy = false;
    }

    /// <summary>
    /// 프록시 호출. 응답의 text를 콜백으로 넘긴다.
    /// 실패하거나 서버가 건너뛰었으면 null이 넘어간다.
    /// </summary>
    private IEnumerator SendToProxy(
        string kind, string text, string imageBase64, Action<string> onDone)
    {
        var body = new ChatRequest
        {
            kind = kind,
            text = text ?? "",
            image = imageBase64 ?? "",
            window = capture != null ? capture.ActiveWindowTitle() : "",
        };
        byte[] raw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));

        using (var req = new UnityWebRequest(proxyUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = timeoutSeconds;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MijinTalk] 프록시 통신 실패: {req.error}");
                onDone(null);
                yield break;
            }

            ChatResponse res;
            try
            {
                res = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MijinTalk] 응답 해석 실패: {e.Message}");
                onDone(null);
                yield break;
            }

            // status가 skipped면 서버가 일부러 건너뛴 것이다 (화면 변화 없음 등).
            // 이때는 아무 말도 하지 않는 게 맞다.
            if (res == null || res.status != "ok" || string.IsNullOrWhiteSpace(res.text))
            {
                onDone("");   // 서버는 응답했지만 할 말이 없는 경우 (null = 통신 실패)
                yield break;
            }

            onDone(res.text);
        }
    }
    
    private void Update()
    {
        if (Input.GetKeyDown(screenTestKey)) LookAtScreen();
    }

    /// <summary>화면을 캡처해 프록시에 보내고 반응을 띄운다.</summary>
    public void LookAtScreen()
    {
        if (blind != null && blind.IsBlind) return;   // 가린 동안은 캡처 자체를 하지 않는다
        if (IsBusy || capture == null) return;
        Debug.Log($"[MijinTalk] 활성 창: \"{capture.ActiveWindowTitle()}\"");
        string shot = capture.CaptureBase64();
        if (string.IsNullOrEmpty(shot))
        {
            Debug.LogWarning("[MijinTalk] 캡처 실패");
            return;
        }
        Talk("screen", null, shot);
        
    }

    // JsonUtility는 필드 이름이 JSON 키와 같아야 한다.
    [Serializable]
    private class ChatRequest
    {
        public string kind;
        public string text;
        public string image;
        public string window;
    }

    [Serializable]
    private class ChatResponse
    {
        public string status;
        public string text;
    }
}
