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

        AudioClip clip = Microphone.Start(Microphone.devices[0], false, 10, 44100);
        audioSource.PlayOneShot(clip);

    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
