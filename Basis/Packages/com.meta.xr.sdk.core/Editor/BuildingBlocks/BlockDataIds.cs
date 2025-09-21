/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Meta.XR.BuildingBlocks.Editor
{
    internal static class BlockDataIds
    {
        public const string CameraRig = "e47682b9-c270-40b1-b16d-90b627a5ce1b";
        public const string ControllerTracking = "5817f7c0-f2a5-45f9-a5ca-64264e0166e8";
        public const string EyeGaze = "f92445a7-db19-452c-8d3d-96820f6c8972";
        public const string HandTracking = "8b26b298-7bf4-490e-b245-a039c0184303";
        public const string Passthrough = "f0540b20-dfd6-420e-b20d-c270f88dc77e";
        public const string RoomModel = "be2b0240-1191-4e84-90bb-40fb6d75848b";
        public const string PassthroughOverlay = "40e08c51-14aa-4822-927d-5fe79943b5b4";
        public const string SurfaceProjectedPassthrough = "19c06269-24b5-4657-a433-21a6f80dbabf";
        public const string VirtualKeyboard = "bac08eda-400f-4f61-8842-77e7699e068d";
        public const string PassthroughWindow = "4f06f550-8209-46c8-a4c9-879a368560f6";
        public const string SampleSpatialAnchorController = "1f4566e8-f4ba-46d2-9e47-3748c9869766";
        public const string SpatialAnchorCore = "a383f5ea-3856-4c23-a11c-7fdbb9408035";
        public const string SharedSpatialAnchorCore = "975428dc-db04-4e43-be4a-232f192cd3eb";
        public const string ControllerButtonsMapper = "80aa2eb2-af2f-40a9-ad9e-18044ce65bee";
        public const string RoomMesh = "6b8a377d-05de-449b-a9c4-8ed22606f71c";
        public const string Occlusion = "c1dbe32c-fed2-4835-8e6a-3fbb4f4f4315";
        public const string MRUK = "d4413f4f-542f-451f-92ee-4801f27d676a";

        // Utilities
        public const string Cube = "7358e191-33b7-43ca-b066-b2f00531ac66";
        public const string AlertViewHUD = "dfd47015-f5ae-4435-8b15-8acde41b3871";

        // Interaction
        public const string GrabbableItem = "5c5184f2-c2f5-4063-b14b-3b1264fb3c1a";
        public const string PointableItem = "76a013d1-7c16-4a60-9c1b-79b3691c0438";
        public const string PokeableItem = "5838e892-31a4-4f65-a415-dc108aefa14c";
        public const string RealHands = "f547fe18-d477-46ec-bdf5-7208df19cb98";
        public const string SyntheticHands = "6b67162c-2460-4766-a931-980388647573";
        public const string ThrowableItem = "803f3803-bb5e-464a-ac45-dad44c840fb6";

        // Audio SDK
        public const string SpatialAudio = "6737262b-4fab-4779-a38b-fe74012fa505";

        public const string SpeechToTextBlock = "af49bc82-4174-46e0-b1e4-40f1c05b4908";
        public const string TextToSpeech = "ab791591-5ee2-431a-93e7-1749d1b5ad8f";
        public const string Haptics = "ad78e7c1-6a4d-422e-99f0-5b1a1ae07f6f";


        // Multiplayer Blocks
        public const string IAutoMatchmaking = "d45b4309-f823-42e4-b871-0f58e8e647d8";
        public const string ICustomMatchmaking = "c5bea02f-8079-4142-87f9-56da5993f861";
        public const string AutoMatchmakingNGOInstallationRoutine = "0d740637-ae4c-40f5-98e2-1a9bac152d35";
        public const string AutoMatchmakingFusionInstallationRoutine = "6436b892-b7d4-4073-8113-00ab41af3be1";

        public const string INetworkManager = "1d8db162-54f6-43df-b4ef-b499df1f6769";
        public const string NetworkManagerNGOInstallationRoutine = "45364db4-f880-45c3-9d76-d366c46500d3";
        public const string NetworkManagerFusionInstallationRoutine = "676820bd-24bb-4da2-9785-74ad32626885";

        public const string IPlayerNameTag = "97a7e1ae-ac65-4ee6-9167-10b3b94782f6";
        public const string PlayerNameTagNGOInstallationRoutine = "5fa4d807-dad5-45f9-8d2a-fd910366fd35";
        public const string PlayerNameTagFusionInstallationRoutine = "d2603dd8-0302-481a-a5e8-29f8e329a3e0";

        public const string INetworkedAvatar = "b627f6cd-e738-4827-bfb1-7332719112b3";
        public const string NetworkedAvatarNGOInstallationRoutine = "e7255571-f7d7-46a3-a005-558934863c26";
        public const string NetworkedAvatarFusionInstallationRoutine = "4e28f6ff-1e9b-4a90-b9e0-ebc3fb118408";

        public const string ControllerTeleporter = "3503368f-ae74-4ba8-a0c1-cf904f4bf5b6";
        public const string PlatformInit = "40089589-8290-4ae3-a056-d9ca1ccaa35a";
        public const string IFriendsMatchmaking = "a29fd151-3fe0-46e4-8ae6-c899e8b9be35";
        public const string LocalMatchmaking = "9141594f-eab4-43f9-b05e-17f2f40db586";

        public const string PlayerVoiceChat = "5523ee0f-69d0-4e9f-b4b3-1ca61874f631";
        public const string PlayerVoiceChatFusionInstallationRoutine = "ca29a203-ad6d-4c65-ae37-ffab1dc8a165";

        public const string IColocation = "f308c8f0-7a4b-4cd5-88be-c15b6399f823";
        public const string ColocationNGOInstallationRoutine = "627c0498-c394-4708-824d-be6f2ba735a1";
        public const string ColocationFusionInstallationRoutine = "7ea39360-e3ed-49ba-8b87-3a3427ae198c";

        public const string INetworkedGrabbableObject = "e9b4b64f-1c7e-4dff-8f3c-ce409bdc3951";
        public const string NetworkedGrabbableObjectNGOInstallationRoutine = "d5c83ff6-47e4-4c98-acdd-041d4686912f";
        public const string NetworkedGrabbableObjectFusionInstallationRoutine = "54548176-30f4-4a4a-8057-340a383c6102";
    }
}
