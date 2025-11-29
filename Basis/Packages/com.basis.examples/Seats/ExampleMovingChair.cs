using UnityEngine;

public class ExampleMovingChair : MonoBehaviour
{
    Vector3 startPos;
    float time = 0.0f;

    void Start()
    {
        startPos = transform.position;
    }

    void Update()
    {
        time += Time.deltaTime;
        transform.position = startPos + new Vector3(0.0f, 1.0f + Mathf.Sin(time), 0.0f);
    }
}
