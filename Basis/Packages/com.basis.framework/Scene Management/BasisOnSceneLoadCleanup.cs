using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Destroys the attached GameObject when a scene is loaded with the specified load mode.
/// Useful for cleaning up temporary objects when additional scenes are added or replaced.
/// </summary>
public class BasisOnSceneLoadCleanup : MonoBehaviour
{
    /// <summary>
    /// Scene load mode that will trigger destruction of this GameObject.
    /// Defaults to <see cref="LoadSceneMode.Additive"/>.
    /// </summary>
    public LoadSceneMode TriggerOn = LoadSceneMode.Additive;

    /// <summary>
    /// Unity OnEnable hook. Subscribes to the <see cref="SceneManager.sceneLoaded"/> event.
    /// </summary>
    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    /// <summary>
    /// Unity OnDisable hook. Unsubscribes from the <see cref="SceneManager.sceneLoaded"/> event.
    /// </summary>
    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    /// <summary>
    /// Callback executed whenever a new scene is loaded. Destroys the GameObject if the load mode matches <see cref="TriggerOn"/>.
    /// </summary>
    /// <param name="scene">The scene that was loaded.</param>
    /// <param name="mode">The load mode used when loading the scene.</param>
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == TriggerOn)
        {
            GameObject.Destroy(this.gameObject);
        }
    }
}
