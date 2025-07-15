
using UnityEngine;
using UnityEditor;


public class ModelData : MonoBehaviour
{
        public string TABLENAME = "Pool Table";
        [Header("Table Visuals")]
        public GameObject tableMesh;

        [Header("Table Properties")]
        [SerializeField] public float tableWidth = 1.06f;
        [SerializeField] public float tableHeight = 0.603f;
        [SerializeField] public float cushionRadius = 0.043f;
        [SerializeField] public float pocketWidthCorner = 0.1f;
        [SerializeField] public float pocketHeightCorner = 0.1f;
        [SerializeField] public float facingAngleCorner = 133f;
        [SerializeField] public float pocketRadiusSide = 0.1f;
        [SerializeField] public float pocketDepthSide = 0.04f;
        [SerializeField] public float facingAngleSide = 104.93142f;
        [SerializeField] public float railHeightUpper = 0.038002f;
        public bool useRailHeightLower = false;
        [SerializeField] public float railHeightLower = 0.028472f;
        [SerializeField] public float railDepthWidth = 0.12f;
        [SerializeField] public float railDepthHeight = 0.12f;
        [SerializeField] public float pocketInnerRadiusCorner = 0.072f;
        [SerializeField] public float pocketInnerRadiusCorner2 = 0.08f;
        [SerializeField] public float pocketInnerRadiusSide = 0.072f;
        [SerializeField] public float pocketInnerRadiusSide2 = 0.08f;
        [SerializeField] public Vector3 cornerPocket = new Vector3(1.09f, 0, 0.633f);
        [SerializeField] public Vector3 cornerPocket2 = new Vector3(1.12f, 0, 0.66f);
        [SerializeField] public Vector3 sidePocket = new Vector3(0, 0, 0.665f);
        [SerializeField] public Vector3 sidePocket2 = new Vector3(0, 0, 0.7f);

        [Header("Ball-Table Coefficients:")]


        [Tooltip("This is the friction of Slide, the time the ball transition from sliding to rolling, " +
                "\n<b>[DEFAULT 0.2] [Research Valid Ranges are 0.15 - 0.4]</b>, <i>only numerical values in MU</i>")]
        [SerializeField] public float bt_CoefSlide = 0.2f; // k_F_SLIDE


        [Tooltip("This is the friction of Roll, once the ball enters a pure roll state this friction is used to slow down the ball" +
                "\n\n<b>[DEFAULT 0.008] [Research Valid Range 0.005 - 0.015]</b>, " +
                "\n<i>only numerical values in MU</i>" +
                "\n\n<i>[tip: this Generally decides how FAST or SLOW your cloth is, a value of 0.01 is MEDIUM/AVERAGE,</i> " +
                "\n<i>but professional tables often feature a fast (i.e clean) cloth,</i> " +
                "\n<i>or even specific cloths crafted for specific games, such case is the Simonis 860 HR crafted for 9 Ball,</i> " +
                "\n<i>meanwhile Home and Pool Hall tables are often medium and durable, while bar tables would be the slowest due to their average poor treatment condition (depends on the owner of the bar and the rules they apply for when anybody uses the table)</i>")]
        [SerializeField] public float bt_CoefRoll = 0.008f; // k_F_ROLL


        [Tooltip("When the ball is spinning i.e has English effects applied, you can see this state as an intersection between sliding and rolling states, " +
                "\n\nthis is the friction that governs how FAST or SLOW the sidespin applied will reach 0. (Usually measured in Radians over Second Squared, see bellow!). " +
                "\n\nhowever if you have an actual data for this value in MU you can plug it here and disable the bool bellow. " +
                "\n\n<b>[DEFAULT: 0.022 - Joris van Balen / 0.044 Marlow Data]</b>" +
                "\n, <i>their data often returns a rate higher than spec, feel free to try!.</i> " +
                "\n\n<i>insert only numerical values in MU.</i>")]
        [SerializeField] public float bt_CoefSpin = 0.022f; // k_F_SPIN


        [Tooltip("Valid only when using Spin is Decleration Rate = TRUE) - " +
                "\n\nIf you dont have the friction value in MU but have it in (rad/sec²), you can use this instead " +
                "\n\nthe code will calculate the exact amount of friction in MU needed to achieve its deceleration rate for you no matter your ball Mass and Radius. " +
                "\n\n<b>[DEFAULT 5.0122876] [Valid Range 5 - 15]</b>")]
        [SerializeField, Range(5f, 15f)] public float bt_CoefSpinRate = 5.0122876f; // k_F_SPIN_RATE


        [Tooltip("Overrides the Coefficient of Spin in MU above and uses Deceleration Rate in rad/sec² instead" +
                "\n\n <b>[DEFAULT = TRUE]</b>")]
        [SerializeField] public bool bt_ConstDecelRate = true; // isDRate


        [Tooltip("Coefficient of restitution between ball and table/slate, " +
                "\nthis is the bounce the ball has from the table" +
                "\n\n<b>[DEFAULT = 0.5] [VALID RANGES 0.5 - 0.7]</b>")]
        [SerializeField] public float bt_BounceFactor = 0.5f; // K_BOUNCE_FACTOR

        [SerializeField] public float bt_PocketRestitutionFactor = 0.7f; // k_POCKET_RESTITUTION

        /// End of Ball-Table Header ---


        [Header("Cushion Model:")]


        [Tooltip("This Replaces the current Default Model with a similar and NEW one, " +
                "\n\nwhich includes: " +
                "\n\n-Friction, " +
                "\n\n-Jumps from Cushion" +
                "\n\n-Accessible Coefficient of Restitution, " +
                "\n\nworks abroad every component of the ball [Displacement, Velocity, Angular Velocity and their Inertia tensors " +
                "\n\n<b>[Default = ON]</b>" +
                "\n\n<i>When Disabling this, the table will use its Default Derived Model (Previous Version 1.0.3), this may be useful if you prefer Default Model.</i>")]
        [SerializeField] public bool bc_UseHan05 = true; // isHanModel


        [Tooltip("How bouncy your cushions are! Another interpretation is how much speed the cushion will absorb from the ball." +
                "\n\n<b>[Default 0.85] Provided by Joris van Balen - [Constrained to Valid Range]</b>" +
                "\n\n<i>[High quality Cushion are very Bouncy, but what governs these characteristics is not only the Quality of the rubber being used but also its Profile, Rubber stiffness and other factors!]</i>" +
                "\n\n<i>(normally This coefficient is Dynamic and it reacts differently at different shot speeds, However a Fixed Constant value works really well for a lot of shots!, if you wish to use a Dynamic Resolution, check out the last bool below!)</i>" +
                "\n\n<i>Beware this Value has a high influence on the final angle the ball will head due to the nature of physics)</i>")]
        [SerializeField, Range(0.5f, 0.98f)] public float bc_CoefRestitution = 0.85f; // k_E_C


        [Tooltip("since Version 0.5M, This option overrides all values above and uses another Fixed Linear Dynamic Restitution) " +
                "\n\n<b>[Default - OFF]</b>" +
                "\n\n<i>(this resolution seems to work well, but results in shots may vary a little)</i>")]
        [SerializeField] public bool bc_DynRestitution = false; // isDynamicRestitution


        [Tooltip("Replaces Cushion Friction model to a constant value set bellow")]
        [SerializeField] public bool bc_UseConstFriction = false; // isCushionFrictionConstant


        [Tooltip("This parameter seems to work best at Dynamic Ranges and by default it is emperically determined and HardCoded." +
                "\n\n if you wish to use a constant, you can do it here by checking the above bool! " +
                "\n\n<b>[Valid Ranges are 0.2 - 0.4]</b>")]
        [SerializeField, Range(0.1f, 0.4f)] public float bc_ConstFriction = 0.2f; // k_Cushion_MU


        /// End of Cushion Model Header ---


        [Header("Ball Set Configuration:")]


        [Tooltip("This controls how bouncy your balls are when colliding with each other " +
                "\n\n<b>[Default 0.98] [Acceptable range constrained]</b> " +
                "\n\n<i>(For `assuming perfectly elastic collisions` set to = 1</i>" +
                "\n\n<i>Ball inelasticity is described by a technical factor called the <b>coefficient of restitution</b>. It describes how well the cue ball delivers energy to the object ball during impact. In other words, it quantifies how much energy is lost during impact. If the balls were perfect, and if they were colliding in a vacuum (i.e., with no surrounding air), there would be no loss in energy and the coefficient of restitution would be 1.00 or 100%. However, actual pool balls are not perfectly elastic, and when they collide in air some energy is lost to sound. That’s right ... if you can hear a sound when the balls collide, that means energy of motion is being lost to acoustic (sound) energy. Most experimental numbers I have seen for the coefficient of restitution for pool balls have been in the range 0.90 to 0.96.</i> - David Alciatore")]
        [SerializeField, Range(0.92f, 1f)] public float bs_CoefRestitution = 0.98f; // k_BALL_E


        [Tooltip("The amount of friction at the surface of the balls, Derived from a theoretical curve fit using Marlow Data, all you are doing with this value is scaling the curve and <b>not</b> changing its shape." +
                "\n\n<b>Default = 1 [Valid Ranges 0.5 - 1.5]</b> " +
                "\n\n<i>(For `assuming frictionless response` set = 0</i>" +
                "\n\n<i>Friction between the cue ball and the object ball creates a sideways force between the balls during impact. It is caused by relative sliding motion between the balls, due to cut angle or cue ball spin. Ball friction is what causes throw effects often referred to as English throw, cut-induced throw (CIT), cut throw, spin-induced throw (SIT), drag, collision-induced throw, cling, skid, kick, drag, or push..]</i> - David Alciatore")]
        [SerializeField, Range(0f, 1.5f)] public float bs_Friction = 1f; // muFactor


        [Tooltip("Ball diameter in milimeters")]
        [SerializeField] public float bs_BallDiameter = 60f;
        [SerializeField] public float bs_BallMass = 0.16f;

        /// End of Ball Set Header ---


        [Header("Snooker:")]
        [SerializeField] public float baulkLine = 0.7367f;
        [SerializeField] public float semiCircleRadius = 0.2921f;
        [SerializeField] public float pinkSpot = 0.892251f;
        [SerializeField] public float blackSpot = 1.46048f;
        [Header("Cue Positions:")]
        [SerializeField] public Transform CueOrange;
        [SerializeField] public Transform CueBlue;
        [SerializeField] public float DesktopUIScaleFactor = 1.08f;

        [System.NonSerialized] public Material[] tableMaterial;
        public void _Init()
        {
                MeshRenderer[] tableMeshR = tableMesh.GetComponentsInChildren<MeshRenderer>();
                for (int i = 0; i < tableMeshR.Length; i++)
                {
                        tableMaterial = tableMeshR[i].materials; // create a new instance for this table
                        for (int o = 0; o < tableMaterial.Length; o++)
                        {
                                tableMaterial[o].name = " for " + gameObject.name;
                                tableMeshR[i].materials[o] = tableMaterial[o];
                        }
                }
        }
}
