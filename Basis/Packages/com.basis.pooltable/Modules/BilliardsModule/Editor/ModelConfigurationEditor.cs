using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(ModelConfiguration))]
public class ModelConfigurationEditor : Editor
{
    bool bShowCollisionModel = true;
    static GUIStyle styleHeader;
    static GUIStyle styleError;
    static GUIStyle styleWarning;
    static bool gui_resource_ready = false;

    CollisionVisualizer cdata_displayTarget;
    private static void DrawError(string szError, GUIStyle style)
    {
        GUILayout.BeginVertical("GroupBox");
        GUILayout.Label(szError, style);
        GUILayout.EndVertical();
    }

    private static bool Material_ht8b_supports(ref Material mat)
    {
        bool isFullSupport = true;

        if (!mat.HasProperty("_EmissionColor"))
        {
            DrawError($"[!] Shader '{mat.shader.name}' does not have property: _EmissionColor", styleError);
            isFullSupport = false;
        }

        if (!mat.HasProperty("_Color"))
        {
            DrawError($"Shader {mat.shader.name} does not have property: _Color", styleWarning);
        }

        return isFullSupport;
    }

    private static void Ht8bUIGroup(string szHeader)
    {
        GUILayout.BeginVertical("HelpBox");
        GUILayout.Label(szHeader, styleHeader);
    }

    private static bool Ht8bUIGroupMitButton(string szHeader, string szButton)
    {
        GUILayout.BeginVertical("HelpBox");
        GUILayout.BeginHorizontal();
        GUILayout.Label(szHeader, styleHeader);
        bool b = GUILayout.Button(szButton);
        GUILayout.EndHorizontal();

        return b;
    }

    private static void Ht8bUIGroupEnd()
    {
        GUILayout.EndVertical();
    }

    private static void gui_resource_init()
    {
        styleHeader = new GUIStyle()
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold
        };

        styleWarning = new GUIStyle()
        {
            wordWrap = true
        };

        styleError = new GUIStyle()
        {
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };

        gui_resource_ready = true;
    }

    public override void OnInspectorGUI()
    {
        if (!gui_resource_ready)
        {
            gui_resource_init();
        }
        ModelConfiguration _editor = (ModelConfiguration)target;

        base.DrawDefaultInspector();

        ModelData data = _editor.data;

        if (data != null)
        {
            Ht8bUIGroup("Collision info");

            if (!cdata_displayTarget)
            {
                Transform table = null;
                if (_editor.transform.parent)
                    if (_editor.transform.parent.parent)
                        table = _editor.transform.parent.parent.Find("intl.balls");
                if (table)
                {
                    Transform refiner = table.Find("__table_refiner__");
                    if (refiner)
                    {
                        cdata_displayTarget = _editor.transform.parent.parent.Find("intl.balls").Find("__table_refiner__").gameObject.GetComponent<CollisionVisualizer>();
                    }
                }
            }
            if (cdata_displayTarget == null) { return; }

            this.bShowCollisionModel = EditorGUILayout.Toggle("Draw collision model", this.cdata_displayTarget.gameObject.activeSelf);
            this.cdata_displayTarget.gameObject.SetActive(this.bShowCollisionModel);

            Ht8bUIGroupEnd();
            sendValuesToVisualizerAndUpdateView(data);
        }
    }
    float lastUpdate;
    void sendValuesToVisualizerAndUpdateView(ModelData data)
    {
        // Same conversions as in BilliardsModule.setTableModel()
        this.cdata_displayTarget.tableWidth = data.tableWidth * .5f;
        this.cdata_displayTarget.tableHeight = data.tableHeight * .5f;
        this.cdata_displayTarget.k_BALL_RADIUS = (data.bs_BallDiameter * .5f) / 1000f;
        this.cdata_displayTarget.pocketWidthCorner = data.pocketWidthCorner;
        this.cdata_displayTarget.pocketHeightCorner = data.pocketHeightCorner;
        this.cdata_displayTarget.pocketRadiusSide = data.pocketRadiusSide;
        this.cdata_displayTarget.pocketDepthSide = data.pocketDepthSide;
        this.cdata_displayTarget.cushionRadius = data.cushionRadius;
        this.cdata_displayTarget.pocketInnerRadiusCorner = data.pocketInnerRadiusCorner;
        this.cdata_displayTarget.pocketInnerRadiusSide = data.pocketInnerRadiusSide;
        this.cdata_displayTarget.pocketInnerRadiusCorner2 = data.pocketInnerRadiusCorner2;
        this.cdata_displayTarget.pocketInnerRadiusSide2 = data.pocketInnerRadiusSide2;
        this.cdata_displayTarget.cornerPocket = data.cornerPocket;
        this.cdata_displayTarget.cornerPocket2 = data.cornerPocket2;
        this.cdata_displayTarget.sidePocket = data.sidePocket;
        this.cdata_displayTarget.sidePocket2 = data.sidePocket2;
        this.cdata_displayTarget.facingAngleCorner = data.facingAngleCorner;
        this.cdata_displayTarget.facingAngleSide = data.facingAngleSide;
        this.cdata_displayTarget.k_RAIL_HEIGHT_UPPER = data.railHeightUpper;
        this.cdata_displayTarget.k_RAIL_HEIGHT_LOWER = data.railHeightLower;
        this.cdata_displayTarget.k_RAIL_DEPTH_WIDTH = data.railDepthWidth;
        this.cdata_displayTarget.k_RAIL_DEPTH_HEIGHT = data.railDepthHeight;

        this.cdata_displayTarget.baulkLine = -((data.tableWidth * .5f) - data.baulkLine);
        this.cdata_displayTarget.blackSpot = (data.tableWidth * .5f) - data.blackSpot;
        this.cdata_displayTarget.semiCircleRadius = data.semiCircleRadius;
        this.cdata_displayTarget.pinkSpot = (data.tableWidth * .5f) - data.pinkSpot;

        Transform table_artwork = data.transform.Find("table_artwork");
        Transform tableSurface = table_artwork.transform.Find(".TABLE_SURFACE");
        if (tableSurface)
        { this.cdata_displayTarget.table_Surface = tableSurface; }
        SceneView.RepaintAll();
    }
    void OnEnable()
    {
        ModelConfiguration _editor = (ModelConfiguration)target;
        if (!cdata_displayTarget)
        {
            if (_editor.transform.parent)
                if (_editor.transform.parent.parent)
                    cdata_displayTarget = _editor.transform.parent.parent.Find("intl.balls").Find("__table_refiner__").gameObject.GetComponent<CollisionVisualizer>();
            if (cdata_displayTarget == null)
            {
                return;
            }
        }
        ModelData data = _editor.data;
        cdata_displayTarget.gameObject.SetActive(this.bShowCollisionModel);
        cdata_displayTarget.drawTable();
        sendValuesToVisualizerAndUpdateView(data);
    }
    void OnDisable()
    {
        if (cdata_displayTarget)
        {
            cdata_displayTarget.gameObject.SetActive(false);
        }
    }
}