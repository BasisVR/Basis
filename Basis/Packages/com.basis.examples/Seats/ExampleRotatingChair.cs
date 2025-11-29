using UnityEngine;

public class ExampleRotatingChair : MonoBehaviour
{
    float time = 0.0f;

    void Update()
    {
        time += Time.deltaTime;
        transform.Rotate(new Vector3(0.0f, Time.deltaTime * 5.0f, 0.0f));
    }
}
