using System;
using System.Net.Http;
using System.Net.Http.Headers;
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

    // Riceve il path della cartella keyframe da KeyFrameManager, la zippa e la
    // invia in streaming all'endpoint /scan.
    // async void: e' un "fire and forget" innescato dalla pressione del tasto A;
    // il chiamante (RetrieveAndSendData) non lo awaita. Il try/catch interno cattura
    // tutte le eccezioni, cosi' l'async void non lascia errori non osservati.
    public async void SendKeyframesFolderAsync(string folderPath)
    {
        Debug.Log($"[HTTP] SendKeyframesFolderAsync ricevuto path: {folderPath}");

        // Letto QUI, sul main thread, prima di qualsiasi await (Application.* va toccato dal main).
        string zipPath = $"{Application.persistentDataPath}/keyframes.zip";

        try
        {
            // Zip pesante (ordine del GB): eseguito su un thread di background.
            // "await Task.Run(...)" cede subito il main thread -> niente freeze del visore.
            await Task.Run(() =>
            {
                // CreateFromDirectory lancia se il file esiste gia': cancella lo zip precedente.
                if (System.IO.File.Exists(zipPath))
                    System.IO.File.Delete(zipPath);

                System.IO.Compression.ZipFile.CreateFromDirectory(folderPath, zipPath);
            });

            Debug.Log($"[HTTP] zip creato: {zipPath}");

            // StreamContent legge lo zip dal disco a blocchi mentre lo invia:
            // NON carica l'intero GB in RAM (a differenza di ByteArrayContent).
            using (var stream = System.IO.File.OpenRead(zipPath))
            using (var content = new StreamContent(stream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                var resp = await client.PostAsync($"{baseUrl}/scan", content);
                resp.EnsureSuccessStatusCode();

                string body = await resp.Content.ReadAsStringAsync();
                Debug.Log($"[HTTP] scan inviata OK {(int)resp.StatusCode} — risposta: {body}");
            }

            // Pulizia: non accumulare uno zip da GB a ogni scan.
            System.IO.File.Delete(zipPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"[HTTP] SendKeyframesFolderAsync errore: {e.Message}");
        }
    }

}