using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
public class BasisUICircularMenu : MonoBehaviour
{
    public RectTransform MiddlePoint;
    public RectTransform Tophat;
    [Header("Settings")]
    public bool clockwise = true;
    public float startAngle = 0f;
    public Sprite DefaultIcon;

    [SerializeField]
    public List<BasisCircularMenuItem> Items;

    [SerializeField]
    public List<BasisCircularMenuBackground> BackGrounds;

    public BasisCircularMenuItem MenuItem;
    public BasisCircularMenuBackground BackGround;
    public float YPointDistance = 20;
    public float XPointDistance = 20;

    public int CreateCount = 4;
    public void OnEnable()
    {
        RebuildMenu();
    }
    public void RebuildMenu()
    {
        BasisUGCMenuDescription[] Description = new BasisUGCMenuDescription[CreateCount];

        for (int Index = 0; Index < Description.Length; Index++)
        {
            BasisUGCMenuDescription description = Description[Index];
            description.MenuName = $"test  {Index}";
            Description[Index] = description;
        }
        ArrangeItemsInCircle(Description);
    }
    // The full circular image this is based on (e.g., 0 to 360 degrees)
    public float TotalDegrees = 360f;
    public float radius;
    public void ArrangeItemsInCircle(BasisUGCMenuDescription[] MenuItems)
    {
        foreach (BasisCircularMenuItem item in Items)
        {
            if (item != null)
            {
                GameObject.Destroy(item.gameObject);
            }
        }
        foreach (BasisCircularMenuBackground item in BackGrounds)
        {
            if (item != null)
            {
                GameObject.Destroy(item.gameObject);
            }
        }
        BackGrounds.Clear();
        Items.Clear();

        int MenuItemsCount = MenuItems.Length;
        if (MenuItemsCount == 0) return;

        float angleStep = TotalDegrees / MenuItemsCount;
        for (int Index = 0; Index < MenuItemsCount; Index++)
        {
            // Calculate the angle in degrees
            float angle = startAngle + (clockwise ? -1 : 1) * Index * angleStep;
            // Instantiate the UI item
            GameObject copyBackground = Instantiate(BackGround.gameObject, MiddlePoint);
            copyBackground.transform.localPosition = Vector3.zero;
            copyBackground.transform.localRotation = Quaternion.identity;
            if (copyBackground.TryGetComponent(out BasisCircularMenuBackground background))
            {
                // Rotate background fill to match the slice
                background.Background.fillAmount = angleStep / 360f;
                background.Background.rectTransform.localRotation = Quaternion.Euler(0, 0, angle);
                background.Background.rectTransform.SetAsFirstSibling();
                copyBackground.SetActive(true);
                BackGrounds.Add(background);
            }
            // Calculate midpoint angle of the filled arc
            float midAngle = angle - (angleStep / 2f);
            // Convert angle to radians (Unity uses clockwise rotation in local space for UI)
            float radians = midAngle * Mathf.Deg2Rad;
            // Radius from center — assuming the pivot is (0.5, 0.5)
            radius = background.Background.rectTransform.rect.width / 2 * 0.5f;
            // Offset position from center
            Vector2 offset = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * radius;
            GameObject itemGO = Instantiate(MenuItem.gameObject, MiddlePoint);
            itemGO.transform.localPosition = new Vector3(XPointDistance, YPointDistance, 0f);
            itemGO.transform.localRotation = Quaternion.identity;
            // Set anchored position relative to the center of the circle
            RectTransform fillRect = itemGO.GetComponent<RectTransform>();
            fillRect.anchoredPosition = offset;
            fillRect.SetAsLastSibling();
            if (itemGO.TryGetComponent(out BasisCircularMenuItem menu))
            {
                background.ConnectedItem = menu;
                Apply(menu, MenuItems[Index]);
                itemGO.SetActive(true);
                Items.Add(menu);
            }
        }

        Tophat.SetAsLastSibling(); // Keep this on top
    }
    public void Apply(BasisCircularMenuItem MenuItem, BasisUGCMenuDescription Description)
    {
        MenuItem.Description = Description;
        MenuItem.Text.text = Description.MenuName;
        MenuItem.Icon.sprite = Description.Sprite != null ? Description.Sprite : DefaultIcon;
    }
}
