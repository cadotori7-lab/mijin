using System;
using System.IO;
using UnityEngine;

/// <summary>
/// 미진이 설정. 실행 파일 옆 mijin_settings.json에 저장한다.
///
/// 인스펙터 값은 빌드에 굳어 버려서 사용자가 바꿀 수 없다. 메뉴에서 바꾼 값이
/// 다음 실행에도 남으려면 이렇게 파일로 빼야 한다.
/// </summary>
[Serializable]
public class MijinSettings
{
    public float volume = 0.8f;
    public float scale = 0.42f;
    public bool monologueEnabled = true;
    public bool walkEnabled = true;

    // 아직 메뉴에 안 붙은 항목들. 미리 자리를 잡아 두면 기능을 더할 때
    // 저장 파일 형식이 바뀌지 않는다.
    public bool screenBlind = false;    // 가리기
    public bool autoStart = false;      // 윈도우 시작 시 자동 실행

    private const string FileName = "mijin_settings.json";

    private static string FilePath =>
        Path.Combine(Directory.GetParent(Application.dataPath).FullName, FileName);

    public static MijinSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonUtility.FromJson<MijinSettings>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    s.Clamp();
                    return s;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Settings] 불러오기 실패, 기본값으로 시작: {e.Message}");
        }
        return new MijinSettings();
    }

    public void Save()
    {
        try
        {
            Clamp();
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Settings] 저장 실패: {e.Message}");
        }
    }

    /// <summary>파일을 손으로 고쳤을 때 이상한 값이 들어오지 않게 막는다.</summary>
    private void Clamp()
    {
        volume = Mathf.Clamp01(volume);
        scale = Mathf.Clamp(scale, 0.15f, 1.2f);
    }
}
