#include "UnityHeaders/IUnityInterface.h"
#include "UnityHeaders/IUnityXRTrace.h"
#include "UnityHeaders/IUnityXRDisplay.h"
#include "UnityHeaders/IUnityXRInput.h"
#include "UnityHeaders/UnitySubsystemTypes.h"
#include <vector>

extern "C" void BasisWebXR_SetData(float* data);
extern "C" void BasisWebXR_Haptic(int hand, float amplitude, float duration);

enum
{
    kFlagPose = 0, kEyeCount = 1, kEyeWidth = 2, kEyeHeight = 3,
    kLeftProjection = 4, kRightProjection = 20,
    kHeadPosition = 36, kHeadRotation = 39,
    kLeftEyePosition = 43, kLeftEyeRotation = 46,
    kRightEyePosition = 50, kRightEyeRotation = 53,
    kControllerBase = 60, kControllerStride = 40,
    kDataLength = 160
};

enum
{
    kConnected = 0, kGripPosition = 1, kGripRotation = 4, kAimPosition = 8, kAimRotation = 11,
    kTrigger = 15, kSqueeze = 16, kStickX = 17, kStickY = 18, kPadX = 19, kPadY = 20,
    kPrimaryButton = 21, kSecondaryButton = 22, kStickClick = 23, kPadClick = 24,
    kPrimaryTouch = 25, kSecondaryTouch = 26, kStickTouch = 27, kTriggerTouch = 28, kMenuButton = 29,
    kTriggerPressed = 30, kSqueezePressed = 31, kPadTouch = 32
};

static float s_Data[kDataLength];

struct BasisWebXRContext
{
    IUnityInterfaces* interfaces;
    IUnityXRTrace* trace;
    IUnityXRDisplayInterface* display;
    IUnityXRInputInterface* input;
    UnitySubsystemHandle displayHandle;
    UnitySubsystemHandle inputHandle;
    std::vector<UnityXRRenderTextureId> textures;
    int textureWidth, textureHeight;
    bool connected[3];
};

static BasisWebXRContext s_Context;

static const UnityXRInternalInputDeviceId kHeadDevice = 1;
static const UnityXRInternalInputDeviceId kLeftDevice = 2;
static const UnityXRInternalInputDeviceId kRightDevice = 3;

static UnityXRVector3 ReadVector3(int index)
{
    UnityXRVector3 value;
    value.x = s_Data[index]; value.y = s_Data[index + 1]; value.z = s_Data[index + 2];
    return value;
}

static UnityXRVector4 ReadRotation(int index)
{
    UnityXRVector4 value;
    value.x = s_Data[index]; value.y = s_Data[index + 1]; value.z = s_Data[index + 2]; value.w = s_Data[index + 3];
    if (value.x == 0.0f && value.y == 0.0f && value.z == 0.0f && value.w == 0.0f) value.w = 1.0f;
    return value;
}

static UnityXRPose ReadPose(int position, int rotation)
{
    UnityXRPose pose;
    pose.position = ReadVector3(position);
    pose.rotation = ReadRotation(rotation);
    return pose;
}

static UnityXRProjection ReadProjection(int index)
{
    UnityXRProjection projection;
    projection.type = kUnityXRProjectionTypeMatrix;
    UnityXRVector4* columns = projection.data.matrix.columns;
    for (int column = 0; column < 4; column++)
    {
        columns[column].x = s_Data[index + column * 4];
        columns[column].y = s_Data[index + column * 4 + 1];
        columns[column].z = s_Data[index + column * 4 + 2];
        columns[column].w = s_Data[index + column * 4 + 3];
    }
    return projection;
}

static void DestroyTextures()
{
    for (size_t index = 0; index < s_Context.textures.size(); index++) s_Context.display->DestroyTexture(s_Context.displayHandle, s_Context.textures[index]);
    s_Context.textures.clear();
    s_Context.textureWidth = 0;
    s_Context.textureHeight = 0;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API GfxStart(UnitySubsystemHandle, void*, UnityXRRenderingCapabilities* capabilities)
{
    capabilities->noSinglePassRenderingSupport = true;
    capabilities->invalidateRenderStateAfterEachCallback = false;
    capabilities->skipPresentToMainScreen = false;
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API GfxSubmit(UnitySubsystemHandle, void*)
{
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API GfxPopulateNextFrame(UnitySubsystemHandle handle, void*, const UnityXRFrameSetupHints* hints, UnityXRNextFrameDesc* next)
{
    int eyeWidth = (int)s_Data[kEyeWidth], eyeHeight = (int)s_Data[kEyeHeight];
    int eyes = s_Data[kEyeCount] > 1.5f ? 2 : 1;
    if (eyeWidth <= 0 || eyeHeight <= 0)
    {
        next->renderPassesCount = 0;
        return kUnitySubsystemErrorCodeSuccess;
    }
    int width = (int)(eyeWidth * eyes * hints->appSetup.textureResolutionScale), height = (int)(eyeHeight * hints->appSetup.textureResolutionScale);
    if (width < 1) width = 1;
    if (height < 1) height = 1;
    if (s_Context.textures.empty() || width != s_Context.textureWidth || height != s_Context.textureHeight)
    {
        DestroyTextures();
        UnityXRRenderTextureDesc desc{};
        desc.color.nativePtr = (void*)kUnityXRRenderTextureIdDontCare;
        desc.depth.nativePtr = (void*)kUnityXRRenderTextureIdDontCare;
        desc.depthFormat = kUnityXRDepthTextureFormat24bitOrGreater;
        desc.width = width;
        desc.height = height;
        UnityXRRenderTextureId texture;
        if (s_Context.display->CreateTexture(handle, &desc, &texture) != kUnitySubsystemErrorCodeSuccess)
        {
            next->renderPassesCount = 0;
            return kUnitySubsystemErrorCodeFailure;
        }
        s_Context.textures.push_back(texture);
        s_Context.textureWidth = width;
        s_Context.textureHeight = height;
    }
    next->renderPassesCount = eyes;
    UnityXRPose left = ReadPose(kLeftEyePosition, kLeftEyeRotation), right = ReadPose(kRightEyePosition, kRightEyeRotation);
    float dx = right.position.x - left.position.x, dy = right.position.y - left.position.y, dz = right.position.z - left.position.z;
    float separation = dx * dx + dy * dy + dz * dz;
    for (int pass = 0; pass < eyes; pass++)
    {
        UnityXRNextFrameDesc::UnityXRRenderPass& renderPass = next->renderPasses[pass];
        renderPass.textureId = s_Context.textures[0];
        renderPass.renderParamsCount = 1;
        renderPass.cullingPassIndex = pass;
        UnityXRNextFrameDesc::UnityXRRenderPass::UnityXRRenderParams& params = renderPass.renderParams[0];
        params.deviceAnchorToEyePose = pass == 0 ? left : right;
        params.projection = ReadProjection(pass == 0 ? kLeftProjection : kRightProjection);
        params.viewportRect.x = eyes == 2 ? pass * 0.5f : 0.0f;
        params.viewportRect.y = 0.0f;
        params.viewportRect.width = eyes == 2 ? 0.5f : 1.0f;
        params.viewportRect.height = 1.0f;
        params.textureArraySlice = 0;
        UnityXRNextFrameDesc::UnityXRCullingPass& culling = next->cullingPasses[pass];
        culling.deviceAnchorToCullingPose = params.deviceAnchorToEyePose;
        culling.projection = params.projection;
        culling.separation = separation > 0.0f ? __builtin_sqrtf(separation) : 0.0f;
    }
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API GfxStop(UnitySubsystemHandle, void*)
{
    DestroyTextures();
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API GfxMirrorBlit(UnitySubsystemHandle, void*, const UnityXRMirrorViewBlitInfo)
{
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API DisplayUpdateState(UnitySubsystemHandle, void*, UnityXRDisplayState* state)
{
    state->displayIsTransparent = false;
    state->focusLost = s_Data[kFlagPose] < 0.5f;
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API DisplayInitialize(UnitySubsystemHandle handle, void* userData)
{
    s_Context.displayHandle = handle;
    UnityXRDisplayGraphicsThreadProvider gfx{};
    gfx.userData = userData;
    gfx.Start = &GfxStart;
    gfx.SubmitCurrentFrame = &GfxSubmit;
    gfx.PopulateNextFrameDesc = &GfxPopulateNextFrame;
    gfx.Stop = &GfxStop;
    gfx.BlitToMirrorViewRenderTarget = &GfxMirrorBlit;
    s_Context.display->RegisterProviderForGraphicsThread(handle, &gfx);
    UnityXRDisplayProvider provider{};
    provider.userData = userData;
    provider.UpdateDisplayState = &DisplayUpdateState;
    s_Context.display->RegisterProvider(handle, &provider);
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API DisplayStart(UnitySubsystemHandle, void*)
{
    return kUnitySubsystemErrorCodeSuccess;
}

static void UNITY_INTERFACE_API DisplayStop(UnitySubsystemHandle, void*)
{
}

static void UNITY_INTERFACE_API DisplayShutdown(UnitySubsystemHandle, void*)
{
}

static int ControllerBase(UnityXRInternalInputDeviceId device)
{
    return kControllerBase + (device == kLeftDevice ? 0 : kControllerStride);
}

static bool DeviceConnected(UnityXRInternalInputDeviceId device)
{
    if (device == kHeadDevice) return s_Data[kFlagPose] > 0.5f;
    return s_Data[ControllerBase(device) + kConnected] > 0.5f;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputTick(UnitySubsystemHandle handle, void*, UnityXRInputUpdateType)
{
    for (UnityXRInternalInputDeviceId device = kHeadDevice; device <= kRightDevice; device++)
    {
        bool connected = DeviceConnected(device);
        if (connected == s_Context.connected[device - 1]) continue;
        s_Context.connected[device - 1] = connected;
        if (connected) s_Context.input->InputSubsystem_DeviceConnected(handle, device);
        else s_Context.input->InputSubsystem_DeviceDisconnected(handle, device);
    }
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputFillDefinition(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId device, UnityXRInputDeviceDefinition* definition)
{
    IUnityXRInputInterface& input = *s_Context.input;
    input.DeviceDefinition_SetManufacturer(definition, "WebXR");
    if (device == kHeadDevice)
    {
        input.DeviceDefinition_SetName(definition, "WebXR Head");
        input.DeviceDefinition_SetCharacteristics(definition, (UnityXRInputDeviceCharacteristics)(kUnityXRInputDeviceCharacteristicsHeadMounted | kUnityXRInputDeviceCharacteristicsTrackedDevice));
        input.DeviceDefinition_AddFeatureWithUsage(definition, "isTracked", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageIsTracked);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "trackingState", kUnityXRInputFeatureTypeDiscreteStates, kUnityXRInputFeatureUsageTrackingState);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "devicePosition", kUnityXRInputFeatureTypeAxis3D, kUnityXRInputFeatureUsageDevicePosition);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "deviceRotation", kUnityXRInputFeatureTypeRotation, kUnityXRInputFeatureUsageDeviceRotation);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "centerEyePosition", kUnityXRInputFeatureTypeAxis3D, kUnityXRInputFeatureUsageCenterEyePosition);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "centerEyeRotation", kUnityXRInputFeatureTypeRotation, kUnityXRInputFeatureUsageCenterEyeRotation);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "leftEyePosition", kUnityXRInputFeatureTypeAxis3D, kUnityXRInputFeatureUsageLeftEyePosition);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "leftEyeRotation", kUnityXRInputFeatureTypeRotation, kUnityXRInputFeatureUsageLeftEyeRotation);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "rightEyePosition", kUnityXRInputFeatureTypeAxis3D, kUnityXRInputFeatureUsageRightEyePosition);
        input.DeviceDefinition_AddFeatureWithUsage(definition, "rightEyeRotation", kUnityXRInputFeatureTypeRotation, kUnityXRInputFeatureUsageRightEyeRotation);
        return kUnitySubsystemErrorCodeSuccess;
    }
    bool left = device == kLeftDevice;
    input.DeviceDefinition_SetName(definition, left ? "WebXR Left Controller" : "WebXR Right Controller");
    input.DeviceDefinition_SetCharacteristics(definition, (UnityXRInputDeviceCharacteristics)(kUnityXRInputDeviceCharacteristicsHeldInHand | kUnityXRInputDeviceCharacteristicsTrackedDevice | kUnityXRInputDeviceCharacteristicsController | (left ? kUnityXRInputDeviceCharacteristicsLeft : kUnityXRInputDeviceCharacteristicsRight)));
    input.DeviceDefinition_AddFeatureWithUsage(definition, "isTracked", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageIsTracked);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "trackingState", kUnityXRInputFeatureTypeDiscreteStates, kUnityXRInputFeatureUsageTrackingState);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "devicePosition", kUnityXRInputFeatureTypeAxis3D, kUnityXRInputFeatureUsageDevicePosition);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "deviceRotation", kUnityXRInputFeatureTypeRotation, kUnityXRInputFeatureUsageDeviceRotation);
    input.DeviceDefinition_AddFeature(definition, "pointerPosition", kUnityXRInputFeatureTypeAxis3D);
    input.DeviceDefinition_AddFeature(definition, "pointerRotation", kUnityXRInputFeatureTypeRotation);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "trigger", kUnityXRInputFeatureTypeAxis1D, kUnityXRInputFeatureUsageTrigger);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "grip", kUnityXRInputFeatureTypeAxis1D, kUnityXRInputFeatureUsageGrip);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "primary2DAxis", kUnityXRInputFeatureTypeAxis2D, kUnityXRInputFeatureUsagePrimary2DAxis);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "secondary2DAxis", kUnityXRInputFeatureTypeAxis2D, kUnityXRInputFeatureUsageSecondary2DAxis);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "primaryButton", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsagePrimaryButton);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "secondaryButton", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageSecondaryButton);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "primary2DAxisClick", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsagePrimary2DAxisClick);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "secondary2DAxisClick", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageSecondary2DAxisClick);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "primaryTouched", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsagePrimaryTouch);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "secondaryTouched", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageSecondaryTouch);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "thumbstickTouched", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsagePrimary2DAxisTouch);
    input.DeviceDefinition_AddFeature(definition, "triggerTouched", kUnityXRInputFeatureTypeBinary);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "triggerPressed", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageTriggerButton);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "gripPressed", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageGripButton);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "menuButton", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageMenuButton);
    input.DeviceDefinition_AddFeatureWithUsage(definition, "touchpadTouched", kUnityXRInputFeatureTypeBinary, kUnityXRInputFeatureUsageSecondary2DAxisTouch);
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputUpdateState(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId device, UnityXRInputUpdateType, UnityXRInputDeviceState* state)
{
    IUnityXRInputInterface& input = *s_Context.input;
    bool tracked = DeviceConnected(device);
    input.DeviceState_SetBinaryValue(state, 0, tracked);
    input.DeviceState_SetDiscreteStateValue(state, 1, tracked ? 3u : 0u);
    if (device == kHeadDevice)
    {
        UnityXRVector3 head = ReadVector3(kHeadPosition);
        UnityXRVector4 rotation = ReadRotation(kHeadRotation);
        input.DeviceState_SetAxis3DValue(state, 2, head);
        input.DeviceState_SetRotationValue(state, 3, rotation);
        input.DeviceState_SetAxis3DValue(state, 4, head);
        input.DeviceState_SetRotationValue(state, 5, rotation);
        input.DeviceState_SetAxis3DValue(state, 6, ReadVector3(kLeftEyePosition));
        input.DeviceState_SetRotationValue(state, 7, ReadRotation(kLeftEyeRotation));
        input.DeviceState_SetAxis3DValue(state, 8, ReadVector3(kRightEyePosition));
        input.DeviceState_SetRotationValue(state, 9, ReadRotation(kRightEyeRotation));
        return kUnitySubsystemErrorCodeSuccess;
    }
    int b = ControllerBase(device);
    UnityXRVector2 stick, pad;
    stick.x = s_Data[b + kStickX]; stick.y = s_Data[b + kStickY];
    pad.x = s_Data[b + kPadX]; pad.y = s_Data[b + kPadY];
    input.DeviceState_SetAxis3DValue(state, 2, ReadVector3(b + kGripPosition));
    input.DeviceState_SetRotationValue(state, 3, ReadRotation(b + kGripRotation));
    input.DeviceState_SetAxis3DValue(state, 4, ReadVector3(b + kAimPosition));
    input.DeviceState_SetRotationValue(state, 5, ReadRotation(b + kAimRotation));
    input.DeviceState_SetAxis1DValue(state, 6, s_Data[b + kTrigger]);
    input.DeviceState_SetAxis1DValue(state, 7, s_Data[b + kSqueeze]);
    input.DeviceState_SetAxis2DValue(state, 8, stick);
    input.DeviceState_SetAxis2DValue(state, 9, pad);
    input.DeviceState_SetBinaryValue(state, 10, s_Data[b + kPrimaryButton] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 11, s_Data[b + kSecondaryButton] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 12, s_Data[b + kStickClick] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 13, s_Data[b + kPadClick] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 14, s_Data[b + kPrimaryTouch] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 15, s_Data[b + kSecondaryTouch] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 16, s_Data[b + kStickTouch] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 17, s_Data[b + kTriggerTouch] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 18, s_Data[b + kTriggerPressed] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 19, s_Data[b + kSqueezePressed] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 20, s_Data[b + kMenuButton] > 0.5f);
    input.DeviceState_SetBinaryValue(state, 21, s_Data[b + kPadTouch] > 0.5f);
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputHandleEvent(UnitySubsystemHandle, void*, unsigned int, UnityXRInternalInputDeviceId, void*, unsigned int)
{
    return kUnitySubsystemErrorCodeFailure;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputRecenter(UnitySubsystemHandle, void*)
{
    return kUnitySubsystemErrorCodeFailure;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputHapticImpulse(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId device, int, float amplitude, float duration)
{
    if (device != kLeftDevice && device != kRightDevice) return kUnitySubsystemErrorCodeFailure;
    BasisWebXR_Haptic(device == kLeftDevice ? 0 : 1, amplitude, duration);
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputHapticBuffer(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId, int, unsigned int, const unsigned char* const)
{
    return kUnitySubsystemErrorCodeFailure;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputHapticCapabilities(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId device, UnityXRHapticCapabilities* capabilities)
{
    bool hand = device == kLeftDevice || device == kRightDevice;
    capabilities->numChannels = hand ? 1 : 0;
    capabilities->supportsImpulse = hand;
    capabilities->supportsBuffer = false;
    capabilities->bufferFrequencyHz = 0;
    capabilities->bufferMaxSize = 0;
    capabilities->bufferOptimalSize = 0;
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputHapticStop(UnitySubsystemHandle, void*, UnityXRInternalInputDeviceId)
{
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputTrackingOrigin(UnitySubsystemHandle, void*, UnityXRInputTrackingOriginModeFlags* mode)
{
    *mode = kUnityXRInputTrackingOriginModeFloor;
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputSetTrackingOrigin(UnitySubsystemHandle, void*, UnityXRInputTrackingOriginModeFlags mode)
{
    return mode == kUnityXRInputTrackingOriginModeFloor ? kUnitySubsystemErrorCodeSuccess : kUnitySubsystemErrorCodeFailure;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputStateAtTime(UnitySubsystemHandle, void*, UnityXRTimeStamp, UnityXRInternalInputDeviceId, UnityXRInputDeviceState*)
{
    return kUnitySubsystemErrorCodeFailure;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputInitialize(UnitySubsystemHandle handle, void* userData)
{
    s_Context.inputHandle = handle;
    UnityXRInputProvider provider{};
    provider.userData = userData;
    provider.Tick = &InputTick;
    provider.FillDeviceDefinition = &InputFillDefinition;
    provider.UpdateDeviceState = &InputUpdateState;
    provider.HandleEvent = &InputHandleEvent;
    provider.HandleRecenter = &InputRecenter;
    provider.HandleHapticImpulse = &InputHapticImpulse;
    provider.HandleHapticBuffer = &InputHapticBuffer;
    provider.QueryHapticCapabilities = &InputHapticCapabilities;
    provider.HandleHapticStop = &InputHapticStop;
    provider.QueryTrackingOriginMode = &InputTrackingOrigin;
    provider.QuerySupportedTrackingOriginModes = &InputTrackingOrigin;
    provider.HandleSetTrackingOriginMode = &InputSetTrackingOrigin;
    provider.TryGetDeviceStateAtTime = &InputStateAtTime;
    s_Context.input->RegisterInputProvider(handle, &provider);
    return kUnitySubsystemErrorCodeSuccess;
}

static UnitySubsystemErrorCode UNITY_INTERFACE_API InputStart(UnitySubsystemHandle, void*)
{
    for (int index = 0; index < 3; index++) s_Context.connected[index] = false;
    return kUnitySubsystemErrorCodeSuccess;
}

static void UNITY_INTERFACE_API InputStop(UnitySubsystemHandle handle, void*)
{
    for (UnityXRInternalInputDeviceId device = kHeadDevice; device <= kRightDevice; device++)
    {
        if (!s_Context.connected[device - 1]) continue;
        s_Context.connected[device - 1] = false;
        s_Context.input->InputSubsystem_DeviceDisconnected(handle, device);
    }
}

static void UNITY_INTERFACE_API InputShutdown(UnitySubsystemHandle, void*)
{
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API BasisWebXRPluginLoad(IUnityInterfaces* interfaces)
{
    s_Context.interfaces = interfaces;
    s_Context.trace = interfaces->Get<IUnityXRTrace>();
    s_Context.display = interfaces->Get<IUnityXRDisplayInterface>();
    s_Context.input = interfaces->Get<IUnityXRInputInterface>();
    if (s_Context.display != NULL)
    {
        UnityLifecycleProvider display{};
        display.userData = &s_Context;
        display.Initialize = &DisplayInitialize;
        display.Start = &DisplayStart;
        display.Stop = &DisplayStop;
        display.Shutdown = &DisplayShutdown;
        s_Context.display->RegisterLifecycleProvider("Basis WebXR", "Basis WebXR Display", &display);
    }
    if (s_Context.input != NULL)
    {
        UnityLifecycleProvider input{};
        input.userData = &s_Context;
        input.Initialize = &InputInitialize;
        input.Start = &InputStart;
        input.Stop = &InputStop;
        input.Shutdown = &InputShutdown;
        s_Context.input->RegisterLifecycleProvider("Basis WebXR", "Basis WebXR Input", &input);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API BasisWebXRPluginUnload()
{
}

typedef void (UNITY_INTERFACE_API * BasisWebXRLoadFunction)(IUnityInterfaces*);
typedef void (UNITY_INTERFACE_API * BasisWebXRUnloadFunction)();
extern "C" void UnityRegisterRenderingPlugin(BasisWebXRLoadFunction load, BasisWebXRUnloadFunction unload);

static bool s_Registered = false;

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API BasisWebXR_Register()
{
    BasisWebXR_SetData(s_Data);
    if (s_Registered) return;
    s_Registered = true;
    UnityRegisterRenderingPlugin(&BasisWebXRPluginLoad, &BasisWebXRPluginUnload);
}
