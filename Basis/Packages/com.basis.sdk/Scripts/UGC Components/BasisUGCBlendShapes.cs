using Basis.Scripts.Behaviour;
using LiteNetLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.UGC.BlendShapes
{
    public class BasisUGCBlendShapes : BasisAvatarMonoBehaviour
    {
        public SkinnedMeshRenderer BlendShapeRenderer;

        [SerializeField]
        public List<BasisUGCBlendShapesItem> basisUGCBlendShapesItems;

        public int SelectedItemIndex = 0;

        [System.Serializable]
        public struct BasisUGCBlendShapesItem
        {
            public BasisUGCMenuDescription Description;
            public List<BasisUGCBlendShapeSettings> BlendShapeSettings;
            public int BlendShapeCount;
            public BasisBlendShapeMode Mode;
        }

        [System.Serializable]
        public struct BasisUGCBlendShapeSettings
        {
            public string BlendShapeName;
            [Range(0, 100)]
            public float Value;
            [HideInInspector]
            public bool HasIndexAssigned;
            [HideInInspector]
            public int BlendShapeIndex;
            [HideInInspector]
            public float CurrentValue;
            [HideInInspector]
            public float TargetValue;
            [HideInInspector]
            public float Speed;
        }

        public enum BasisBlendShapeMode
        {
            SetTo,
            Slider
        }

        public int BlendShapeCount;
        public bool HasIssue = false;

        public void Start()
        {
            Initalize();
        }

        public void Initalize()
        {
            if (BlendShapeRenderer == null || BlendShapeRenderer.sharedMesh == null)
            {
                BasisDebug.LogError("BlendShapeRenderer or its sharedMesh is not assigned.");
                return;
            }

            BlendShapeCount = basisUGCBlendShapesItems.Count;

            for (int Index = 0; Index < BlendShapeCount; Index++)
            {
                BasisUGCBlendShapesItem item = basisUGCBlendShapesItems[Index];
                item.BlendShapeCount = item.BlendShapeSettings.Count;
                for (int BlendShapeIndex = 0; BlendShapeIndex < item.BlendShapeCount; BlendShapeIndex++)
                {
                    BasisUGCBlendShapeSettings setting = item.BlendShapeSettings[BlendShapeIndex];
                    setting.BlendShapeIndex = BlendShapeRenderer.sharedMesh.GetBlendShapeIndex(setting.BlendShapeName);
                    setting.HasIndexAssigned = true;
                    setting.CurrentValue = 0;
                    setting.TargetValue = setting.Value;
                    setting.Speed = 10f;
                    item.BlendShapeSettings[BlendShapeIndex] = setting;
                }

                basisUGCBlendShapesItems[Index] = item;
            }

            HasIssue = BlendShapeRenderer == null || basisUGCBlendShapesItems == null || SelectedItemIndex < 0 || SelectedItemIndex >= basisUGCBlendShapesItems.Count;
        }

        public void LateUpdate()
        {
            if (HasIssue)
            {
                return;
            }
            if(IsLocallyOwned == false)
            {
                return;
            }

            UpdateBlendShapes(basisUGCBlendShapesItems[SelectedItemIndex]);
        }

        private void UpdateBlendShapes(BasisUGCBlendShapesItem item)
        {
            for (int Index = 0; Index < item.BlendShapeCount; Index++)
            {
                BasisUGCBlendShapeSettings setting = item.BlendShapeSettings[Index];

                if (!setting.HasIndexAssigned)
                {
                    continue;
                }

                    // Interpolation
                    setting.CurrentValue = Mathf.MoveTowards(setting.CurrentValue, setting.TargetValue, setting.Speed * Time.deltaTime);
                BlendShapeRenderer.SetBlendShapeWeight(setting.BlendShapeIndex, setting.CurrentValue);

                item.BlendShapeSettings[Index] = setting;
                SendBlendShapeUpdate(setting.BlendShapeIndex, setting.CurrentValue, setting.Speed);
            }

            basisUGCBlendShapesItems[SelectedItemIndex] = item;
        }

        public void SendBlendShapeUpdate(int blendShapeIndex, float targetValue, float speed)
        {
            UGCBlendshapeNetworkMessage msg = new UGCBlendshapeNetworkMessage
            {
                BlendShapeIndex = (byte)blendShapeIndex,
                TargetValue = (byte)Mathf.Clamp(targetValue, 0, 100),
                Speed = (byte)Mathf.Clamp(speed, 0, 255)
            };

            byte[] buffer = new byte[3];
            buffer[0] = msg.BlendShapeIndex;
            buffer[1] = msg.TargetValue;
            buffer[2] = msg.Speed;

            NetworkMessageSend(buffer, DeliveryMethod.Sequenced);
        }

        public class UGCBlendshapeNetworkMessage
        {
            public byte BlendShapeIndex;
            public byte TargetValue;
            public byte Speed;
        }

        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            if (buffer.Length < 3)
                return;

            int index = buffer[0];
            float targetValue = buffer[1];
            float speed = buffer[2];

            if (SelectedItemIndex < 0 || SelectedItemIndex >= basisUGCBlendShapesItems.Count)
                return;

            var item = basisUGCBlendShapesItems[SelectedItemIndex];

            for (int i = 0; i < item.BlendShapeSettings.Count; i++)
            {
                if (item.BlendShapeSettings[i].BlendShapeIndex == index)
                {
                    var setting = item.BlendShapeSettings[i];
                    setting.TargetValue = targetValue;
                    setting.Speed = speed;
                    item.BlendShapeSettings[i] = setting;
                    break;
                }
            }

            basisUGCBlendShapesItems[SelectedItemIndex] = item;
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            // Optionally handle server-side optimizations
        }
        public override void OnNetworkChange(byte messageIndex,bool IsLocallyOwned)
        {
            // Optionally handle network change
        }
    }
}
