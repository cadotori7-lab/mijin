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

    public void OnPoked()
    {
        // 한동안 안 찔렀으면 다시 센다
        if (Time.time - _lastPokeTime > 60f) _pokeCount = 0;
        _lastPokeTime = Time.time;
        _pokeCount++;

        Talk("chat", $"(파트너님이 미진이를 쿡 찌른다. 이번이 연속 {_pokeCount}번째다.)");
    }

    /// <summary>
    /// 프록시에 요청하고 받은 대사를 말풍선에 띄운다.
    /// kind: "chat"(말 걸기) | "monologue"(혼잣말) | "screen"(화면 반응)
    /// </summary>
    public void Talk(string kind, string text, string imageBase64 = null)
    {
        if (IsBusy) return;
        StartCoroutine(TalkRoutine(kind, text, imageBase64));
    }
    /// <summary>맨 앞의 감정 태그를 떼어낸다. 태그는 TTS에만 보내고 말풍선에는 안 띄운다.</summary>
    private static string StripEmotionTag(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*\[[^\]]{1,20}\]\s*");
        return m.Success ? text.Substring(m.Length).Trim() : text;
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
