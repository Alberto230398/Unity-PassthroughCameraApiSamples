using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// HttpClient lifecycle management best practices:
// https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines#recommended-use

public class HttpManager : MonoBehaviour
{
    [SerializeField] private string baseUrl = "https://jsonplaceholder.typicode.com";
    static readonly HttpClient client = new HttpClient(); // UNO, statico, riusato

    CancellationTokenSource cts;   // per fermare il worker in modo pulito
    Task workerTask;               // riferimento al task, per aspettarlo alla chiusura

    async Task UploadKeyframe(int index)
{
    try
    {
        HttpContent content = new StringContent(
            $"{{\"CIAO\": {index}}}", System.Text.Encoding.UTF8, "application/json");

        var resp = await client.PostAsync($"{baseUrl}/ping", content); // /posts !
        resp.EnsureSuccessStatusCode();

        string body = await resp.Content.ReadAsStringAsync();
        Debug.Log($"[HTTP] OK {(int)resp.StatusCode} — risposta: {body}");
    }
    catch (Exception e)
    {
        Debug.LogError($"[HTTP] Errore: {e.Message}");
    }
}

void Start()
{
    cts = new CancellationTokenSource();
    workerTask = Task.Run(() => UploadKeyframe(0));
}

}