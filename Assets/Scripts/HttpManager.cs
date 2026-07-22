using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// HttpClient lifecycle management best practices:
// https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines#recommended-use

public class HttpManager : MonoBehaviour
{
    public static HttpManager httpMng { get; private set; }

    void Awake()
    {
        if (httpMng != null && httpMng != this)
        {
            Destroy(this);
            return;
        }
        httpMng = this;
    }

    [SerializeField] private string baseUrl = "https://jsonplaceholder.typicode.com";
    static readonly HttpClient client = new HttpClient(); // UNO, statico, riusato

    CancellationTokenSource cts;   // per fermare il worker in modo pulito
    Task workerTask;               // riferimento al task, per aspettarlo alla chiusura

    byte[] rgbTexture;
    byte[] depthTexture;

    int keyframeIndex = 0;

    async Task UploadKeyframe(int index)
{
    try
    {
        HttpContent textureContent = new ByteArrayContent(rgbTexture);
        HttpContent depthContent = new ByteArrayContent(depthTexture); // Se hai anche una texture di profondità

        MultipartFormDataContent multipartContent = new MultipartFormDataContent();
        multipartContent.Add(textureContent, "files", "LeftRGB.png");
        //multipartContent.Add(depthContent, "files", "Depth.exr");

         HttpContent content = new StringContent(
            $"{{\"Sent keyframe: \": {index}}}", System.Text.Encoding.UTF8, "application/json");
            
        var resp = await client.PostAsync($"{baseUrl}/ping", content); // /posts !
        resp.EnsureSuccessStatusCode();

        var textureResp = await client.PostAsync($"{baseUrl}/keyframe/{index}", multipartContent);
        textureResp.EnsureSuccessStatusCode();

        string body = await resp.Content.ReadAsStringAsync();
        string textureBody = await textureResp.Content.ReadAsStringAsync();
        Debug.Log($"[HTTP] OK {(int)resp.StatusCode} — risposta: {body}");
        Debug.Log($"[HTTP] OK {(int)textureResp.StatusCode} — risposta: {textureBody}");
    }
    catch (Exception e)
    {
        Debug.LogError($"[HTTP] Errore: {e.Message}");
    }
}

    void Start()
    {
        cts = new CancellationTokenSource();
        //workerTask = Task.Run(() => UploadKeyframe(0));
    }

    public void SetRGBTexture(byte[] rgbText, byte[] depthText)
    {
        rgbTexture = rgbText;
        depthTexture = depthText; // Se hai anche una texture di profondità
        Task.Run(() => UploadKeyframe(keyframeIndex));   // parte ORA che il dato c'e'
        keyframeIndex++;

    }

}