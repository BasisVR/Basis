using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class BasisUIBackground :
    UIBehaviour,
    IPointerClickHandler,
    IPointerDownHandler,
    IPointerUpHandler,
    IPointerEnterHandler,
    IPointerExitHandler,
    IPointerMoveHandler
{
    private Material mat;
    private const string CursorPos = "_CursorPos";
    private RectTransform RectTransform;
    public Graphic targetGraphic;
    protected override void Awake()
    {
        if (TryGetComponent<Graphic>(out targetGraphic))
        {
            mat = targetGraphic.material;
        }
        if (TryGetComponent<RectTransform>(out RectTransform))
        {

        }
        base.Awake();
    }

    public void OnPointerMove(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        UpdateShader(eventData);
    }
    private void UpdateShader(PointerEventData eventData)
    {
        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(RectTransform, eventData.position, eventData.pressEventCamera, out Vector3 worldPos))
        {
            // mat.SetVector(CursorPos, worldPos);
            //targetGraphic.canvasRenderer.SetMaterial(mat,0);
            //targetGraphic.canvasRenderer.set
            Shader.SetGlobalVector(CursorPos, worldPos);
        }
    }
}
