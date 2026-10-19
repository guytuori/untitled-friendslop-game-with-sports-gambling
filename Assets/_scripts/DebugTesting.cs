using UnityEngine;
using UnityEngine.Audio;

public class DebugTesting : MonoBehaviour
{
    public AudioListener audio;
    public void Start()
    {
        AudioListener.pause = true;

    }
}
