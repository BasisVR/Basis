class ForceDateTimeOffset : MonoBehaviour
{
    void Start()
    {
        Debug.Log($"[ForceDateTimeOffset] Start() - Current DateTimeOffset: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
    }
}