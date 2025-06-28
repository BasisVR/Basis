using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.UI.UI_Panels;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BasisUIAdminPanel : BasisUIBase
{
    public static string Path = "Packages/com.basis.sdk/Prefabs/UI/BasisUIAdminPanel.prefab";
    public static string CursorRequest = "BasisUIAdminPanel";
    public BasisUIAdminButton ButtonPrefab;
    public Transform Parent;
    public BasisNetworkPlayer SelectedPlayer;
    public override void DestroyEvent()
    {
        BasisCursorManagement.LockCursor(CursorRequest);
    }

    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(CursorRequest);
    }
    public void OnEnable()
    {
        BindButtons();
        GenerateButtons();
        BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayerJoined;
        BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayerJoined;
    }
    public void OnDestroy()
    {
        BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayerJoined;
        BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayerJoined;
    }
    private void OnRemotePlayerJoined(BasisNetworkPlayer player1, BasisRemotePlayer player2)
    {
        GenerateButtons();
    }

    public List<BasisUIAdminButton> AdminButtons = new List<BasisUIAdminButton>();
    public void GenerateButtons()
    {
        foreach (BasisUIAdminButton Button in AdminButtons)
        {
            GameObject.Destroy(Button.gameObject);
        }
        AdminButtons.Clear();
        foreach (BasisNetworkPlayer Player in BasisNetworkManagement.Players.Values)
        {
            var buttonObject = Instantiate(ButtonPrefab.gameObject, Parent);
            buttonObject.name = Player.Player.DisplayName;
            buttonObject.SetActive(true);
            if (buttonObject.TryGetComponent<BasisUIAdminButton>(out BasisUIAdminButton BasisUIAdminButton))
            {
                AdminButtons.Add(BasisUIAdminButton);

                BasisUIAdminButton.Button.onClick.AddListener(() => OnClick(Player));
                BasisUIAdminButton.DisplayName.text = Player.Player.SafeDisplayName;
                BasisUIAdminButton.PlayerUUID.text = $"{Player.Player.UUID}";
                BasisUIAdminButton.NetworkID.text = $"{Player.NetId}";
            }
        }
    }
    private void BindButtons()
    {
        TeleportAll.onClick.AddListener(() => BasisNetworkModeration.TeleportAll(SelectedPlayer.NetId));
        Ban.onClick.AddListener(() => BasisNetworkModeration.SendBan(UUIDSubmission.text, ReasonSubmission.text));
        Kick.onClick.AddListener(() => BasisNetworkModeration.SendKick(UUIDSubmission.text, ReasonSubmission.text));
        IpBan.onClick.AddListener(() => BasisNetworkModeration.SendIPBan(UUIDSubmission.text, ReasonSubmission.text));
        Uban.onClick.AddListener(() => BasisNetworkModeration.UnBan(UUIDSubmission.text));
        AddAdmin.onClick.AddListener(() => BasisNetworkModeration.AddAdmin(UUIDSubmission.text));
        RemoveAdmin.onClick.AddListener(() => BasisNetworkModeration.RemoveAdmin(UUIDSubmission.text));
        SendFirstMessage.onClick.AddListener(() =>
        {
            if (FindID(UUIDSubmission.text, out ushort Id))
            {
                BasisNetworkModeration.SendMessage(Id, ReasonSubmission.text);
            }
            else
            {
                BasisDebug.LogError("Cant find ID " + UUIDSubmission.text);
            }
        });
        SendMessageAll.onClick.AddListener(() => BasisNetworkModeration.SendMessageAll(ReasonSubmission.text));

        TeleportTo.onClick.AddListener(() =>
        {
            if (FindID(UUIDSubmission.text, out ushort Id))
            {
                BasisNetworkModeration.TeleportTo(Id);
            }
            else
            {
                BasisDebug.LogError("Cant find ID " + UUIDSubmission.text);
            }
        });
        TeleportHere.onClick.AddListener(() =>
        {
            if (FindID(UUIDSubmission.text, out ushort Id))
            {
                BasisNetworkModeration.TeleportHere(Id);
            }
            else
            {
                BasisDebug.LogError("Cant find ID " + UUIDSubmission.text);
            }
        });
    }
    public bool FindID(string UUID, out ushort Id)
    {
        foreach (BasisNetworkPlayer Player in BasisNetworkManagement.Players.Values)
        {
            if (UUID == Player.Player.UUID)
            {
                Id = Player.NetId;
                return true;
            }
        }
        Id = 0;
        return false;
    }
    private void OnClick(BasisNetworkPlayer Player)
    {
        SelectedPlayer = Player;
        UUIDSubmission.text = SelectedPlayer.Player.UUID;
    }
    public Button TeleportAll;
    public Button Ban;
    public Button Kick;
    public Button IpBan;
    public Button Uban;
    public Button AddAdmin;
    public Button RemoveAdmin;

    public Button SendFirstMessage;
    public Button SendMessageAll;

    public Button TeleportTo;
    public Button TeleportHere;

    public TMP_InputField UUIDSubmission;
    public TMP_InputField ReasonSubmission;
}
