namespace JigglePhysics
{
    public class JiggleSettingsBase {
    public virtual JiggleSettingsData GetData() {
        return new JiggleSettingsData();
    }

    public virtual float GetRadius(float normalizedIndex) {
        return 0f;
    }
}

}