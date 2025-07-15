

using UnityEngine;

using TMPro;
using System.Data;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis;


public class GraphicsManager : MonoBehaviour
{
    [Header("4 Ball")]
    [SerializeField] GameObject fourBallPoint;
    [SerializeField] Mesh fourBallMeshPlus;
    [SerializeField] Mesh fourBallMeshMinus;
    [Header("Snooker")]
    [SerializeField] TextMeshProUGUI blueScore;
    [SerializeField] TextMeshProUGUI orangeScore;
    [SerializeField] TextMeshProUGUI snookerInstruction;

    [Header("Text")]
    [SerializeField] TextMeshProUGUI[] playerNames;

    [SerializeField] TextMeshPro winnerText;
    GameObject winnerText_go;

    [Header("Cues")]
    [SerializeField] MeshRenderer[] cueBodyRenderers;
    [SerializeField] MeshRenderer[] cuePrimaryGripRenderers;
    [SerializeField] MeshRenderer[] cueSecondaryGripRenderers;

    [Header("Textures")]
    [SerializeField] bool usColors = true;
    [SerializeField] Texture usColorTexture;
    [SerializeField] Color[] usColorArr;

    [SerializeField] GameObject[] timers;

    private Mesh[] meshOverrideFourBall = new Mesh[4];
    private Mesh[] meshOverrideRegular = new Mesh[4];

    private Color gripColorActive = new Color(0.0f, 0.5f, 1.1f, 1.0f);
    private Color gripColorInactive = new Color(0.34f, 0.34f, 0.34f, 1.0f);

    private BilliardsModule table;

    private Material[] tableMaterial;
    private Material ballMaterial;
    private Material shadowMaterial;

    private bool fourBallPointActive;
    private float fourBallPointTime;

    private float introAnimationTime = 0.0f;

    private uint ANDROID_UNIFORM_CLOCK = 0x00u;
    private uint ANDROID_CLOCK_DIVIDER = 0x8u;

    private BasisNetworkPlayer[] savedPlayers = new BasisNetworkPlayer[4];

    private Material scorecard;
    private GameObject scorecard_gameobject;
    private GameObject scorecard_info;
    private Color[] scorecardColors = new Color[15];

    private bool shadowsDisabled;

    private GameObject[] balls;
    private Transform[] ballTransforms;
    private Vector3[] ballPositions;

    public void _Init(BilliardsModule table_)
    {
        table = table_;

        // copy some temporaries
        balls = table.balls;
        ballPositions = table.ballsP;

        ballTransforms = new Transform[balls.Length];
        for (int i = 0; i < balls.Length; i++)
        {
            ballTransforms[i] = balls[i].transform;
        }

        winnerText_go = winnerText.gameObject;

        Material[] materials = balls[0].GetComponent<MeshRenderer>().materials; // create a new instance for this table
        ballMaterial = materials[0];
        shadowMaterial = materials[1];
        ballMaterial.name = ballMaterial.name + " for " + table_.gameObject.name;
        shadowMaterial.name = shadowMaterial.name + " for " + table_.gameObject.name;

        Material[] newMaterials = new Material[] { ballMaterial, shadowMaterial };
        for (int i = 0; i < balls.Length; i++)
        {
            balls[i].GetComponent<MeshRenderer>().materials = newMaterials;
        }

        for (int i = 0; i < 4; i++)
        {
            meshOverrideFourBall[i] = balls[12 + i].GetComponent<MeshFilter>().sharedMesh;
        }
        meshOverrideRegular[0] = balls[0].GetComponent<MeshFilter>().sharedMesh;
        for (int i = 0; i < 3; i++)
        {
            meshOverrideRegular[i + 1] = balls[13 + i].GetComponent<MeshFilter>().sharedMesh;
        }
    }

    public void _InitializeTable()
    {
        Transform tableBase = table._GetTableBase().transform;
        scorecard_gameobject = tableBase.transform.Find("scorecard").gameObject;
        scorecard = scorecard_gameobject.GetComponent<MeshRenderer>().material;
        scorecard_info = table.transform.Find("intl.scorecardinfo").gameObject;

        for (int i = 0; i < table.tableModels[table.tableModelLocal].tableMaterial.Length; i++)
        {
            tableMaterial = table.tableModels[table.tableModelLocal].tableMaterial;
        }
        if (table.tableModels[table.tableModelLocal].tableMaterial.Length == 0)
            Debug.LogWarning("Table material not found, make sure you set Table Mesh correctly on Model Data");

        _SetShadowsDisabled(false);
        _SetUpReflectionProbe();

        _DisableObjects();
    }

    public void _Tick()
    {
        tickBallPositions();
        tickFourBallPoint();
        tickIntroAnimation();
        tickTableColor();
        tickWinner();
    }

    private void tickBallPositions()
    {
        if (!table.gameLive) return;

        uint ball_bit = 0x1u;
        uint pocketed = table.ballsPocketedLocal;
        for (int i = 0; i < 16; i++)
        {
            if ((ball_bit & pocketed) == 0x0u)
            {
                ballTransforms[i].localPosition = ballPositions[i];
            }

            ball_bit <<= 1;
        }
    }

    private void tickFourBallPoint()
    {
        if (!fourBallPointActive) return;

        // Evaluate time
        fourBallPointTime += Time.deltaTime * 0.25f;

        // Sustained step
        float s = Mathf.Max(fourBallPointTime - 0.1f, 0.0f);
        float v = Mathf.Min(fourBallPointTime * fourBallPointTime * 100.0f, 21.0f * s * Mathf.Exp(-15.0f * s));

        // Exponential step
        float e = Mathf.Exp(-17.0f * Mathf.Pow(Mathf.Max(fourBallPointTime - 1.2f, 0.0f), 3.0f));

        float scale = e * v * 2.0f;

        // Set scale
        fourBallPoint.transform.localScale = new Vector3(scale, scale, scale);

        // Set position
        Vector3 temp = fourBallPoint.transform.localPosition;
        temp.y = fourBallPointTime * 0.5f;
        fourBallPoint.transform.localPosition = temp;

        // Particle death
        if (fourBallPointTime > 2.0f)
        {
            fourBallPointActive = false;
            fourBallPoint.SetActive(false);
        }
    }

    private void tickIntroBall(Transform ball, float offset)
    {
        float localTime = Mathf.Clamp(introAnimationTime - offset, 0.0f, 1.0f);
        float localTimeInverse = (1.0f - localTime) * (table.k_BALL_DIAMETRE / BilliardsModule.ballMeshDiameter);

        Vector3 temp = ball.localPosition;
        temp.y = Mathf.Abs(Mathf.Cos(localTime * 6.29f)) * localTime * 0.5f;
        ball.localPosition = temp;

        ball.localScale = new Vector3(localTimeInverse, localTimeInverse, localTimeInverse);
    }

    private void tickIntroAnimation()
    {
        if (introAnimationTime <= 0.0f) return;

        introAnimationTime -= Time.deltaTime;

        if (introAnimationTime < 0.0f)
            introAnimationTime = 0.0f;

        // Cueball drops late
        tickIntroBall(table.balls[0].transform, 0.33f);

        for (int i = 1; i < 16; i++)
        {
            tickIntroBall(table.balls[i].transform, 0.84f + i * 0.03f);
        }
    }


    private void tickTableColor()
    {
        if (tableCurrentColour == tableSrcColour) return;

#if HT_QUEST
      // Run uniform updates at a slower rate on android (/8)
      ANDROID_UNIFORM_CLOCK++;
      if (ANDROID_UNIFORM_CLOCK < ANDROID_CLOCK_DIVIDER) return;
      ANDROID_UNIFORM_CLOCK = 0x00u;
      const float multiplier = 24.0f;
#else
        const float multiplier = 3.0f;
#endif
        tableCurrentColour = Color.Lerp(tableCurrentColour, tableSrcColour, Time.deltaTime * multiplier);
        for (int i = 0; i < table.tableModels[table.tableModelLocal].tableMaterial.Length; i++)
        {
            tableMaterial[i].SetColor("_EmissionColor", tableCurrentColour);
        }
    }

    private void tickWinner()
    {
        if (!winnerText_go.activeSelf) return;

#if !HT_QUEST
        _FlashTableColor(tableSrcColour * (Mathf.Sin(Time.timeSinceLevelLoad * 3.0f) * 0.5f + 1.0f));
#endif

        winnerText_go.transform.localPosition = new Vector3(0.0f, Mathf.Sin(Time.timeSinceLevelLoad) * 0.1f, 0.0f);
        winnerText_go.transform.Rotate(Vector3.up, 90.0f * Time.deltaTime);
    }

    public void _SetScorecardPlayers(int[] players)
    {
        if (players[2] == -1 || !table.teamsLocal)
        {
            playerNames[0].text = "<size=13>" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[0]));
        }
        else
        {
            playerNames[0].text = "<size=7><line-height=8.25>" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[0])) + "\n" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[2]));
        }

        if (players[3] == -1 || !table.teamsLocal)
        {
            playerNames[1].text = "<size=13>" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[1]));
        }
        else
        {
            playerNames[1].text = "<size=7><line-height=8.25>" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[1])) + "\n" + _FormatName(BasisNetworkPlayer.GetPlayerById(players[3]));
        }
    }

    public void _OnGameReset()
    {
        _DisableObjects();
        if (table.gameLive)
        {
            winnerText.gameObject.SetActive(true);
            winnerText.text = "Game reset!";
            numGameResets++;
            table.SendCustomEventDelayedSeconds(disableWinnerText, 15f);
        }
        else
        {
            winnerText.gameObject.SetActive(false);
        }
    }

    int numGameResets = 0;
    public void disableWinnerText()
    {
        numGameResets--;
        if (numGameResets != 0) return;
        winnerText.gameObject.SetActive(false);
    }

    public void _ResetWinners()
    {
        winnerText.gameObject.SetActive(false);
    }

    public void _SetWinners(uint winnerId, int[] players)
    {
        BasisNetworkPlayer player1 = winnerId == 0 ? BasisNetworkPlayer.GetPlayerById(players[0]) : BasisNetworkPlayer.GetPlayerById(players[1]);
        BasisNetworkPlayer player2 = winnerId == 0 ? BasisNetworkPlayer.GetPlayerById(players[2]) : BasisNetworkPlayer.GetPlayerById(players[3]);

        winnerText.gameObject.SetActive(true);
        winnerText.gameObject.transform.localRotation = Quaternion.identity;
        if (player2 == null || !table.teamsLocal)
        {
            winnerText.text = _FormatName(player1) + " wins!";
        }
        else
        {
            winnerText.text = _FormatName(player1) + " and " + _FormatName(player2) + " win!";
        }
        numGameResets++;
        table.SendCustomEventDelayedSeconds(disableWinnerText, 15f);
    }

    public string _FormatName(BasisNetworkPlayer player)
    {
        if (player == null) { return "No one"; }
        if (player.displayName == null) return string.Empty;

        return $"{player.displayName}</color>";
    }
    public void _PlayIntroAnimation()
    {
        introAnimationTime = 2.0f;
    }

    public void _SpawnFourBallPoint(Vector3 pos, bool plus)
    {
        fourBallPoint.SetActive(true);
        fourBallPointActive = true;
        fourBallPointTime = 0.1f;

        fourBallPoint.GetComponent<MeshFilter>().sharedMesh = plus ? fourBallMeshPlus : fourBallMeshMinus;
        fourBallPoint.transform.localPosition = pos;
        fourBallPoint.transform.localScale = Vector3.zero;
        fourBallPoint.transform.LookAt(BasisNetworkPlayer.LocalPlayer.GetPosition());
    }

    public void _FlashTableLight()
    {
        tableCurrentColour *= 1.9f;
    }

    public void _FlashTableError()
    {
        tableCurrentColour = pColourErr;
    }

    public void _FlashTableColor(Color color)
    {
        tableCurrentColour = color;
    }

    // Shader uniforms
    //  *udon currently does not support integer uniform identifiers
#if USE_INT_UNIFORMS

int uniform_tablecolour;
int uniform_scorecard_colour0;
int uniform_scorecard_colour1;
int uniform_scorecard_info;
int uniform_marker_colour;
int uniform_cue_colour;

#else

    const string uniform_tablecolour = "_EmissionColor";
    const string uniform_clothcolour = "_Color";
    const string uniform_scorecard_colour0 = "_Colour0";
    const string uniform_scorecard_colour1 = "_Colour1";
    const string uniform_scorecard_info = "_Info";
    const string uniform_marker_colour = "_Color";
    const string uniform_cue_colour = "_ReColor";

#endif

    Color tableSrcColour = new Color(1.0f, 1.0f, 1.0f, 1.0f); // Runtime target colour
    Color tableCurrentColour = new Color(1.0f, 1.0f, 1.0f, 1.0f); // Runtime actual colour

    // 'Pointer' colours.
    Color pColour0;      // Team 0
    Color pColour1;      // Team 1
    Color pColour2;      // No team / open / 9 ball
    Color pColourErr;

    private void updateFourBallCues()
    {
        if (table.isPracticeMode)
        {
            cueBodyRenderers[0].material.SetColor(uniform_cue_colour, (table.teamIdLocal == 0 ? pColour0 : pColour1));
        }
        else
        {
            cueBodyRenderers[0].material.SetColor(uniform_cue_colour, pColour0 * (table.teamIdLocal == 0 ? 1.0f : 0.333f));
            cueBodyRenderers[1].material.SetColor(uniform_cue_colour, pColour1 * (table.teamIdLocal == 1 ? 1.0f : 0.333f));
        }
    }

    private void updateNineBallCues()
    {
        if (table.isPracticeMode)
        {
            cueBodyRenderers[0].material.SetColor(uniform_cue_colour, table.k_colour_default);
        }
        else
        {
            cueBodyRenderers[table.teamIdLocal].material.SetColor(uniform_cue_colour, table.k_colour_default);
            cueBodyRenderers[table.teamIdLocal ^ 0x1u].material.SetColor(uniform_cue_colour, table.k_colour_off);
        }
    }

    private void updateEightBallCues(uint teamId)
    {
        if (table.isPracticeMode)
        {
            if (!table.isTableOpenLocal)
            {
                cueBodyRenderers[0].material.SetColor(uniform_cue_colour, (teamId ^ table.teamColorLocal) == 0 ? pColour0 : pColour1);
            }
            else
            {
                cueBodyRenderers[0].material.SetColor(uniform_cue_colour, table.k_colour_default);
            }
        }
        else
        {
            if (!table.isTableOpenLocal)
            {
                cueBodyRenderers[table.teamColorLocal].material.SetColor(uniform_cue_colour, pColour0);
                cueBodyRenderers[table.teamColorLocal ^ 0x1u].material.SetColor(uniform_cue_colour, pColour1);
            }
            else
            {
                cueBodyRenderers[table.teamIdLocal].material.SetColor(uniform_cue_colour, table.k_colour_default);
                cueBodyRenderers[table.teamIdLocal ^ 0x1u].material.SetColor(uniform_cue_colour, table.k_colour_off);
            }
        }
    }

    private void updateCues(uint idsrc)
    {
        if (table.is4Ball) updateFourBallCues();
        else if (table.is9Ball) updateNineBallCues();
        else if (table.is8Ball) updateEightBallCues(idsrc);

        if (table.isPracticeMode)
        {
            cuePrimaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorActive);
            cueSecondaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorActive);
        }
        else
        {
            if (table.teamIdLocal == 0)
            {
                cuePrimaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorActive);
                cueSecondaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorActive);
                cuePrimaryGripRenderers[1].material.SetColor(uniform_marker_colour, gripColorInactive);
                cueSecondaryGripRenderers[1].material.SetColor(uniform_marker_colour, gripColorInactive);
            }
            else
            {
                cuePrimaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorInactive);
                cueSecondaryGripRenderers[0].material.SetColor(uniform_marker_colour, gripColorInactive);
                cuePrimaryGripRenderers[1].material.SetColor(uniform_marker_colour, gripColorActive);
                cueSecondaryGripRenderers[1].material.SetColor(uniform_marker_colour, gripColorActive);
            }
        }
    }

    private void updateTable(uint teamId)
    {
        if (table.is4Ball)
        {
            if ((teamId ^ table.teamColorLocal) == 0)
            {
                // Set table colour to blue
                tableSrcColour = pColour0;
            }
            else
            {
                // Table colour to orange
                tableSrcColour = pColour1;
            }
        }
        else if (table.is9Ball)
        {
            tableSrcColour = pColour2;
        }
        else if (table.isSnooker6Red)
        {
            if ((teamId ^ table.teamColorLocal) == 0)
            {
                tableSrcColour = pColour0;
            }
            else
            {
                tableSrcColour = pColour1;
            }
        }
        else
        {
            if (!table.isTableOpenLocal)
            {
                if ((teamId ^ table.teamColorLocal) == 0)
                {
                    // Set table colour to blue
                    tableSrcColour = pColour0;
                }
                else
                {
                    // Table colour to orange
                    tableSrcColour = pColour1;
                }
            }
            else
            {
                tableSrcColour = pColour2;
            }
        }
    }

    public void _UpdateTeamColor(uint teamId)
    {
        updateCues(teamId);
        updateTable(teamId);
    }

    public void _HideTimers()
    {
        timers[0].SetActive(false);
        timers[1].SetActive(false);
    }

    public void _ShowTimers()
    {
        if (!table.timerRunning) return;
        timers[0].SetActive(true);
        timers[1].SetActive(true);
    }

    public void _RefreshTimers()
    {
        if (!table.timerRunning || !table.gameLive)
        {
            _HideTimers();
            return;
        }
        timers[0].SetActive(true);
        timers[1].SetActive(true);
    }

    public void _SetTimerPercentage(float pct)
    {
        for (int i = 0; i < timers.Length; i++)
        {
            timers[i].GetComponent<MeshRenderer>().material.SetFloat("_TimeFrac", pct);
        }
    }

    public void _ShowBalls()
    {
        if (table.is9Ball)
        {
            for (int i = 0; i <= 9; i++)
                table.balls[i].SetActive(true);

            for (int i = 10; i < 16; i++)
                table.balls[i].SetActive(false);
        }
        else if (table.is4Ball)
        {
            for (int i = 1; i < 16; i++)
                table.balls[i].SetActive(false);

            table.balls[0].SetActive(true);
            table.balls[13].SetActive(true);
            table.balls[14].SetActive(true);
            table.balls[15].SetActive(true);
        }
        else if (table.isSnooker6Red)
        {
            for (int i = 0; i < 13; i++)
                table.balls[i].SetActive(true);
            for (int i = 13; i < 16; i++)
                table.balls[i].SetActive(false);
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                table.balls[i].SetActive(true);
            }
        }
    }

    public void _OnLobbyOpened()
    {
        _DisableObjects();
    }

    public void _OnLobbyClosed()
    {
        winnerText.gameObject.SetActive(true);
    }

    private uint loadedGameMode = uint.MaxValue;
    public void _OnGameStarted()
    {
        if (table.gameModeLocal == uint.MaxValue || table.gameStateLocal != 2 || (scorecard_info.activeSelf && table.gameModeLocal == loadedGameMode)) { return; }
        loadedGameMode = table.gameModeLocal;

        scorecard_info.SetActive(true);
        scorecard_gameobject.SetActive(true);
        scorecard.SetInt("_GameMode", (int)table.gameModeLocal);
        scorecard.SetInt("_SolidsMode", 0);
        scorecard_gameobject.SetActive(true);
        for (int i = 0; i < playerNames.Length; i++)
        {
            playerNames[i].gameObject.SetActive(true);
        }
        if (table.isSnooker6Red)
        {
            orangeScore.gameObject.SetActive(true);
            blueScore.gameObject.SetActive(true);
            snookerInstruction.gameObject.SetActive(true);
        }
        else
        {
            orangeScore.gameObject.SetActive(false);
            blueScore.gameObject.SetActive(false);
            snookerInstruction.gameObject.SetActive(false);
        }

        _UpdateTableColorScheme();
        _UpdateTeamColor(0);

        if (table.is4Ball)
        {
            balls[0].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[0];
            balls[13].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[1];
            balls[14].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[2];
            balls[15].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[3];
        }
        else
        {
            balls[0].GetComponent<MeshFilter>().sharedMesh = meshOverrideRegular[0];
            balls[13].GetComponent<MeshFilter>().sharedMesh = meshOverrideRegular[1];
            balls[14].GetComponent<MeshFilter>().sharedMesh = meshOverrideRegular[2];
            balls[15].GetComponent<MeshFilter>().sharedMesh = meshOverrideRegular[3];
        }

        _ShowBalls();
    }

    public void _UpdateTableColorScheme()
    {
        if (table.is9Ball)  // 9 Ball / USA colours
        {
            pColour0 = table.k_colour_default;
            pColour1 = table.k_colour_default;
            pColour2 = table.k_colour_default;

            pColourErr = table.k_colour_foul;

            // 9 ball only uses one colourset / cloth colour
            ballMaterial.SetTexture("_MainTex", table.textureSets[1]);
        }
        else if (table.is4Ball)
        {
            pColour0 = table.k_colour4Ball_team_0;
            pColour1 = table.k_colour4Ball_team_1;

            // Should not be used
            pColour2 = table.k_colour_foul;
            pColourErr = table.k_colour_foul;

            ballMaterial.SetTexture("_MainTex", table.textureSets[1]);
        }
        else if (table.isSnooker6Red)
        {
            pColourErr = table.k_colour_foul;
            pColour2 = table.k_colour_default;

            pColour0 = table.k_snookerTeamColour_0;
            pColour1 = table.k_snookerTeamColour_1;

            ballMaterial.SetTexture("_MainTex", table.textureSets[2]);
        }
        else // Standard 8 ball derivatives
        {
            pColourErr = table.k_colour_foul;
            pColour2 = table.k_colour_default;

            pColour0 = table.k_teamColour_spots;
            pColour1 = table.k_teamColour_stripes;

            ballMaterial.SetTexture("_MainTex", usColors ? usColorTexture : table.textureSets[0]);
        }
    }

    public void _DisableObjects()
    {
        table.guideline.SetActive(false);
        table.guideline2.SetActive(false);
        table.devhit.SetActive(false);
        winnerText.gameObject.SetActive(false);
        table.markerObj.SetActive(false);
        scorecard_info.SetActive(false);
        scorecard_gameobject.SetActive(false);
        table.marker9ball.SetActive(false);
        fourBallPoint.SetActive(false);
        orangeScore.gameObject.SetActive(false);
        blueScore.gameObject.SetActive(false);
        snookerInstruction.gameObject.SetActive(false);
        _HideTimers();

        winnerText.text = "";
    }

    // Finalize positions onto their rack spots
    public void _RackBalls()
    {
        uint ball_bit = 0x1u;

        for (int i = 0; i < 16; i++)
        {
            table.balls[i].GetComponent<Rigidbody>().isKinematic = true;

            if ((ball_bit & table.ballsPocketedLocal) == ball_bit)
            {
                // Recover Y position since its lost in networking
                Vector3 rack_position = table.ballsP[i];
                rack_position.y = table.k_rack_position.y;

                table.balls[i].transform.localPosition = rack_position;
            }

            ball_bit <<= 1;
        }
    }

    public void _UpdateLOD()
    {
        if (table.gameStateLocal > 1 && (table.networkingManager.NetworkedSyncInfo.winningTeamSynced != 2)) // game has started or finished, && hasn't been reset
        {
            _OnGameStarted(); // make sure all visuals are set

            scorecard_gameobject.SetActive(true);
            for (int i = 0; i < playerNames.Length; i++)
            {
                playerNames[i].gameObject.SetActive(true);
            }
            if (table.isSnooker6Red)
            {
                orangeScore.gameObject.SetActive(true);
                blueScore.gameObject.SetActive(true);
                snookerInstruction.gameObject.SetActive(true);
            }
            else
            {
                orangeScore.gameObject.SetActive(false);
                blueScore.gameObject.SetActive(false);
                snookerInstruction.gameObject.SetActive(false);
            }
            _UpdateScorecard();
        }
        _RefreshTimers();
    }

    public void _UpdateScorecard()
    {
        if (table.is4Ball)
        {
            scorecard.SetInt("_LeftScore", table.fbScoresLocal[0]);
            scorecard.SetInt("_RightScore", table.fbScoresLocal[1]);

            scorecardColors[0] = table.k_colour4Ball_team_0;
            scorecardColors[1] = table.k_colour4Ball_team_1;
            scorecard.SetColorArray("_Colors", scorecardColors);
        }
        else if (table.isSnooker6Red)
        {
            orangeScore.text = table.fbScoresLocal[0].ToString();
            blueScore.text = table.fbScoresLocal[1].ToString();
            bool redOnTable = table.sixRedCheckIfRedOnTable(table.ballsPocketedLocal, false);
            bool freeBall = table.foulStateLocal == 5;
            if (table.colorTurnLocal)
            {
                snookerInstruction.text = "Pot any color but red";
            }
            else
            {
                if (redOnTable)
                {
                    snookerInstruction.text = "Pot a Red";
                    if (freeBall)
                        snookerInstruction.text += " or free ball of choice";
                }
                else
                {
                    int nextcolor = table.sixRedFindLowestUnpocketedColor(table.ballsPocketedLocal);
                    if (nextcolor < 12 && nextcolor > -1)
                    {
                        snookerInstruction.text = "Pot " + table.sixRedNumberToColor(nextcolor, true);
                        if (freeBall)
                            snookerInstruction.text += " or free ball of choice";
                    }
                    else
                        snookerInstruction.text = string.Empty;
                }
            }
        }
        else
        {
            int[] counter0 = new int[2];

            uint temp = table.ballsPocketedLocal;

            for (int j = 0; j < 2; j++)
            {
                int counter = 0;
                int idx = (int)(j ^ table.teamColorLocal);
                for (int i = 0; i < 7; i++)
                {
                    if ((temp & 0x4) > 0)
                    {
                        counter0[idx]++;
                        if (usColors)
                        {
                            if (idx == 0) scorecardColors[counter] = usColorArr[i];
                            else if (idx == 1) scorecardColors[14 - counter] = usColorArr[i];
                            counter++;
                        }
                    }

                    temp >>= 1;
                }
            }

            if (!usColors)
            {
                for (int i = 0; i < 7; i++) scorecardColors[i] = (table.teamColorLocal == 0 ? pColour0 : pColour1) / 1.5f;
                for (int i = 0; i < 7; i++) scorecardColors[8 + i] = (table.teamColorLocal == 1 ? pColour0 : pColour1) / 1.5f;
            }

            // Add black ball if we are winning the thing
            if (!table.gameLive && table.winningTeamLocal < 2)
            {
                counter0[table.winningTeamLocal] += (int)((table.ballsPocketedLocal & 0x2) >> 1);
                if (!usColors)
                {
                    scorecardColors[7] = (table.winningTeamLocal == table.teamColorLocal ? pColour0 : pColour1) / 1.5f;
                }
                else
                {
                    scorecardColors[7] = Color.white * 0.1f;
                }
            }
            scorecard.SetInt("_LeftScore", counter0[0]);
            scorecard.SetInt("_RightScore", counter0[1]);
            scorecard.SetColorArray("_Colors", scorecardColors);

            if (table.isTableOpenLocal || !usColors)
            {
                scorecard.SetInt("_SolidsMode", 0);
            }
            else
            {
                scorecard.SetInt("_SolidsMode", table.teamColorLocal == 0 ? 1 : 2);
            }
        }
    }

    public void _UpdateFourBallCueBallTextures(uint fourBallCueBall)
    {
        if (!table.is4Ball) return;

        if (fourBallCueBall == 0)
        {
            table.balls[0].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[0];
            table.balls[13].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[1];
        }
        else
        {
            table.balls[13].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[0];
            table.balls[0].GetComponent<MeshFilter>().sharedMesh = meshOverrideFourBall[1];
        }
    }

    public bool _IsUSColors()
    {
        return usColors;
    }

    public void _SetUSColors(bool usColors_)
    {
        usColors = usColors_;

        if (table != null)
        {
            if (table.is8Ball)
            {
                for (int i = 0; i < 16; i++)
                {
                    ballMaterial.SetTexture("_MainTex", usColors ? usColorTexture : table.textureSets[0]);
                }
                _UpdateScorecard();
            }
        }
    }

    public bool _IsShadowsDisabled()
    {
        return shadowsDisabled;
    }

    public void _SetShadowsDisabled(bool shadowsDisabled_)
    {
        shadowsDisabled = shadowsDisabled_;

        if (table != null)
        {
            Material[] newMaterials;
            if (shadowsDisabled)
            {
                newMaterials = new Material[] { ballMaterial };
            }
            else
            {
                newMaterials = new Material[] { ballMaterial, shadowMaterial };

                float height = table._GetTableBase().transform.Find(".TABLE_SURFACE").position.y + 0.0025f;
                shadowMaterial.SetFloat("_Floor", height);
                shadowMaterial.SetFloat("_Scale", table.k_BALL_RADIUS / 0.03f);//0.03f is the radius of the ball's 3D mesh
            }
            for (int i = 0; i < 16; i++)
            {
                balls[i].GetComponent<MeshRenderer>().materials = newMaterials;
            }
        }
    }
    public void _SetUpReflectionProbe()
    {
        ReflectionProbe dynamicProbe = table.reflection_main;
        if (!dynamicProbe) { return; }

        float diagonal = Mathf.Sqrt(table.k_TABLE_HEIGHT * table.k_TABLE_HEIGHT + table.k_TABLE_WIDTH * table.k_TABLE_WIDTH) * 2;

        Transform probeTransform = table.reflection_main.transform;
        Transform tableBase = table._GetTableBase().transform.Find(".TABLE_SURFACE");
        probeTransform.position = tableBase.position + Vector3.up * 0.31f;
        Vector3 reflBounds = dynamicProbe.size;
        reflBounds.y = 0.6f;
        reflBounds.x = diagonal;
        reflBounds.z = diagonal;
        dynamicProbe.size = reflBounds;

        // prevent probe being rendered twice in one frame (late joiners)
        if (!renderingProbe)
        {
         table.SendCustomEventDelayedFrames(renderProbe, 1);
            renderingProbe = true;
        }
    }

    bool renderingProbe = false;
    public void renderProbe()
    {
        renderingProbe = false;
        table.reflection_main.RenderProbe();
    }
}
