#define HT8B_DRAW_REGIONS

using UnityEngine;
[ExecuteInEditMode]
public class CollisionVisualizer : MonoBehaviour
{
    [SerializeField] BilliardsModule table;
    [Tooltip("Only works in play mode")]
    public bool getFromTable = false;
    [Tooltip("This function hasn't been updated for MS-VRCSA, may show something incorrect")]
    public bool previewCollision = false;
    [SerializeField] public float tableWidth;
    [SerializeField] public float tableHeight;
    [SerializeField] public float pocketWidthCorner;
    [SerializeField] public float pocketHeightCorner;
    [SerializeField] public float pocketDepthSide;
    [SerializeField] public float pocketRadiusSide;
    [SerializeField] public float cushionRadius;
    [SerializeField] public float pocketInnerRadiusCorner;
    [SerializeField] public float pocketInnerRadiusSide;
    [SerializeField] public float pocketInnerRadiusCorner2;
    [SerializeField] public float pocketInnerRadiusSide2;

    [SerializeField] public Vector3 cornerPocket; // k_vE
    [SerializeField] public Vector3 sidePocket; // k_vF
    [SerializeField] public Vector3 cornerPocket2; // k_vE
    [SerializeField] public Vector3 sidePocket2; // k_vF
    [SerializeField] public float facingAngleCorner;
    [SerializeField] public float facingAngleSide;
    [SerializeField] public float k_BALL_RADIUS;
    [SerializeField] public float k_RAIL_HEIGHT_UPPER;
    [SerializeField] public float k_RAIL_HEIGHT_LOWER;
    [SerializeField] public float k_RAIL_DEPTH_WIDTH;
    [SerializeField] public float k_RAIL_DEPTH_HEIGHT;
    [SerializeField] public float baulkLine;
    [SerializeField] public float blackSpot;
    [SerializeField] public float semiCircleRadius;
    [SerializeField] public float pinkSpot;
    [SerializeField] public Transform table_Surface;

    float k_MINOR_REGION_CONST;

    Vector3 k_vA = new Vector3();
    Vector3 k_vB = new Vector3();
    Vector3 k_vC = new Vector3();
    Vector3 k_vD = new Vector3();

    Vector3 k_vX = new Vector3();
    Vector3 k_vY = new Vector3();
    Vector3 k_vZ = new Vector3();
    Vector3 k_vW = new Vector3();

    Vector3 k_pK = new Vector3();
    Vector3 k_pL = new Vector3();
    Vector3 k_pM = new Vector3();
    Vector3 k_pN = new Vector3();
    Vector3 k_pO = new Vector3();
    Vector3 k_pP = new Vector3();
    Vector3 k_pQ = new Vector3();
    Vector3 k_pR = new Vector3();
    Vector3 k_pT = new Vector3();
    Vector3 k_pS = new Vector3();
    Vector3 k_pU = new Vector3();
    Vector3 k_pV = new Vector3();

    Vector3 k_vA_vD = new Vector3();
    Vector3 k_vA_vD_normal = new Vector3();

    Vector3 k_vB_vY = new Vector3();
    Vector3 k_vC_vZ = new Vector3();
    Vector3 k_vB_vY_normal = new Vector3();

    Vector3 k_vC_vZ_normal = new Vector3();

    Vector3 k_vA_vB_normal = new Vector3(0.0f, 0.0f, -1.0f);
    Vector3 k_vC_vW_normal = new Vector3(-1.0f, 0.0f, 0.0f);

    Vector3 railCornerOuter = new Vector3();
    Vector3 railWidthMidPoint = new Vector3();
    Vector3 railHeightMidPoint = new Vector3();

    //public Vector3 k_vE = new Vector3(1.087f,0.0f,0.627f);
    //public Vector3 k_vF = new Vector3(0.0f,0.0f,0.665f);
    // Stub
    void _phy_bounce_cushion(int id, Vector3 N) { }

    string _encode_table_collider()
    {
        return "";
    }

    void _phy_table_init()
    {
        // Handy values
        k_MINOR_REGION_CONST = tableWidth - tableHeight;

        // Major source vertices
        k_vA.x = pocketRadiusSide;
        k_vA.z = tableHeight + cushionRadius;

        k_vB.x = tableWidth;
        k_vB.z = tableHeight + cushionRadius;

        k_vC.x = tableWidth + cushionRadius;
        k_vC.z = tableHeight;

        k_vD = k_vA;
        Vector3 Rotationk_vD = new Vector3(pocketDepthSide, 0, 0);
        Rotationk_vD = Quaternion.AngleAxis(-facingAngleSide, Vector3.up) * Rotationk_vD;
        k_vD += Rotationk_vD;

        // Aux points
        k_vX = k_vD + Vector3.forward;
        k_vW = k_vC;
        k_vW.z = 0.0f;

        k_vY = k_vB;
        Vector3 Rotationk_vY = new Vector3(-1, 0, 0);
        Rotationk_vY = Quaternion.AngleAxis(facingAngleCorner, Vector3.up) * Rotationk_vY;
        k_vY += Rotationk_vY;
        // k_vY.x += 0.781f;
        // k_vY.z += 1f;

        k_vZ = k_vC;
        Vector3 Rotationk_vZ = new Vector3(0, 0, -1);
        Rotationk_vZ = Quaternion.AngleAxis(-facingAngleCorner, Vector3.up) * Rotationk_vZ;
        k_vZ += Rotationk_vZ;
        // k_vZ.x += 1.0f;
        // k_vZ.z += 0.781f;

        // Normals
        k_vA_vD = k_vD - k_vA;
        k_vA_vD = k_vA_vD.normalized;
        k_vA_vD_normal.x = -k_vA_vD.z;
        k_vA_vD_normal.z = k_vA_vD.x;

        k_vB_vY = k_vB - k_vY;
        k_vB_vY = k_vB_vY.normalized;
        k_vB_vY_normal.x = -k_vB_vY.z;
        k_vB_vY_normal.z = k_vB_vY.x;

        //set up angle properly instead of just mirroring, for if it's not just 45 degrees
        k_vC_vZ = k_vC - k_vZ;
        k_vC_vZ = k_vC_vZ.normalized;
        k_vC_vZ_normal.x = k_vC_vZ.z;
        k_vC_vZ_normal.z = -k_vC_vZ.x;

        // Minkowski difference
        k_pN = k_vA;
        k_pN.z -= cushionRadius;

        k_pM = k_vA + k_vA_vD_normal * cushionRadius;
        k_pL = k_vD + k_vA_vD_normal * cushionRadius;

        k_pK = k_vD;
        k_pK.x -= cushionRadius;

        k_pO = k_vB;
        k_pO.z -= cushionRadius;

        k_pP = k_vB + k_vB_vY_normal * cushionRadius;

        k_pQ = k_vC + k_vC_vZ_normal * cushionRadius;


        k_pR = k_vC;
        k_pR.x -= cushionRadius;


        k_pT = k_vX;
        k_pT.x -= cushionRadius;

        k_pS = k_vW;
        k_pS.x -= cushionRadius;

        k_pU = k_vY + k_vB_vY_normal * cushionRadius;
        k_pV = k_vZ + k_vC_vZ_normal * cushionRadius;

        //move points to enable pocket radius tweaking and adjusting cushion radius without moving edges
        float heightXdifN = k_pN.x - k_pM.x;
        float heightXdifT = k_pT.x - k_pM.x;
        float heightXdifL = k_pL.x - k_pM.x;
        float heightXdifK = k_pK.x - k_pM.x;
        float heightXdifA = k_vA.x - k_pM.x;
        float heightXdifD = k_vD.x - k_pM.x;
        k_pN.x += pocketRadiusSide;
        k_pM.x = k_pN.x;
        k_pN.x = k_pM.x + heightXdifN;
        k_pT.x = k_pM.x + heightXdifT;
        k_pL.x = k_pM.x + heightXdifL;
        k_pK.x = k_pM.x + heightXdifK;
        k_vA.x = k_pM.x + heightXdifA;
        k_vD.x = k_pM.x + heightXdifD;

        float widthXdifR = k_pO.x - k_pP.x;
        float widthXdifV = k_pU.x - k_pP.x;
        float widthXdifB = k_vB.x - k_pP.x;
        k_pO.x -= pocketWidthCorner;
        k_pP.x = k_pO.x;
        k_pO.x = k_pP.x + widthXdifR;
        k_pU.x = k_pP.x + widthXdifV;
        k_vB.x = k_pP.x + widthXdifB;

        float heightZdifR = k_pR.z - k_pQ.z;
        float heightZdifV = k_pV.z - k_pQ.z;
        float heightZdifC = k_vC.z - k_pQ.z;
        k_pR.z -= pocketHeightCorner;
        k_pQ.z = k_pR.z;
        k_pR.z = k_pQ.z + heightZdifR;
        k_pV.z = k_pQ.z + heightZdifV;
        k_vC.z = k_pQ.z + heightZdifC;

        // Rail Height Stuff
        railCornerOuter = Vector3.right * (tableWidth + k_RAIL_DEPTH_WIDTH) + Vector3.forward * (tableHeight + k_RAIL_DEPTH_HEIGHT);
        railWidthMidPoint = railCornerOuter;
        railWidthMidPoint.x = 0;
        railHeightMidPoint = railCornerOuter;
        railHeightMidPoint.z = 0;
    }

    string _obj_vec_str(Vector3 v)
    {
        return $"v {v.x} {v.y} {v.z}\n";
    }

    void drawCylinder_pocket(Vector3 at, Vector3 other, float r, float r_other, Color colour, float wallHeight = 0.048f)
    {
        Vector3 last = Vector3.forward * r;
        Vector3 cur = last;
        float angle = 360f / 32;
        Quaternion rotQuat = Quaternion.AngleAxis(angle, Vector3.up);

        for (int i = 0; i < 32; i++)
        {
            cur = rotQuat * cur;
            Color thisCol = colour;
            if (Vector3.Distance(at + last, other) > r_other && Vector3.Distance(at + cur, other) > r_other)
            {
                thisCol *= .3f;
            }
            drawPlane(at + last, at + cur, thisCol, wallHeight, 16);
            last = cur;
        }
    }
    void drawCylinder(Vector3 at, float r, Color colour, float wallHeight = 0.048f, uint density = 16, uint sides = 32)
    {
        Vector3 last = Vector3.forward * r;
        Vector3 cur = last;
        float angle = 360f / sides;
        Quaternion rotQuat = Quaternion.AngleAxis(angle, Vector3.up);

        for (int i = 0; i < sides; i++)
        {
            cur = rotQuat * cur;

            drawPlane(at + last, at + cur, colour, wallHeight, density);
            last = cur;
        }
    }

    void _drawline_applyparent(Vector3 from, Vector3 to, Color colour)
    {
        if (table_Surface)
            Debug.DrawLine(table_Surface.TransformPoint(from), table_Surface.TransformPoint(to), colour);
    }

    // Reflective, stacked by n
    void drawPlane(Vector3 from, Vector3 to, Color colour, float wallHeight = 0.048f, uint density = 16)
    {
        Vector3 reflect_x = new Vector3(-1.0f, 1.0f, 1.0f);
        Vector3 reflect_z = new Vector3(1.0f, 1.0f, -1.0f);
        Vector3 reflect_xz = Vector3.Scale(reflect_x, reflect_z);

        float heightInterval = wallHeight / (density - 1);
        for (int n = 0; n < density; n++)
        {
            Vector3 height = Vector3.up * (heightInterval * n);

            _drawline_applyparent(from + height, to + height, colour);
            _drawline_applyparent(Vector3.Scale(from, reflect_x) + height, Vector3.Scale(to, reflect_x) + height, colour);
            _drawline_applyparent(Vector3.Scale(from, reflect_z) + height, Vector3.Scale(to, reflect_z) + height, colour);
            _drawline_applyparent(Vector3.Scale(from, reflect_xz) + height, Vector3.Scale(to, reflect_xz) + height, colour);
        }
    }

    Vector3 _sign_pos = new Vector3(0.0f, 1.0f, 0.0f);
    void _phy_ball_table_new()
    {
        Vector3 A, N, a_to_v;
        float dot;

        A = this.transform.localPosition;

        int id = 0;

        _sign_pos.x = Mathf.Sign(A.x);
        _sign_pos.z = Mathf.Sign(A.z);

        A = Vector3.Scale(A, _sign_pos);

#if HT8B_DRAW_REGIONS

        _phy_table_init();

        //side pockets
        drawPlane(k_pT, k_pK, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pK, k_pL, Color.yellow, k_RAIL_HEIGHT_UPPER);

        drawPlane(railWidthMidPoint, railCornerOuter, Color.grey, k_RAIL_HEIGHT_UPPER);
        drawPlane(railHeightMidPoint, railCornerOuter, Color.grey, k_RAIL_HEIGHT_UPPER);


        drawPlane(k_pL, k_pM, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pM, k_pN, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pN, k_pO, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pO, k_pP, Color.yellow, k_RAIL_HEIGHT_UPPER);

        //corner pockets
        drawPlane(k_pP, k_pU, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pV, k_pQ, Color.yellow, k_RAIL_HEIGHT_UPPER);

        drawPlane(k_pQ, k_pR, Color.yellow, k_RAIL_HEIGHT_UPPER);
        drawPlane(k_pR, k_pS, Color.yellow, k_RAIL_HEIGHT_UPPER);

        if (_phy_ball_pockets() && previewCollision)
        {
            drawCylinder_pocket(cornerPocket, cornerPocket2, pocketInnerRadiusCorner, pocketInnerRadiusCorner2, Color.green, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(sidePocket, sidePocket2, pocketInnerRadiusSide, pocketInnerRadiusSide2, Color.green, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(cornerPocket2, cornerPocket, pocketInnerRadiusCorner2, pocketInnerRadiusCorner, Color.green, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(sidePocket2, sidePocket, pocketInnerRadiusSide2, pocketInnerRadiusSide, Color.green, k_RAIL_HEIGHT_UPPER);
        }
        else
        {
            drawCylinder_pocket(cornerPocket, cornerPocket2, pocketInnerRadiusCorner, pocketInnerRadiusCorner2, Color.red, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(sidePocket, sidePocket2, pocketInnerRadiusSide, pocketInnerRadiusSide2, Color.red, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(cornerPocket2, cornerPocket, pocketInnerRadiusCorner2, pocketInnerRadiusCorner, Color.red, k_RAIL_HEIGHT_UPPER);
            drawCylinder_pocket(sidePocket2, sidePocket, pocketInnerRadiusSide2, pocketInnerRadiusSide, Color.red, k_RAIL_HEIGHT_UPPER);
        }
        _drawline_applyparent(new Vector3(baulkLine, 0, -tableWidth * .5f), new Vector3(baulkLine, 0, tableWidth * .5f), Color.red);
        _drawline_applyparent(new Vector3(baulkLine, 0, -semiCircleRadius), new Vector3(baulkLine, .12f, -semiCircleRadius), Color.yellow);
        _drawline_applyparent(new Vector3(baulkLine, 0, semiCircleRadius), new Vector3(baulkLine, .12f, semiCircleRadius), Color.green);
        _drawline_applyparent(new Vector3(baulkLine, 0, 0), new Vector3(baulkLine, .12f, 0), new Color(.3f, .12f, .05f));
        _drawline_applyparent(new Vector3(pinkSpot, 0, 0), new Vector3(pinkSpot, .12f, 0), (Color.red + Color.white) / 2f);
        _drawline_applyparent(new Vector3(blackSpot, 0, 0), new Vector3(blackSpot, .12f, 0), Color.black);
        _drawline_applyparent(new Vector3(0, 0, 0), new Vector3(0, .12f, 0), Color.blue);

        drawCylinder(k_vA, cushionRadius, Color.yellow, k_RAIL_HEIGHT_UPPER, 2);
        drawCylinder(k_vB, cushionRadius, Color.yellow, k_RAIL_HEIGHT_UPPER, 2);
        drawCylinder(k_vC, cushionRadius, Color.yellow, k_RAIL_HEIGHT_UPPER, 2);
        drawCylinder(k_vD, cushionRadius, Color.yellow, k_RAIL_HEIGHT_UPPER, 2);
#endif
        if (!previewCollision) { return; }

        if (A.x > k_vA.x) // Major Regions
        {
            if (A.x > A.z + k_MINOR_REGION_CONST) // Minor B
            {
                if (A.z < tableHeight - pocketHeightCorner)
                {
                    // Region H
#if HT8B_DRAW_REGIONS
                    _drawline_applyparent(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(tableWidth, 0.0f, 0.0f), Color.red);
                    _drawline_applyparent(k_vC, k_vC + k_vC_vW_normal, Color.red);
#endif
                    if (A.x > tableWidth - cushionRadius)
                    {
                        // Static resolution
                        A.x = tableWidth - cushionRadius;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos));
                    }
                }
                else
                {
                    a_to_v = A - k_vC;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region I ( VORONI )
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vC, k_pR, Color.green);
                        _drawline_applyparent(k_vC, k_pQ, Color.green);
#endif
                        if (a_to_v.magnitude < cushionRadius)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vC + N * cushionRadius;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
                        }
                    }
                    else
                    {
                        // Region J
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vC, k_vB, Color.red);
                        _drawline_applyparent(k_pQ, k_pV, Color.blue);
#endif
                        a_to_v = A - k_pQ;

                        if (Vector3.Dot(k_vC_vZ_normal, a_to_v) < 0.0f)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vB_vY);
                            A = k_pQ + dot * k_vB_vY;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vZ_normal, _sign_pos));
                        }
                    }
                }
            }
            else // Minor A
            {
                if (A.x < k_vB.x)
                {
                    // Region A
#if HT8B_DRAW_REGIONS
                    _drawline_applyparent(k_vA, k_vA + k_vA_vB_normal, Color.red);
                    _drawline_applyparent(k_vB, k_vB + k_vA_vB_normal, Color.red);
#endif
                    if (A.z > k_pN.z)
                    {
                        // Static resolution
                        A.z = k_pN.z;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vA_vB_normal, _sign_pos));
                    }
                }
                else
                {
                    a_to_v = A - k_vB;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region F ( VERONI )
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vB, k_pO, Color.green);
                        _drawline_applyparent(k_vB, k_pP, Color.green);
#endif
                        if (a_to_v.magnitude < cushionRadius)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vB + N * cushionRadius;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
                        }
                    }
                    else
                    {
                        // Region G
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vB, k_vC, Color.red);
                        _drawline_applyparent(k_pP, k_pU, Color.blue);
#endif
                        a_to_v = A - k_pP;

                        if (Vector3.Dot(k_vB_vY_normal, a_to_v) < 0.0f)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vB_vY);
                            A = k_pP + dot * k_vB_vY;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vB_vY_normal, _sign_pos));
                        }
                    }
                }
            }
        }
        else
        {
            a_to_v = A - k_vA;

            if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
            {
                a_to_v = A - k_vD;

                if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
                {
                    if (A.z > k_pK.z)
                    {
                        // Region E
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vD, k_vD + k_vC_vW_normal, Color.red);
#endif
                        if (A.x > k_pK.x)
                        {
                            // Static resolution
                            A.x = k_pK.x;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos));
                        }
                    }
                    else
                    {
                        // Region D ( VORONI )
#if HT8B_DRAW_REGIONS
                        _drawline_applyparent(k_vD, k_vD + k_vC_vW_normal, Color.green);
                        _drawline_applyparent(k_vD, k_vD + k_vA_vD_normal, Color.green);
#endif
                        if (a_to_v.magnitude < cushionRadius)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vD + N * cushionRadius;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
                        }
                    }
                }
                else
                {
                    // Region C
#if HT8B_DRAW_REGIONS
                    _drawline_applyparent(k_vA, k_vA + k_vA_vD_normal, Color.red);
                    _drawline_applyparent(k_vD, k_vD + k_vA_vD_normal, Color.red);
                    _drawline_applyparent(k_pL, k_pM, Color.blue);
#endif
                    a_to_v = A - k_pL;

                    if (Vector3.Dot(k_vA_vD_normal, a_to_v) < 0.0f)
                    {
                        // Static resolution
                        dot = Vector3.Dot(a_to_v, k_vA_vD);
                        A = k_pL + dot * k_vA_vD;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vA_vD_normal, _sign_pos));
                    }
                }
            }
            else
            {
                // Region B ( VORONI )
#if HT8B_DRAW_REGIONS
                _drawline_applyparent(k_vA, k_vA + k_vA_vB_normal, Color.green);
                _drawline_applyparent(k_vA, k_vA + k_vA_vD_normal, Color.green);
#endif
                if (a_to_v.magnitude < cushionRadius)
                {
                    // Static resolution
                    N = a_to_v.normalized;
                    A = k_vA + N * cushionRadius;

                    // Dynamic
                    _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
                }
            }
        }

        A = Vector3.Scale(A, _sign_pos);

        this.transform.localPosition = A;
    }

    bool _phy_ball_pockets()
    {
        Vector3 A;

        A = this.transform.localPosition;

        _sign_pos.x = Mathf.Sign(A.x);
        _sign_pos.z = Mathf.Sign(A.z);

        A = Vector3.Scale(A, _sign_pos);

        if (Vector3.Distance(A, cornerPocket) < pocketInnerRadiusCorner)
        {
            return true;
        }

        if (Vector3.Distance(A, sidePocket) < pocketInnerRadiusSide)
        {
            return true;
        }

        if (A.z > sidePocket.z)
        {
            return true;
        }

        if (A.z > -A.x + cornerPocket.x + cornerPocket.z)
        {
            return true;
        }

        return false;
    }

    // Update is called once per frame
    void Update()
    {
        drawTable();
    }
    public void drawTable()
    {
        if (getFromTable && Application.isPlaying)
        {
            tableWidth = (float)table.k_TABLE_WIDTH;
            tableHeight = (float)table.k_TABLE_HEIGHT;
            pocketWidthCorner = (float)table.k_POCKET_WIDTH_CORNER;
            pocketHeightCorner = (float)table.k_POCKET_WIDTH_CORNER;
            pocketRadiusSide = (float)table.k_POCKET_RADIUS_SIDE;
            pocketDepthSide = (float)table.k_POCKET_DEPTH_SIDE;
            cushionRadius = (float)table.k_CUSHION_RADIUS;
            pocketInnerRadiusCorner = (float)table.k_INNER_RADIUS_CORNER;
            pocketInnerRadiusSide = (float)table.k_INNER_RADIUS_SIDE;
            pocketInnerRadiusCorner2 = (float)table.k_INNER_RADIUS_CORNER2;
            pocketInnerRadiusSide2 = (float)table.k_INNER_RADIUS_SIDE2;
            cornerPocket = (Vector3)table.k_vE; // k_vE
            sidePocket = (Vector3)table.k_vF; // k_vF
            cornerPocket2 = (Vector3)table.k_vE2; // k_vE
            sidePocket2 = (Vector3)table.k_vF2; // k_vF
            facingAngleCorner = (float)table.k_FACING_ANGLE_CORNER;
            facingAngleSide = (float)table.k_FACING_ANGLE_SIDE;
            baulkLine = (float)table.K_BAULK_LINE;
            blackSpot = (float)table.K_BLACK_SPOT;
            semiCircleRadius = (float)table.k_SEMICIRCLERADIUS;
            pinkSpot = (float)table.k_SPOT_POSITION_X;
        }

        _phy_ball_table_new();
        checkCushionCollision();
    }

    private Vector3 sidePocketNew;
    private float sidePocketRadius;
    private Vector3 cornerPocketNew;
    private float cornerPocketRadius;

    private Vector3 sideStart;
    private Vector3 sideEnd;
    private Vector3 endStart;
    private Vector3 endEnd;
    private Vector3 sideFacingStart;
    private Vector3 cornerFacingSide;
    private Vector3 cornerFacingEnd;


    private void checkCushionCollision()
    {
        if (!previewCollision) { return; }
        float signX = Mathf.Sign(this.transform.localPosition.x);
        float signZ = Mathf.Sign(this.transform.localPosition.z);

        Vector3[][] lines = new Vector3[][] {
            new Vector3[] {sideFacingStart, sideStart },
            new Vector3[] {sideStart, sideEnd},
            new Vector3[] {sideEnd, cornerFacingSide },
            new Vector3[] {endStart, endEnd },
            new Vector3[] {endEnd, cornerFacingEnd },
        };

        float xc = Mathf.Abs(this.transform.localPosition.x);
        float zc = Mathf.Abs(this.transform.localPosition.z);

        foreach (Vector3[] line in lines)
        {
            float slopeNum = (line[1].z - line[0].z);
            float slopeDen = (line[1].x - line[0].x);
            float b = line[0].z - slopeNum / slopeDen * line[0].x;
            if (float.IsNaN(b)) b = 0;

            float A = -slopeNum;
            float B = slopeDen;
            float C = -b * slopeDen;

            float d = Mathf.Abs(A * xc + B * zc + C) / Mathf.Sqrt(A * A + B * B);

            if (d <= k_BALL_RADIUS)
            {
                drawPlane(line[0], line[1], Color.blue);
            }
        }
    }

    private float inchToCm(float val)
    {
        return val * 2.54f;
    }

    private float inchToM(float val)
    {
        return inchToCm(val) / 100f;
    }
}
