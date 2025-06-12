using UnityEngine;
using UnityEngine.SceneManagement;

public class BasisOnSceneLoadCleanup : MonoBehaviour
{
    public LoadSceneMode TriggerOn = LoadSceneMode.Additive;
    void OnEnable()
    {
        // Subscribe to the sceneLoaded event
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        // Unsubscribe from the sceneLoaded event
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // This method is called every time a new scene is loaded
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == TriggerOn)
        {
            GameObject.Destroy(this.gameObject);
        }
    }
}