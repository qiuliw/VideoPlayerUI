using System;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.EventSystems; // 鼠标移动事件，隐藏UI

public class VideoControl : MonoBehaviour, IPointerMoveHandler
{
    public VideoPlayer videoPlayer;
    public TextMeshProUGUI playButtonText;
    public TextMeshProUGUI videoTimeText; // 视频时间
    public Slider videoSlider;            // 进度条
    public TMP_Dropdown videoSpeedDropdown; // 倍速下拉框
    public Slider videoVolumeSlider;        // 音量条
    public RectTransform videoBarRectTransform;
    
    private double videoLength;
    private string videoLengthString;
    private CancellationTokenSource _cts; // 定时任务令牌，处理进度条时间更新显示

    private float volumeShowTimeThreshold = 3;    // 音量条自动消失时间阈值

    private float waitSeconds = 0.3f; // 更新频率
    
    private bool isUserDraggingSlider = false; // 是否正在主动更改进度条，避免与定时任务冲突进度条闪回
    
    [SerializeField] private float barHideDelay = 0.7f;      // 鼠标静止多久隐藏
    [SerializeField] private float barAnimDuration = 0.25f; // 动画时长

    private Vector2 barShownPos;
    private Vector2 barHiddenPos;
    private bool isBarVisible = false;

    // Bar 自动隐藏计时器异步
    private CancellationTokenSource barIdleCts;
    // Bar 移动动画的异步任务
    private CancellationTokenSource barAnimCts;
    // 音量条隐藏异步
    private CancellationTokenSource volumeHideCts;

    void Start()
    {
        if (videoPlayer != null && videoPlayer.clip != null)
        {
            videoLength = videoPlayer.clip.length;
            videoLengthString = TurnTimeString((int)videoLength);
        }
        else
        {
            videoLength = 0;
            videoLengthString = "00:00:00";
            Debug.LogWarning("VideoPlayer 未绑定 VideoClip！");
        }

        videoTimeText.text = $"00:00:00 / {videoLengthString}";
        playButtonText.text = "Play";

        // Slider 初始化
        videoSlider.minValue = 0f;
        videoSlider.maxValue = (float)videoLength;
        videoSlider.value = 0f;

        // 进度条改变视频进度
        videoSlider.onValueChanged.AddListener(OnVideoSliderValueChange);
        // 下拉框改变倍速
        videoSpeedDropdown.onValueChanged.AddListener(OnVideoSpeedValueChange);
        // 音量条变化
        videoVolumeSlider.onValueChanged.AddListener(OnVideoVolumeChange);
        
        // 初始隐藏 Bar
        barShownPos = videoBarRectTransform.anchoredPosition;
        float barHeight = videoBarRectTransform.rect.height;
        float videoSliderHeight = videoSlider.GetComponent<RectTransform>().rect.height;
        barHiddenPos = barShownPos + Vector2.down * (barHeight + videoSliderHeight);
        
        // 初始直接隐藏，不动画
        videoBarRectTransform.anchoredPosition = barHiddenPos;
        isBarVisible = false;

        // 视频播放完成重初始化
        videoPlayer.loopPointReached += OnVideoFinished;
    }

    // UI直接绑定的处理函数习惯+On开头
    public void OnPlayButtonDown()
    {
        if (videoPlayer == null) return;

        if (videoPlayer.isPlaying)
            PauseVideo();
        else
            PlayVideo();
    }

    private void PlayVideo()
    {
        videoPlayer.Play();
        playButtonText.text = "Pause";
        StartUpdateTimeTask();
    }

    private void PauseVideo()
    {
        videoPlayer.Pause();
        playButtonText.text = "Play";
        StopUpdateTimeTask();
    }

    private void StartUpdateTimeTask()
    {
        StopUpdateTimeTask();
        _cts = new CancellationTokenSource();
        _ = UpdateVideoUIAsync(_cts.Token);
    }

    private void StopUpdateTimeTask()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>
    /// UI 更新主循环
    /// </summary>
    private async Task UpdateVideoUIAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UpdateTimeText();   // 更新时间文本
            UpdateSlider();     // 更新进度条

            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token);
        }
    }

    /// <summary>
    /// 更新时间显示
    /// </summary>
    private void UpdateTimeText()
    {
        int currentTime = (int)videoPlayer.time;
        videoTimeText.text =
            $"{TurnTimeString(currentTime)} / {videoLengthString}";
    }

    /// <summary>
    /// 更新进度条（不触发回调，避免死循环）
    /// </summary>
    private void UpdateSlider()
    {
        // 如果正在主动更改进度条，跳过定时更新，防止闪回
        if (isUserDraggingSlider)
            return;
        
        // 设置不触发事件，否则：
        // 视频变化 --> 进度条更新 -|-> 视频更新 --> 进度条更新（死循环）
        videoSlider.SetValueWithoutNotify((float)videoPlayer.time);
    }

    private void OnVideoSliderValueChange(float value)
    {
        // 标记为主动更改进度条
        isUserDraggingSlider = true;
        
        // 解决未Play过拖动进度条不显示帧画面。如果未Prepare过，临时Play以刷新画面，对于低频UI操作性能损耗可以接受
        bool wasPlaying = videoPlayer.isPlaying;
        if (!wasPlaying)
            videoPlayer.Play();

        videoPlayer.time = value;

        // 立刻 Pause，保持原有状态
        if (!wasPlaying)
            videoPlayer.Pause();
        
        // 更新时间显示，即使不在播放
        videoTimeText.text =
            $"{TurnTimeString((int)value)} / {videoLengthString}";
        
        // 主动更改完成，允许定时更新
        isUserDraggingSlider = false;
    }

    private void OnVideoSpeedValueChange(int value)
    {
        float speed = 1;
        switch (value)
        {
            case 0:
                speed = 4;
                break;
            case 1:
                speed = 2;
                break;
        }
        videoPlayer.playbackSpeed = speed;
    }

    private void OnVideoVolumeChange(float value)
    {
        videoPlayer.SetDirectAudioVolume(0, value);

        // 显示音量条
        videoVolumeSlider.gameObject.SetActive(true);

        // 重置自动隐藏计时
        RestartVolumeHideTimer();
    }
    
    // 音量条隐藏动画异步调用
    private void RestartVolumeHideTimer()
    {
        volumeHideCts?.Cancel();
        volumeHideCts?.Dispose();

        volumeHideCts = new CancellationTokenSource();
        _ = AutoHideVolumeSliderAsync(volumeHideCts.Token);
    }
    
    // 音量条动画异步
    private async Task AutoHideVolumeSliderAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(volumeShowTimeThreshold), token);

            videoVolumeSlider.gameObject.SetActive(false);
        }
        catch (TaskCanceledException)
        {
            // 取消时什么都不做
        }
    }

    private void OnVideoFinished(VideoPlayer vp)
    {
        playButtonText.text = "Play";
        StopUpdateTimeTask();
    }

    private string TurnTimeString(int totalSecond)
    {
        int hour = totalSecond / 3600;
        int minute = (totalSecond % 3600) / 60;
        int second = totalSecond % 60;
        return $"{hour:D2}:{minute:D2}:{second:D2}";
    }

    private void OnDestroy()
    {
        StopUpdateTimeTask();
        if (videoPlayer != null)
            videoPlayer.loopPointReached -= OnVideoFinished;
        
        
        // Bar 显隐计时器相关资源释放
        barIdleCts?.Cancel();  // 取消延迟隐藏任务
        barIdleCts?.Dispose(); // 释放 CancellationTokenSource 资源

        // Bar 动画任务相关资源释放
        barAnimCts?.Cancel();  // 取消正在进行的动画
        barAnimCts?.Dispose(); // 释放 CancellationTokenSource 资源
    }

    public void OnPointerMove(PointerEventData eventData)
    {
        // 重启自动隐藏计时器
        RestartBarIdleTimer();

        // 如果 Bar 当前不可见，则显示
        if (!isBarVisible)
        {
            ShowVideoBarAsync();
        }
    }
    
    // 重启 Bar 自动隐藏计时器
    private void RestartBarIdleTimer()
    {
        // 取消并释放上一个计时器
        barIdleCts?.Cancel();
        barIdleCts?.Dispose();

        // 新建 CancellationTokenSource 并启动延迟隐藏异步任务
        barIdleCts = new CancellationTokenSource();
        _ = AutoHideBarAsync(barIdleCts.Token); // 启动任务，不阻塞主线程
    }

    private async Task AutoHideBarAsync(CancellationToken token)
    {
        try
        {
            // 延迟一定时间后执行隐藏
            await Task.Delay(TimeSpan.FromSeconds(barHideDelay), token);

            // 如果 Bar 当前仍然可见，则隐藏
            if (isBarVisible)
            {
                HideVideoBarAsync();
            }
        }
        catch (TaskCanceledException)
        {
            // 正常取消时会抛出异常，这里无需处理
        }
    }
    // 显示 Bar
    private void ShowVideoBarAsync()
    {
        isBarVisible = true;                 // 标记 Bar 状态为可见
        StartBarAnimationAsync(barShownPos); // 启动动画移动到显示位置
    }

    // 隐藏 Bar
    private void HideVideoBarAsync()
    {
        isBarVisible = false;                // 标记 Bar 状态为不可见
        StartBarAnimationAsync(barHiddenPos);// 启动动画移动到隐藏位置
    }

    // 启动 Bar 移动动画
    private void StartBarAnimationAsync(Vector2 targetPos)
    {
        // 取消并释放之前的动画任务，保证动画不会叠加
        barAnimCts?.Cancel();
        barAnimCts?.Dispose();

        // 新建 CancellationTokenSource 并启动动画异步任务
        barAnimCts = new CancellationTokenSource();
        _ = AnimateBarAsync(targetPos, barAnimCts.Token);
    }

    // 异步执行 Bar 移动动画
    private async Task AnimateBarAsync(Vector2 targetPos, CancellationToken token)
    {
        Vector2 startPos = videoBarRectTransform.anchoredPosition; // 起始位置
        float elapsed = 0f; // 记录动画已用时间

        while (elapsed < barAnimDuration)
        {
            // 如果任务被取消，立即退出
            if (token.IsCancellationRequested)
                return;

            // 增加时间
            elapsed += Time.deltaTime;

            // 计算动画插值（0~1）
            float t = Mathf.Clamp01(elapsed / barAnimDuration);

            // Lerp 移动 Bar
            videoBarRectTransform.anchoredPosition =
                Vector2.Lerp(startPos, targetPos, t);

            // 等待下一帧（等价于 yield return null）
            await Task.Yield();
        }

        // 确保最终位置精确
        videoBarRectTransform.anchoredPosition = targetPos;
    }

    // 显示音量条
    public void OnVolumeButtonDown()
    {
        if (videoVolumeSlider == null) return;

        // 切换音量条激活状态：显示/隐藏
        bool isVolumeActive = videoVolumeSlider.gameObject.activeSelf;
        if (isVolumeActive)
        {
            // 直接隐藏音量条
            videoVolumeSlider.gameObject.SetActive(false);
            // 取消当前的隐藏计时器
            volumeHideCts?.Cancel();
            volumeHideCts?.Dispose();
            volumeHideCts = null;
        }
        else
        {
            // 显示音量条
            videoVolumeSlider.gameObject.SetActive(true);
            // 重置自动隐藏计时
            RestartVolumeHideTimer();
        }
    }
}
