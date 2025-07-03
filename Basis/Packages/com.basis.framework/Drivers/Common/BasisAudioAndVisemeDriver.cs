using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using uLipSync;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
namespace Basis.Scripts.Drivers
{
    [System.Serializable]
    public partial class BasisAudioAndVisemeDriver
    {
        public int smoothAmount = 70;
        public bool[] HasViseme;
        public int BlendShapeCount;
        public BasisPlayer Player;
        public BasisAvatar Avatar;
        public uLipSync.uLipSync uLipSync;
        public uLipSyncBlendShape uLipSyncBlendShape;
        public List<BasisPhonemeBlendShapeInfo> phonemeBlendShapeTable = new List<BasisPhonemeBlendShapeInfo>();
        public bool FirstTime = false;
        public bool WasSuccessful;
        public int HashInstanceID = -1;
        public bool TryInitialize(BasisPlayer BasisPlayer)
        {
            WasSuccessful = false;
            Avatar = BasisPlayer.BasisAvatar;
            Player = BasisPlayer;
            if (Avatar == null)
            {
             //  BasisDebug.Log("not setting up BasisVisemeDriver Avatar was null");
                return false;
            }
            if (Avatar.FaceVisemeMesh == null)
            {
              //  BasisDebug.Log("not setting up BasisVisemeDriver FaceVisemeMesh was null");
                return false;
            }
            if (Avatar.FaceVisemeMesh.sharedMesh.blendShapeCount == 0)
            {
              //  BasisDebug.Log("not setting up BasisVisemeDriver blendShapeCount was empty");
                return false;
            }
            if (uLipSync == null)
            {
                FirstTime = true;
            }
            if (uLipSync == null)
            {
                uLipSync = BasisHelpers.GetOrAddComponent<uLipSync.uLipSync>(BasisPlayer.gameObject);
            }
            phonemeBlendShapeTable.Clear();
            if (uLipSync.profile == null)
            {
                uLipSync.profile = BasisDeviceManagement.LipSyncProfile.Result;
            }
            if (uLipSyncBlendShape == null)
            {
                uLipSyncBlendShape = BasisHelpers.GetOrAddComponent<uLipSyncBlendShape>(BasisPlayer.gameObject);
            }
            uLipSyncBlendShape.usePhonemeBlend = true;
            uLipSyncBlendShape.skinnedMeshRenderer = Avatar.FaceVisemeMesh;
            BlendShapeCount = Avatar.FaceVisemeMovement.Length;
            HasViseme = new bool[BlendShapeCount];
            for (int Index = 0; Index < BlendShapeCount; Index++)
            {
                if (Avatar.FaceVisemeMovement[Index] != -1)
                {
                    int FaceVisemeIndex = Avatar.FaceVisemeMovement[Index];
                    HasViseme[Index] = true;
                    switch (Index)
                    {
                        case 10:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "A",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }

                        case 12:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "I",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }

                        case 14:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "U",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }

                        case 11:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "E",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }
                        case 13:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "O",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }
                        case 7:
                            {
                                BasisPhonemeBlendShapeInfo PhonemeBlendShapeInfo = new BasisPhonemeBlendShapeInfo
                                {
                                    phoneme = "S",
                                    blendShape = FaceVisemeIndex
                                };
                                phonemeBlendShapeTable.Add(PhonemeBlendShapeInfo);
                                break;
                            }

                    }
                }
                else
                {
                    HasViseme[Index] = false;
                }
            }
            uLipSyncBlendShape.CachedblendShapes.Clear();
            for (int Index = 0; Index < phonemeBlendShapeTable.Count; Index++)
            {
                BasisPhonemeBlendShapeInfo info = phonemeBlendShapeTable[Index];
                uLipSyncBlendShape.AddBlendShape(info.phoneme, info.blendShape);
            }
            uLipSyncBlendShape.BlendShapeInfos = uLipSyncBlendShape.CachedblendShapes.ToArray();
            if (FirstTime)
            {
                uLipSync.uLipSyncBlendShape = uLipSyncBlendShape;
            }
            uLipSync.Initalize();
            if (Player != null && Player.FaceRenderer != null && HashInstanceID != Player.FaceRenderer.GetInstanceID())
            {
               // BasisDebug.Log("Wired up Renderer Check For Blinking");
                Player.FaceRenderer.Check += UpdateFaceVisibility;
                Player.FaceRenderer.DestroyCalled += TryShutdown;
            }
            UpdateFaceVisibility(Player.FaceIsVisible);
            WasSuccessful = true;
            return true;
        }
        public void TryShutdown()
        {
            WasSuccessful = false;
            OnDeInitalize();
        }
        public bool uLipSyncEnabledState = true;
        private void UpdateFaceVisibility(bool State)
        {
            uLipSyncEnabledState = State;
        }
        public void OnDeInitalize()
        {
            if (Player != null)
            {
                if (Player.FaceRenderer != null && HashInstanceID == Player.FaceRenderer.GetInstanceID())
                {
                    Player.FaceRenderer.Check -= UpdateFaceVisibility;
                    Player.FaceRenderer.DestroyCalled -= TryShutdown;
                }
            }
        }
        public void ProcessAudioSamples(float[] data,int channels,int Length)
        {
            if (uLipSyncEnabledState == false)
            {
                return;
            }
            if (WasSuccessful == false)
            {
                return;
            }
            uLipSync.OnDataReceived(data, channels, Length);
        }
        public void OnPausedEvent(bool IsPaused)
        {
            if (IsPaused)
            {
                if (uLipSyncBlendShape != null)
                {
                    uLipSyncBlendShape.maxVolume = 0;
                    uLipSyncBlendShape.minVolume = 0;
                }
            }
            else
            {
                if (uLipSyncBlendShape != null)
                {
                    uLipSyncBlendShape.maxVolume = -1.5f;
                    uLipSyncBlendShape.minVolume = -2.5f;
                }
            }
        }
    }
}
