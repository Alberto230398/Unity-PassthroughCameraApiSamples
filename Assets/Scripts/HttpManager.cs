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

    int keyframeIndex = 0;

    // I byte[] arrivano come argomenti: ogni task possiede i propri dati,
    // niente campi condivisi che la cattura successiva possa sovrascrivere.
    async Task UploadKeyframe(int index, byte[] rgb, byte[] depth)
    {
        try
        {
            HttpContent textureContent = new ByteArrayContent(rgb);
            HttpContent depthContent = new ByteArrayContent(depth); // Se hai anche una texture di profondità
            MultipartFormDataContent multipartContent = new MultipartFormDataContent();
            multipartContent.Add(textureContent, "files", "LeftRGB.png");
            multipartContent.Add(depthContent, "files", "Depth.exr");

            var textureResp = await client.PostAsync($"{baseUrl}/keyframe/{index}", multipartContent);
            textureResp.EnsureSuccessStatusCode();

            string textureBody = await textureResp.Content.ReadAsStringAsync();
            Debug.Log($"[HTTP] keyframe {index} OK {(int)textureResp.StatusCode} — risposta: {textureBody}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HTTP] keyframe {index} Errore: {e.Message}");
        }
    }

    void Start()
    {
        cts = new CancellationTokenSource();
    }

    public void SetRGBTexture(byte[] rgbText, byte[] depthText)
    {
        int index = keyframeIndex;   // fotografa l'indice ADESSO, in una locale
        keyframeIndex++;

        // indice e byte[] passati come argomenti: la lambda cattura valori congelati
        Task.Run(() => UploadKeyframe(index, rgbText, depthText));
    }

}