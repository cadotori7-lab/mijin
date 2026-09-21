using System.Collections;
using UnityEngine;

/// <summary>
/// 자리 비움을 감지해 잠들고 깨운다. 판단만 하고, 실제 자세 전환은 MijinCharacter가 갖고 있다.
///
/// IdleSeconds()는 GetLastInputInfo 기반이라 마우스를 살짝 스치기만 해도 0으로 떨어진다.
/// 그걸 그대로 깨우는 조건으로 쓰면 지나가다 스친 것만으로 벌떡 깨서 잠든 의미가 없다.
/// 그렇다고 입력으로 아예 안 깨게 하면, 자리를 3분 비우고 두 시간을 작업해도 계속 자며
/// 화면을 보고 말 거는 기능이 통째로 꺼진 상태가 된다. 그래서 끄는 게 아니라 둔하게
/// 만든다 - 누적 카운터(_awakeCredit)로 "한동안 계속 입력이 있었는지"를 본다.
///
/// 찌르기·말 걸기는 이 카운터를 거치지 않고 즉시 깬다 (MijinCharacter.WakeUp 쪽 경로).
/// 기다리기 싫으면 찌르면 된다.
///
/// 붙이는 곳: Mijin 오브젝트
/// </summary>
public class MijinSleep : MonoBehaviour
{
    [Header("연결")]
    public MijinCharacter mijin;
    public MijinScreenCapture capture;
    public MijinTalk talk;

    [Header("잠들기")]
    [Tooltip("이만큼 입력이 없으면 잠든다. MijinMonologue.idleSkipSeconds(기본 180)와 맞춰 두면 " +
             "말을 멈추는 시점과 잠드는 시점이 같아진다. 조금 크게 두면 조용해진 뒤 한 박자 있다가 잠드는 느낌이 난다.")]
    public float sleepAfterSeconds = 180f;
    public float pollSeconds = 1f;

    [Header("깨어나기")]
    [Tooltip("자는 중에 이만큼 입력이 이어져야 깬다. 지나가다 마우스를 한 번 건드린 정도로는 안 깨고, " +
             "실제로 앉아서 일하기 시작하면 깬다.")]
    public float wakeAfterActiveSeconds = 5f;
    [Tooltip("입력이 이어지는 것으로 볼 최대 공백. 타자 중 잠깐 멈추는 것까지 끊김으로 보면 영영 안 깬다.")]
    public float wakeIdleGraceSeconds = 3f;

    private float _awakeCredit;

    private void Start()
    {
        StartCoroutine(Loop());
    }

    private IEnumerator Loop()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollSeconds);
            if (mijin == null || capture == null) continue;

            float idle = capture.IdleSeconds();

            if (!mijin.IsSleeping)
            {
                // 응답을 기다리는 중에 잠들면 대사가 자는 입에서 나온다
                if (idle > sleepAfterSeconds && (talk == null || !talk.IsBusy))
                {
                    mijin.EnterSleep();
                    _awakeCredit = 0f;
                }
            }
            else
            {
                _awakeCredit = idle < wakeIdleGraceSeconds ? _awakeCredit + pollSeconds : 0f;
                if (_awakeCredit >= wakeAfterActiveSeconds) mijin.WakeUp();
            }
        }
    }
}
