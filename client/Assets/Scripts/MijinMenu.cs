using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 미진이를 우클릭하면 뜨는 메뉴. 설정을 바꾸고 바로 저장한다.
///
/// 트레이 아이콘은 유니티가 쥐고 있는 윈도우 메시지 루프를 가로채야 해서
/// 손이 많이 간다. 캐릭터 우클릭이 마스코트답기도 하고 구현도 단순하다.
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinMenu : MonoBehaviour
{
    [Header("연결")]
    public MijinCharacter mijin;
    public MijinVoice voice;
    public MijinMonologue monologue;
    public MijinBlind blind;
    public BoxCollider2D body;

    [Header("UI")]
    [Tooltip("메뉴 전체를 담은 오브젝트. 평소에는 꺼져 있다.")]
    public GameObject panel;
    public RectTransform panelRect;
    public Slider volumeSlider;
    public Slider scaleSlider;
    public Toggle monologueToggle;
    public Toggle walkToggle;
    public Toggle blindToggle;
    public Button quitButton;

    private MijinSettings _settings;
    private Camera _cam;
    private bool _building;   // UI를 코드로 채우는 동안 이벤트가 되돌아오는 것 방지

    private void Start()
    {
        _cam = Camera.main;
        _settings = MijinSettings.Load();

        if (panel != null) panel.SetActive(false);
        BindUI();
        ApplyAll();
    }

    private void BindUI()
    {
        _building = true;

        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 1f;
            volumeSlider.value = _settings.volume;
            volumeSlider.onValueChanged.AddListener(v =>
            {
                if (_building) return;
                _settings.volume = v;
                ApplyVolume();
                _settings.Save();
            });
        }

        if (scaleSlider != null)
        {
            scaleSlider.minValue = 0.2f;
            scaleSlider.maxValue = 0.9f;
            scaleSlider.value = _settings.scale;
            scaleSlider.onValueChanged.AddListener(v =>
            {
                if (_building) return;
                _settings.scale = v;
                ApplyScale();
                _settings.Save();
            });
        }

        if (monologueToggle != null)
        {
            monologueToggle.isOn = _settings.monologueEnabled;
            monologueToggle.onValueChanged.AddListener(v =>
            {
                if (_building) return;
                _settings.monologueEnabled = v;
                ApplyMonologue();
                _settings.Save();
            });
        }

        if (walkToggle != null)
        {
            walkToggle.isOn = _settings.walkEnabled;
            walkToggle.onValueChanged.AddListener(v =>
            {
                if (_building) return;
                _settings.walkEnabled = v;
                ApplyWalk();
                _settings.Save();
            });
        }

        if (blindToggle != null)
        {
            blindToggle.isOn = _settings.screenBlind;
            blindToggle.onValueChanged.AddListener(v =>
            {
                if (_building) return;
                _settings.screenBlind = v;
                if (blind != null) blind.SetBlind(v);
                _settings.Save();
            });
        }

        // 단축키로 바꿨을 때 메뉴 토글도 따라오게 한다
        if (blind != null)
        {
            blind.OnChanged += v =>
            {
                _settings.screenBlind = v;
                _settings.Save();
                if (blindToggle == null) return;
                _building = true;
                blindToggle.isOn = v;
                _building = false;
            };
        }

        if (quitButton != null)
            quitButton.onClick.AddListener(Quit);

        _building = false;
    }

    // ───────────────── 설정 적용 ─────────────────

    private void ApplyAll()
    {
        ApplyVolume();
        ApplyScale();
        ApplyMonologue();
        ApplyWalk();

        if (blind != null) blind.RestoreSilently(_settings.screenBlind);
    }

    private void ApplyVolume()
    {
        if (voice != null) voice.volume = _settings.volume;
    }

    private void ApplyScale()
    {
        if (mijin != null) mijin.SetScale(_settings.scale);
    }

    private void ApplyMonologue()
    {
        if (monologue != null) monologue.enableMonologue = _settings.monologueEnabled;
    }

    private void ApplyWalk()
    {
        if (mijin != null) mijin.walkEnabled = _settings.walkEnabled;
    }

    // ───────────────── 열고 닫기 ─────────────────

    private void Update()
    {
        if (Input.GetMouseButtonDown(1))
        {
            Vector3 m = _cam.ScreenToWorldPoint(Input.mousePosition);
            m.z = 0f;

            if (body != null && body.OverlapPoint(m)) Open();
            else Close();
        }

        if (panel != null && panel.activeSelf && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    public void Open()
    {
        if (panel == null) return;

        // 눌린 자리 옆에 띄우되 화면 밖으로 나가지 않게 한다 (Pivot은 (0,0) 기준)
        if (panelRect != null)
        {
            Vector2 size = panelRect.sizeDelta;
            Vector2 p = (Vector2)Input.mousePosition + new Vector2(12f, 12f);

            // 오른쪽이나 위로 넘치면 반대편으로 펼친다
            if (p.x + size.x > Screen.width)  p.x = Input.mousePosition.x - size.x - 12f;
            if (p.y + size.y > Screen.height) p.y = Input.mousePosition.y - size.y - 12f;

            p.x = Mathf.Clamp(p.x, 0f, Screen.width - size.x);
            p.y = Mathf.Clamp(p.y, 0f, Screen.height - size.y);
            panelRect.position = p;
        }

        panel.SetActive(true);
    }

    public void Close()
    {
        if (panel != null) panel.SetActive(false);
    }

    public void Quit()
    {
        _settings.Save();
        Application.Quit();
    }
}
