using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using Unity.Collections;

[System.Serializable]
public class SelectionData
{
    public List<int> selected; 
}

public class ARPoseTracker : MonoBehaviour
{
    [Header("AR 核心组件")]
    public Transform arCamera;
    public ARSession arSession;
    public ARCameraManager cameraManager;

    [Header("主 UI")]
    public Text debugText;
    public Button recordButton;
    public Text buttonText;

    [Header("人工审核 UI")]
    public GameObject reviewPanel;       
    public RawImage previewImage;        
    public Text imageIndexText;          
    public Text keepStatusText;          
    public Button btnPrev;               
    public Button btnNext;               
    public Button btnToggleKeep;         
    public Button btnSubmit;
    public Button btnCancel;             // 新增：退出/重拍按钮

    [Header("网络设置")]
    public string serverURL = "http://10.102.33.100:5000";
    public float sendInterval = 0.5f;

    private float timer = 0f;
    private bool isSending = false;
    private bool isRecording = false;
    private Texture2D cameraTexture;

    private List<Texture2D> downloadedPreviews = new List<Texture2D>();
    private List<bool> keepFlags = new List<bool>();
    private int currentPreviewIndex = 0;

    void Start()
    {
        if (reviewPanel != null) reviewPanel.SetActive(false);
        recordButton.onClick.AddListener(ToggleRecording);
        buttonText.text = "开始录制";

        btnPrev.onClick.AddListener(() => ChangePreview(-1));
        btnNext.onClick.AddListener(() => ChangePreview(1));
        btnToggleKeep.onClick.AddListener(ToggleKeepStatus);
        btnSubmit.onClick.AddListener(SubmitSelection);
        
        // 绑定退出按钮
        if (btnCancel != null) btnCancel.onClick.AddListener(CancelReview);
    }

    void ToggleRecording()
    {
        isRecording = !isRecording;

        if (isRecording)
        {
            buttonText.text = "结束录制并预处理";
            buttonText.color = Color.red;
            StartCoroutine(SendCommand("/start_record"));
        }
        else
        {
            recordButton.gameObject.SetActive(false); 
            UpdateUI("正在让服务器进行 AI 抠图，请耐心等待...", Color.yellow);
            StartCoroutine(RequestPreprocess());
        }
    }

    // ========== 更新：全局取消与重置 ==========
    void CancelReview()
    {
        // 1. 强行打断所有的前端状态
        isRecording = false;
        isSending = false;
        
        // 2. 隐藏审核面板（如果它正开着的话）
        if (reviewPanel != null) reviewPanel.SetActive(false);
        
        // 3. 恢复主界面的录制按钮
        recordButton.gameObject.SetActive(true);
        buttonText.text = "开始录制";
        buttonText.color = Color.white;
        
        UpdateUI("已强制重置，可随时开始新的录制", Color.white);

        // 4. 通知服务器中断当前任务并清空缓存
        StartCoroutine(SendCommand("/cancel_review"));
    }
    // ==========================================

    void Update()
    {
        if (ARSession.state != ARSessionState.SessionTracking) return;

        if (arCamera != null && isRecording)
        {
            Vector3 pos = arCamera.position;
            Vector3 rot = arCamera.rotation.eulerAngles;
            UpdateUI($"[录制中] \nPos: {pos.x:F2}, {pos.y:F2}, {pos.z:F2}", Color.green);

            timer += Time.deltaTime;
            if (timer >= sendInterval && !isSending)
            {
                timer = 0f;
                if (cameraManager.TryAcquireLatestCpuImage(out UnityEngine.XR.ARSubsystems.XRCpuImage image))
                {
                    StartCoroutine(ProcessAndSendData(image, pos, rot));
                }
            }
        }
    }

    IEnumerator RequestPreprocess()
    {
        using (UnityWebRequest www = UnityWebRequest.PostWwwForm(serverURL + "/preprocess", ""))
        {
            www.timeout = 120; 
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string json = www.downloadHandler.text;
                int totalImages = int.Parse(json.Split("\"total_images\":")[1].Split('}')[0]);
                
                UpdateUI($"抠图完成，共 {totalImages} 张，准备下载预览...", Color.green);
                StartCoroutine(DownloadAllPreviews(totalImages));
            }
            else
            {
                UpdateUI("抠图失败: " + www.error, Color.red);
                // 失败时也把重拍按钮亮出来
                recordButton.gameObject.SetActive(true);
                buttonText.text = "开始录制";
                buttonText.color = Color.white;
            }
        }
    }

    IEnumerator DownloadAllPreviews(int count)
    {
        downloadedPreviews.Clear();
        keepFlags.Clear();

        for (int i = 0; i < count; i++)
        {
            UpdateUI($"正在下载预览图 {i + 1}/{count}...", Color.white);
            using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(serverURL + $"/get_preview/{i}"))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                {
                    downloadedPreviews.Add(DownloadHandlerTexture.GetContent(www));
                    keepFlags.Add(true); 
                }
            }
        }

        reviewPanel.SetActive(true);
        currentPreviewIndex = 0;
        RefreshReviewUI();
    }

    void ChangePreview(int step)
    {
        currentPreviewIndex += step;
        if (currentPreviewIndex < 0) currentPreviewIndex = downloadedPreviews.Count - 1;
        if (currentPreviewIndex >= downloadedPreviews.Count) currentPreviewIndex = 0;
        RefreshReviewUI();
    }

    void ToggleKeepStatus()
    {
        keepFlags[currentPreviewIndex] = !keepFlags[currentPreviewIndex];
        RefreshReviewUI();
    }

    void RefreshReviewUI()
    {
        if (downloadedPreviews.Count == 0) return;
        previewImage.texture = downloadedPreviews[currentPreviewIndex];
        imageIndexText.text = $"当前: {currentPreviewIndex + 1} / {downloadedPreviews.Count}";
        
        bool isKept = keepFlags[currentPreviewIndex];
        keepStatusText.text = isKept ? "✅ 状态: 将保留此图" : "❌ 状态: 已剔除此图";
        keepStatusText.color = isKept ? Color.green : Color.red;
    }

    void SubmitSelection()
    {
        reviewPanel.SetActive(false);
        UpdateUI("已提交生成，服务器正在全力重建 Mesh (需耐心等待)！", Color.yellow);

        SelectionData data = new SelectionData();
        data.selected = new List<int>();
        for (int i = 0; i < keepFlags.Count; i++)
        {
            if (keepFlags[i]) data.selected.Add(i);
        }

        string jsonPayload = JsonUtility.ToJson(data);
        StartCoroutine(SendGenerateCommand(jsonPayload));
    }

    IEnumerator SendGenerateCommand(string json)
    {
        UnityWebRequest www = new UnityWebRequest(serverURL + "/generate", "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
        www.uploadHandler = new UploadHandlerRaw(bodyRaw);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        
        www.timeout = 600; 

        yield return www.SendWebRequest();

        if (www.result == UnityWebRequest.Result.Success)
        {
            UpdateUI("重建完成！请在服务器 output 文件夹查看 Mesh", Color.green);
            recordButton.gameObject.SetActive(true);
            buttonText.text = "开始新录制";
            buttonText.color = Color.white;
        }
        else
        {
            UpdateUI("生成失败: " + www.error, Color.red);
            recordButton.gameObject.SetActive(true);
            buttonText.text = "开始新录制";
            buttonText.color = Color.white;
        }
    }

    IEnumerator ProcessAndSendData(UnityEngine.XR.ARSubsystems.XRCpuImage image, Vector3 pos, Vector3 rot)
    {
        isSending = true;
        var conversionParams = new UnityEngine.XR.ARSubsystems.XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, image.width, image.height),
            outputDimensions = new Vector2Int(image.width, image.height), // 已恢复全分辨率
            outputFormat = TextureFormat.RGBA32,
            transformation = UnityEngine.XR.ARSubsystems.XRCpuImage.Transformation.MirrorY
        };

        int size = image.GetConvertedDataSize(conversionParams);
        var buffer = new NativeArray<byte>(size, Allocator.Temp);
        image.Convert(conversionParams, buffer);
        image.Dispose();

        if (cameraTexture == null || cameraTexture.width != conversionParams.outputDimensions.x)
            cameraTexture = new Texture2D(conversionParams.outputDimensions.x, conversionParams.outputDimensions.y, TextureFormat.RGBA32, false);

        cameraTexture.LoadRawTextureData(buffer);
        cameraTexture.Apply();
        buffer.Dispose();

        byte[] imageBytes = cameraTexture.EncodeToJPG(75);
        WWWForm form = new WWWForm();
        form.AddField("pos_x", pos.x.ToString("F4"));
        form.AddField("pos_y", pos.y.ToString("F4"));
        form.AddField("pos_z", pos.z.ToString("F4"));
        form.AddField("rot_x", rot.x.ToString("F4"));
        form.AddField("rot_y", rot.y.ToString("F4"));
        form.AddField("rot_z", rot.z.ToString("F4"));
        form.AddBinaryData("image", imageBytes, "frame.jpg", "image/jpeg");

        using (UnityWebRequest www = UnityWebRequest.Post(serverURL + "/upload", form))
        {
            yield return www.SendWebRequest();
        }
        isSending = false;
    }

    IEnumerator SendCommand(string endpoint)
    {
        using (UnityWebRequest www = UnityWebRequest.PostWwwForm(serverURL + endpoint, ""))
            yield return www.SendWebRequest();
    }
    
    void UpdateUI(string text, Color color)
    {
        if (debugText != null) { debugText.text = text; debugText.color = color; }
    }
}