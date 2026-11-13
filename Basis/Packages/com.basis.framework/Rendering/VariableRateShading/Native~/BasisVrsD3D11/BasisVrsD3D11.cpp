// Basis D3D11 variable rate shading through NVAPI (Turing and newer NVIDIA GPUs).
// nvapi64.dll is loaded at runtime, so nothing links against the NVAPI SDK and the plugin
// reports unsupported on other vendors. Struct layouts and interface ids follow the MIT
// licensed NVIDIA/nvapi headers (nvapi.h, nvapi_interface.h).
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>

typedef int NvAPI_Status;
static const int NV_MAX_PIXEL_SHADING_RATES = 16;
static const int NV_MAX_NUM_VIEWPORTS = 16;
#define NV_VERSION(type, ver) ((uint32_t)(sizeof(type) | ((ver) << 16)))

enum NV_PIXEL_SHADING_RATE
{
    NV_PIXEL_X0_CULL_RASTER_PIXELS, NV_PIXEL_X16_PER_RASTER_PIXEL, NV_PIXEL_X8_PER_RASTER_PIXEL, NV_PIXEL_X4_PER_RASTER_PIXEL,
    NV_PIXEL_X2_PER_RASTER_PIXEL, NV_PIXEL_X1_PER_RASTER_PIXEL, NV_PIXEL_X1_PER_2X1_RASTER_PIXELS, NV_PIXEL_X1_PER_1X2_RASTER_PIXELS,
    NV_PIXEL_X1_PER_2X2_RASTER_PIXELS, NV_PIXEL_X1_PER_4X2_RASTER_PIXELS, NV_PIXEL_X1_PER_2X4_RASTER_PIXELS, NV_PIXEL_X1_PER_4X4_RASTER_PIXELS
};

struct NV_D3D1x_GRAPHICS_CAPS_V2
{
    uint32_t bExclusiveScissorRectsSupported : 1;
    uint32_t bVariablePixelRateShadingSupported : 1;
    uint32_t bFastUAVClearSupported : 1;
    uint32_t reservedBits : 29;
    uint16_t majorSMVersion, minorSMVersion, majorCudaSMVersion, minorCudaSMVersion;
    uint32_t reserved[13];
};

struct NV_D3D11_VIEWPORT_SHADING_RATE_DESC_V1
{
    bool enableVariablePixelShadingRate;
    NV_PIXEL_SHADING_RATE shadingRateTable[NV_MAX_PIXEL_SHADING_RATES];
};

struct NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1
{
    uint32_t version;
    uint32_t numViewports;
    NV_D3D11_VIEWPORT_SHADING_RATE_DESC_V1* pViewports;
};

enum NV_SRRV_DIMENSION { NV_SRRV_DIMENSION_TEXTURE2D = 4, NV_SRRV_DIMENSION_TEXTURE2DARRAY = 5 };

struct NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_V1
{
    uint32_t version;
    DXGI_FORMAT Format;
    NV_SRRV_DIMENSION ViewDimension;
    union { struct { UINT MipSlice; } Texture2D; struct { UINT MipSlice, FirstArraySlice, ArraySize; } Texture2DArray; };
};

typedef void* (__cdecl* PFN_QueryInterface)(uint32_t id);
typedef NvAPI_Status (__cdecl* PFN_Initialize)();
typedef NvAPI_Status (__cdecl* PFN_GetGraphicsCapabilities)(IUnknown*, uint32_t, NV_D3D1x_GRAPHICS_CAPS_V2*);
typedef NvAPI_Status (__cdecl* PFN_CreateShadingRateResourceView)(ID3D11Device*, ID3D11Resource*, NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_V1*, IUnknown**);
typedef NvAPI_Status (__cdecl* PFN_RSSetShadingRateResourceView)(IUnknown*, IUnknown*);
typedef NvAPI_Status (__cdecl* PFN_RSSetViewportsPixelShadingRates)(IUnknown*, NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1*);

// Unity native plugin interfaces (IUnityInterface.h / IUnityGraphics.h / IUnityGraphicsD3D11.h).
struct UnityInterfaceGUID { unsigned long long high, low; };
struct IUnityInterfaces
{
    void* (__stdcall* GetInterface)(UnityInterfaceGUID guid);
    void (__stdcall* RegisterInterface)(UnityInterfaceGUID guid, void* ptr);
    void* (__stdcall* GetInterfaceSplit)(unsigned long long high, unsigned long long low);
    void (__stdcall* RegisterInterfaceSplit)(unsigned long long high, unsigned long long low, void* ptr);
};
struct IUnityGraphics
{
    int (__stdcall* GetRenderer)();
    void (__stdcall* RegisterDeviceEventCallback)(void (__stdcall* cb)(int));
    void (__stdcall* UnregisterDeviceEventCallback)(void (__stdcall* cb)(int));
    int (__stdcall* ReserveEventIDRange)(int count);
};
struct IUnityGraphicsD3D11 { ID3D11Device* (__stdcall* GetDevice)(); };
static const unsigned long long GuidGraphicsHigh = 0x7CBA0A9CA4DDB544ULL, GuidGraphicsLow = 0x8C5AD4926EB17B11ULL;
static const unsigned long long GuidD3D11High = 0xAAB37EF87A87D748ULL, GuidD3D11Low = 0xBF76967F07EFB177ULL;
static const int RendererD3D11 = 2;
static const int DeviceEventInitialize = 0, DeviceEventShutdown = 1;

enum Status { StatusNotLoaded = 0, StatusSupported = 1, StatusNotD3D11 = 2, StatusNoNvapi = 3, StatusNvapiInitFailed = 4, StatusNoVrs = 5, StatusNotNvidia = 6 };
enum Event { EventEnable = 1, EventDisable = 2, EventRelease = 3 };

static IUnityInterfaces* s_Interfaces;
static IUnityGraphics* s_Graphics;
static ID3D11Device* s_Device;
static HMODULE s_Nvapi;
static PFN_CreateShadingRateResourceView s_CreateView;
static PFN_RSSetShadingRateResourceView s_SetView;
static PFN_RSSetViewportsPixelShadingRates s_SetRates;
static IUnknown* s_View;
static ID3D11Resource* s_ViewResource;
static NV_D3D11_VIEWPORT_SHADING_RATE_DESC_V1 s_Viewports[NV_MAX_NUM_VIEWPORTS];
static volatile LONG s_Status = StatusNotLoaded;
static volatile LONG s_LastError;
static volatile LONG s_EnableCount;
static volatile LONG s_UncoveredCount;
static bool s_Enabled;

static void ReleaseView()
{
    if (s_View) { s_View->Release(); s_View = nullptr; }
    s_ViewResource = nullptr;
}

// Table index = the D3D12/Vulkan rate code the build compute writes: (log2(w) << 2) | log2(h).
static void BuildTable()
{
    for (int v = 0; v < NV_MAX_NUM_VIEWPORTS; v++)
    {
        s_Viewports[v].enableVariablePixelShadingRate = true;
        for (int i = 0; i < NV_MAX_PIXEL_SHADING_RATES; i++) { s_Viewports[v].shadingRateTable[i] = NV_PIXEL_X1_PER_RASTER_PIXEL; }
        s_Viewports[v].shadingRateTable[1] = NV_PIXEL_X1_PER_1X2_RASTER_PIXELS;
        s_Viewports[v].shadingRateTable[4] = NV_PIXEL_X1_PER_2X1_RASTER_PIXELS;
        s_Viewports[v].shadingRateTable[5] = NV_PIXEL_X1_PER_2X2_RASTER_PIXELS;
        s_Viewports[v].shadingRateTable[6] = NV_PIXEL_X1_PER_2X4_RASTER_PIXELS;
        s_Viewports[v].shadingRateTable[9] = NV_PIXEL_X1_PER_4X2_RASTER_PIXELS;
        s_Viewports[v].shadingRateTable[10] = NV_PIXEL_X1_PER_4X4_RASTER_PIXELS;
    }
}

static UINT AdapterVendor(ID3D11Device* device)
{
    UINT vendor = 0;
    IDXGIDevice* dxgi = nullptr;
    if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)))
    {
        IDXGIAdapter* adapter = nullptr;
        DXGI_ADAPTER_DESC desc = {};
        if (SUCCEEDED(dxgi->GetAdapter(&adapter))) { if (SUCCEEDED(adapter->GetDesc(&desc))) { vendor = desc.VendorId; } adapter->Release(); }
        dxgi->Release();
    }
    return vendor;
}

static void Initialize()
{
    ReleaseView();
    s_Device = nullptr;
    if (s_Graphics == nullptr || s_Graphics->GetRenderer() != RendererD3D11) { s_Status = StatusNotD3D11; return; }
    IUnityGraphicsD3D11* d3d = (IUnityGraphicsD3D11*)s_Interfaces->GetInterfaceSplit(GuidD3D11High, GuidD3D11Low);
    s_Device = d3d ? d3d->GetDevice() : nullptr;
    if (s_Device == nullptr) { s_Status = StatusNotD3D11; return; }
    // NVAPI is only ever called for a device on an NVIDIA adapter. A machine can carry the NVIDIA
    // driver (and nvapi64.dll) while Unity renders on AMD or Intel, so the DLL alone proves nothing.
    if (AdapterVendor(s_Device) != 0x10DE) { s_Status = StatusNotNvidia; return; }
    if (s_Nvapi == nullptr) { s_Nvapi = LoadLibraryW(L"nvapi64.dll"); }
    PFN_QueryInterface query = s_Nvapi ? (PFN_QueryInterface)GetProcAddress(s_Nvapi, "nvapi_QueryInterface") : nullptr;
    if (query == nullptr) { s_Status = StatusNoNvapi; return; }
    PFN_Initialize init = (PFN_Initialize)query(0x0150e828);
    PFN_GetGraphicsCapabilities caps = (PFN_GetGraphicsCapabilities)query(0x52b1499a);
    s_SetRates = (PFN_RSSetViewportsPixelShadingRates)query(0x34f7938f);
    s_CreateView = (PFN_CreateShadingRateResourceView)query(0x99ca2dff);
    s_SetView = (PFN_RSSetShadingRateResourceView)query(0x1b0c2f83);
    if (!init || !caps || !s_SetRates || !s_CreateView || !s_SetView) { s_Status = StatusNoNvapi; return; }
    NvAPI_Status st = init();
    if (st != 0) { s_LastError = st; s_Status = StatusNvapiInitFailed; return; }
    NV_D3D1x_GRAPHICS_CAPS_V2 c = {};
    st = caps(s_Device, NV_VERSION(NV_D3D1x_GRAPHICS_CAPS_V2, 2), &c);
    if (st != 0 || !c.bVariablePixelRateShadingSupported) { s_LastError = st; s_Status = StatusNoVrs; return; }
    BuildTable();
    s_Status = StatusSupported;
}

// NVAPI does not bounds check the shading rate image: a target larger than the image covers (an eye
// texture resized while the old image is still bound) makes the GPU read past its end, which faults the
// device and later takes down the driver. Only enable when every pixel of the bound target has a tile.
static bool CoversBoundTarget(ID3D11DeviceContext* ctx, ID3D11Resource* sri)
{
    ID3D11Texture2D* image = nullptr;
    if (FAILED(sri->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&image)) || image == nullptr) { return false; }
    D3D11_TEXTURE2D_DESC imageDesc = {};
    image->GetDesc(&imageDesc);
    image->Release();
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11DepthStencilView* dsv = nullptr;
    ctx->OMGetRenderTargets(1, &rtv, &dsv);
    ID3D11View* view = rtv ? (ID3D11View*)rtv : (ID3D11View*)dsv;
    bool covered = true;
    if (view)
    {
        ID3D11Resource* target = nullptr;
        view->GetResource(&target);
        ID3D11Texture2D* texture = nullptr;
        if (target && SUCCEEDED(target->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&texture)) && texture)
        {
            D3D11_TEXTURE2D_DESC targetDesc = {};
            texture->GetDesc(&targetDesc);
            covered = targetDesc.Width <= imageDesc.Width * 16 && targetDesc.Height <= imageDesc.Height * 16;
            texture->Release();
        }
        else { covered = false; }
        if (target) { target->Release(); }
    }
    if (rtv) { rtv->Release(); }
    if (dsv) { dsv->Release(); }
    return covered;
}

static void Disable(ID3D11DeviceContext* ctx)
{
    NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1 off = { NV_VERSION(NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1, 1), 0, s_Viewports };
    s_SetRates(ctx, &off);
    s_SetView(ctx, nullptr);
    s_Enabled = false;
}

static void __stdcall OnRenderEvent(int eventId, void* data)
{
    if (s_Status != StatusSupported || s_Device == nullptr) { return; }
    ID3D11DeviceContext* ctx = nullptr;
    s_Device->GetImmediateContext(&ctx);
    if (ctx == nullptr) { return; }
    if (eventId == EventEnable && data != nullptr)
    {
        ID3D11Resource* resource = (ID3D11Resource*)data;
        if (resource != s_ViewResource)
        {
            ReleaseView();
            NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_V1 desc = {};
            desc.version = NV_VERSION(NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_V1, 1);
            desc.Format = DXGI_FORMAT_R8_UINT;
            desc.ViewDimension = NV_SRRV_DIMENSION_TEXTURE2D;
            NvAPI_Status st = s_CreateView(s_Device, resource, &desc, &s_View);
            if (st != 0) { s_LastError = st; s_View = nullptr; }
            else { s_ViewResource = resource; }
        }
        if (s_View && !CoversBoundTarget(ctx, resource))
        {
            InterlockedIncrement(&s_UncoveredCount);
            if (s_Enabled) { Disable(ctx); }
        }
        else if (s_View)
        {
            NvAPI_Status st = s_SetView(ctx, s_View);
            NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1 on = { NV_VERSION(NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_V1, 1), NV_MAX_NUM_VIEWPORTS, s_Viewports };
            if (st == 0) { st = s_SetRates(ctx, &on); }
            if (st != 0) { s_LastError = st; Disable(ctx); }
            else { s_Enabled = true; InterlockedIncrement(&s_EnableCount); }
        }
    }
    else if (eventId == EventDisable) { if (s_Enabled) { Disable(ctx); } }
    else if (eventId == EventRelease) { if (s_Enabled) { Disable(ctx); } ReleaseView(); }
    ctx->Release();
}

static void __stdcall OnDeviceEvent(int type)
{
    if (type == DeviceEventInitialize) { Initialize(); }
    else if (type == DeviceEventShutdown) { ReleaseView(); s_Device = nullptr; s_Status = StatusNotLoaded; }
}

extern "C"
{
    __declspec(dllexport) void __stdcall UnityPluginLoad(IUnityInterfaces* interfaces)
    {
        s_Interfaces = interfaces;
        s_Graphics = (IUnityGraphics*)interfaces->GetInterfaceSplit(GuidGraphicsHigh, GuidGraphicsLow);
        if (s_Graphics) { s_Graphics->RegisterDeviceEventCallback(OnDeviceEvent); }
        OnDeviceEvent(DeviceEventInitialize);
    }

    __declspec(dllexport) void __stdcall UnityPluginUnload()
    {
        if (s_Graphics) { s_Graphics->UnregisterDeviceEventCallback(OnDeviceEvent); }
        ReleaseView();
    }

    __declspec(dllexport) int __stdcall BasisVrsD3D11_Status() { return s_Status; }
    __declspec(dllexport) int __stdcall BasisVrsD3D11_LastError() { return s_LastError; }
    __declspec(dllexport) int __stdcall BasisVrsD3D11_EnableCount() { return s_EnableCount; }
    __declspec(dllexport) int __stdcall BasisVrsD3D11_UncoveredCount() { return s_UncoveredCount; }
    __declspec(dllexport) int __stdcall BasisVrsD3D11_TileSize() { return 16; }
    __declspec(dllexport) void* __stdcall BasisVrsD3D11_GetRenderEventFunc() { return (void*)OnRenderEvent; }
}
