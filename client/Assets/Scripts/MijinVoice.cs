using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 대사를 Fish Audio로 음성 합성해 재생하고, 재생 음량에 맞춰 입을 움직인다.
///
/// 프록시가 아니라 본체에서 직접 부른다. 대사는 말풍선에 먼저 뜨고 음성이
/// 도착하는 대로 재생되므로, 프록시에서 음성까지 만들어 오는 것보다 반응이 빠르다.
///
/// 나중에 다른 TTS로 갈아 끼울 때는 Speak(text) 하나만 유지하면 되도록
/// 통신 부분을 이 클래스 안에 가둬 뒀다.
///
/// 붙이는 곳: Mijin 오브젝트 (AudioSource와 같이)
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class MijinVoice : MonoBehaviour
{
    [Header("연결")]
    public MijinCharacter mijin;
    public SpeechBubble bubble;

    [Header("서버")]
    public string apiUrl = "https://api.fish.audio/v1/tts";
    [Tooltip("Fish Audio 모델 이름. 요청 헤더로 나간다.")]
    public string modelId = "s2.1-pro-free";
    [Tooltip("쓸 목소리의 reference_id. 비우면 모델 기본 목소리로 나온다.")]
    public string referenceId = "c460cd83587e4b828ff0938b2313c406";
    public int timeoutSeconds = 30;

    [Header("API 키")]
    [Tooltip("실행 파일 옆의 이 파일에서 키를 읽는다. 빌드에 키가 박히지 않는다.")]
    public string apiKeyFileName = "fish_key.txt";
    [Tooltip("파일이 없을 때만 쓰는 예비 값. 배포할 빌드에는 비워 둘 것.")]
    public string apiKeyFallback = "";

    [Header("재생")]
    [Range(0f, 1f)] public float volume = 0.8f;

    [Header("립싱크")]
    [Tooltip("클수록 입이 크게 벌어진다. 목소리가 작으면 올린다.")]
    public float mouthSensitivity = 6f;
    [Tooltip("입 움직임을 부드럽게 하는 정도. 높을수록 빠르게 따라간다.")]
    public float mouthResponse = 18f;

    private AudioSource _audio;
    private string _apiKey = "";
    private Coroutine _current;
    private readonly float[] _samples = new float[256];
    private float _mouthLevel;
    private bool _wasPlaying;

    private void Awake()
    {
        _audio = GetComponent<AudioSource>();
        _audio.playOnAwake = false;
        _apiKey = LoadApiKey();
        Debug.Log($"[Voice] 시작. 키 {_apiKey.Length}자");

        if (string.IsNullOrEmpty(_apiKey))
            Debug.LogWarning($"[Voice] API 키가 없습니다. 실행 파일 옆에 {apiKeyFileName}을 두세요. 음성 없이 동작합니다.");
    }

    private string LoadApiKey()
    {
        try
        {
            // 빌드에서는 실행 파일 폴더, 에디터에서는 프로젝트 폴더를 본다.
            string dir = Application.isEditor
                ? Directory.GetParent(Application.dataPath).FullName
                : Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(dir, apiKeyFileName);
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Voice] 키 파일 읽기 실패: {e.Message}");
        }
        return apiKeyFallback.Trim();
    }

    // ───────────────── 립싱크 ─────────────────
    private void Update()
    {
        if (mijin == null) return;

        if (_audio.isPlaying)
        {
            // 재생 중인 소리의 크기(RMS)를 재서 입 벌림 정도로 쓴다
            _audio.GetOutputData(_samples, 0);
            float sum = 0f;
            for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / _samples.Length);

            float target = Mathf.Clamp01(rms * mouthSensitivity);
            _mouthLevel = Mathf.Lerp(_mouthLevel, target, Time.deltaTime * mouthResponse);
            mijin.SetMouthLevel(_mouthLevel);
            _wasPlaying = true;
        }
        else if (_wasPlaying)
        {
            _mouthLevel = 0f;
            mijin.CloseMouth();
            _wasPlaying = false;
        }
    }

    // ───────────────── 외부에서 부르는 것들 ─────────────────

    /// <summary>대사를 음성으로 만들어 재생한다. 말하던 중이면 새 대사로 바꾼다.</summary>
    public void Speak(string text)
    {   
        Debug.Log($"[Voice] Speak 호출: 키 {(_apiKey.Length > 0 ? "있음" : "없음")}, 길이 {text?.Length}");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(_apiKey)) return;
        if (_current != null) StopCoroutine(_current);
        _audio.Stop();
        _current = StartCoroutine(SpeakRoutine(text));
    }

    /// <summary>말하기를 즉시 멈춘다.</summary>
    public void StopSpeaking()
    {
        if (_current != null) StopCoroutine(_current);
        _current = null;
        _audio.Stop();
        if (mijin != null) mijin.CloseMouth();
    }

    private IEnumerator SpeakRoutine(string text)
    {
        string json = JsonUtility.ToJson(new TtsRequest
        {
            text = text,
            reference_id = referenceId ?? "",
            format = "wav",
            latency = "normal",
        });

        using (var req = new UnityWebRequest(apiUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            // WAV는 mp3보다 용량이 크지만 유니티에서 디코딩이 가장 잘 된다.
            req.downloadHandler = new DownloadHandlerAudioClip(apiUrl, AudioType.WAV);
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + _apiKey);
            if (!string.IsNullOrEmpty(modelId))
                req.SetRequestHeader("model", modelId);
            req.timeout = timeoutSeconds;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Voice] 음성 합성 실패: {req.responseCode} {req.error}");
                _current = null;
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null || clip.length <= 0f)
            {
                Debug.LogWarning("[Voice] 받은 음성을 재생할 수 없습니다.");
                _current = null;
                yield break;
            }

            // 음성이 말풍선보다 길면 말하는 도중에 말풍선이 먼저 사라진다.
            // 재생 길이만큼 말풍선을 붙잡아 둔다.
            if (bubble != null) bubble.ExtendHold(clip.length + 0.4f);

            _audio.clip = clip;
            _audio.volume = volume;
            _audio.Play();
        }

        _current = null;
    }

    // JsonUtility는 필드 이름이 JSON 키와 같아야 한다.
    [Serializable]
    private class TtsRequest
    {
        public string text;
        public string reference_id;
        public string format;
        public string latency;
    }
}
