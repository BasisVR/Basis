using Basis;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
[Cilboxable]
public class BilliardsLoadMenu : MonoBehaviour
{
    public BilliardsModule billiardsModule;
    public InputField inputField;

    private void Start()
    {
        wireButton("SaveButton", OnSaveButtonPushed);
        wireButton("LoadButton", OnLoadButtonPushed);
    }

    private void wireButton(string path, UnityAction action)
    {
        Transform target = transform.Find(path);
        if (target == null) return;
        Button button = target.GetComponent<Button>();
        if (button == null) return;
        UnityEventBase onClick = button.onClick;
        int count = onClick.GetPersistentEventCount();
        for (int i = 0; i < count; i++)
        {
            if (onClick.GetPersistentTarget(i) != null) return;
        }
        button.onClick.AddListener(action);
    }

    public void OnSaveButtonPushed()
    {
        if (ReferenceEquals(null, billiardsModule))
        {
            Debug.Log("BilliardsSaveLoad::OnSaveButtonPushed() billiardsModule property is not set !");
            return;
        }

        if (ReferenceEquals(null, inputField))
        {
            Debug.Log("BilliardsSaveLoad::OnSaveButtonPushed() inputField property is not set !");
            return;
        }

        inputField.text = billiardsModule._SerializeGameState();
    }

    public void OnLoadButtonPushed()
    {
        if (ReferenceEquals(null, billiardsModule))
        {
            Debug.Log("BilliardsSaveLoad::OnSaveButtonPushed() billiardsModule property is not set !");
            return;
        }

        if (ReferenceEquals(null, inputField))
        {
            Debug.Log("BilliardsSaveLoad::OnSaveButtonPushed() inputField property is not set !");
            return;
        }

        if (string.IsNullOrEmpty(inputField.text))
        {
            return;
        }

        if (!billiardsModule.isPlayer)
        {
            return;
        }

        billiardsModule._LoadSerializedGameState(inputField.text);

    }
}
