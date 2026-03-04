using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class EnableImageTrackingWhenReady : MonoBehaviour
{
    [SerializeField] ARTrackedImageManager imageManager; // assign in Inspector
    [SerializeField] GameObject[] toEnableAfter;         // optional scripts/go's to enable after

    void Awake()
    {
        if (imageManager) imageManager.enabled = false;
        foreach (var go in toEnableAfter) if (go) go.SetActive(false);
    }

    void Update()
    {
        // Wait for ARSession to be ready and a valid surface size
        if (ARSession.state == ARSessionState.SessionTracking ||
            ARSession.state == ARSessionState.Ready)
        {
            if (Screen.width > 0 && Screen.height > 0)
            {
                if (imageManager && !imageManager.enabled)
                {
                    imageManager.enabled = true;
                    foreach (var go in toEnableAfter) if (go) go.SetActive(true);
                    enabled = false; // we're done
                }
            }
        }
    }
}