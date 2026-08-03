using TMPro;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MicManager : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private TextMeshPro textMeshProUGUI;

    [Header("Latency tuning")]
    // Lunghezza del ring buffer del microfono. Corto = meno lag possibile accumulabile.
    [SerializeField] private int bufferSeconds = 1;
    [SerializeField] private int sampleRate = 48000; // 48 kHz = allineato a Opus/WebRTC, niente resample
    // Quanto tenere la testina di lettura DIETRO alla scrittura del mic. Piccolo = meno lag,
    // ma troppo piccolo rischia underrun (letture su campioni non ancora scritti → crackle).
    [SerializeField, Range(0.02f, 0.4f)] private float targetLatencySeconds = 0.1f;
    // Ricorreggi solo quando la deriva supera questa soglia, così non si "salta" ad ogni frame.
    [SerializeField, Range(0.02f, 0.5f)] private float resyncToleranceSeconds = 0.15f;

    private string micDevice;
    private AudioClip micClip;
    private int clipSamples;

    void Start()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();

        string micList = "Available Microphones:\n";
        foreach (var device in Microphone.devices)
        {
            micList += device + "\n";
        }

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone devices found.");
            return;
        }

        micDevice = Microphone.devices[0];
        micClip = Microphone.Start(micDevice, true, bufferSeconds, sampleRate);
        if (micClip == null)
        {
            Debug.LogError("Failed to start microphone.");
            return;
        }
        clipSamples = micClip.samples;

        audioSource.clip = micClip;
        audioSource.loop = true;

        // Aspetta che il mic scriva davvero il primo campione: su Quest/Android GetPosition
        // resta 0 per un attimo dopo Microphone.Start, e partire prima causa il lag iniziale.
        // (l'attesa avviene nel primo Update via SyncPlayhead, così Start non blocca)
    }

    void Update()
    {
        if (micClip == null || !Microphone.IsRecording(micDevice)) return;

        int writePos = Microphone.GetPosition(micDevice);
        if (writePos <= 0) return; // mic non ha ancora scritto nulla

        // Posizione di lettura desiderata: targetLatency dietro alla scrittura, nel ring buffer.
        int targetSamples = Mathf.RoundToInt(targetLatencySeconds * sampleRate);
        int desiredRead = writePos - targetSamples;
        if (desiredRead < 0) desiredRead += clipSamples;

        if (!audioSource.isPlaying)
        {
            // Il package (o noi) puo' chiamare Play() a posizione 0: agganciamo subito
            // la testina di lettura vicino alla scrittura, cosi' non parte da audio vecchio.
            audioSource.timeSamples = desiredRead;
            audioSource.Play();
            return;
        }

        // Deriva attuale tra scrittura e lettura (quanto siamo indietro), gestendo il wrap.
        int readPos = audioSource.timeSamples;
        int lag = writePos - readPos;
        if (lag < 0) lag += clipSamples;

        int toleranceSamples = Mathf.RoundToInt(resyncToleranceSeconds * sampleRate);
        // Ricorreggi se siamo troppo indietro (lag cresce → i 5-6s) o troppo avanti (underrun).
        if (Mathf.Abs(lag - targetSamples) > toleranceSamples)
        {
            audioSource.timeSamples = desiredRead;
        }
    }

    void OnDisable()
    {
        if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
        {
            Microphone.End(micDevice);
        }
    }
}
