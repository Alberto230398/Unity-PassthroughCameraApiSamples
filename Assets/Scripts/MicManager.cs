using TMPro;
using UnityEngine;

public class MicManager : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private TextMeshPro textMeshProUGUI;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        string micList = "Available Microphones:\n";
        foreach (var device in Microphone.devices)
        {
            micList += device + "\n";
        }

        textMeshProUGUI.text = micList;

        AudioClip clip = Microphone.Start(Microphone.devices[0], true, 10, 44100);
        audioSource.clip = clip;
        if (clip == null)
        {
            Debug.LogError("Failed to start microphone.");
            return;
        }
        audioSource.Play();

        textMeshProUGUI.text = micList;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
