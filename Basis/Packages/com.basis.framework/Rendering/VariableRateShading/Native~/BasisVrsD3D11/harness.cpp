// Drives BasisVrsD3D11.dll the way Unity does (fake IUnityInterfaces + render events) on a chosen
// D3D11 adapter, then draws a full-screen triangle whose colour depends on every pixel position.
// With VRS live and an all-4x4 rate image, every aligned 4x4 block comes out a single colour.
// Usage: set UAVCLEAR=1, then harness <plugin.dll> nvidia|warp. The driver ignores rate data uploaded at
// texture creation; Unity writes the image through a UAV, which UAVCLEAR matches.
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <d3dcompiler.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <stdlib.h>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

struct UnityInterfaceGUID { unsigned long long high, low; };
struct IUnityInterfaces
{
    void* (__stdcall* GetInterface)(UnityInterfaceGUID);
    void (__stdcall* RegisterInterface)(UnityInterfaceGUID, void*);
    void* (__stdcall* GetInterfaceSplit)(unsigned long long, unsigned long long);
    void (__stdcall* RegisterInterfaceSplit)(unsigned long long, unsigned long long, void*);
};
struct IUnityGraphics
{
    int (__stdcall* GetRenderer)();
    void (__stdcall* RegisterDeviceEventCallback)(void (__stdcall*)(int));
    void (__stdcall* UnregisterDeviceEventCallback)(void (__stdcall*)(int));
    int (__stdcall* ReserveEventIDRange)(int);
};
struct IUnityGraphicsD3D11 { ID3D11Device* (__stdcall* GetDevice)(); };

static ID3D11Device* g_Device;
static int __stdcall GetRenderer() { return 2; }
static void __stdcall RegisterCb(void (__stdcall*)(int)) {}
static int __stdcall Reserve(int) { return 0; }
static ID3D11Device* __stdcall GetDevice() { return g_Device; }
static IUnityGraphics g_Graphics = { GetRenderer, RegisterCb, RegisterCb, Reserve };
static IUnityGraphicsD3D11 g_D3D11 = { GetDevice };
static void* __stdcall GetSplit(unsigned long long high, unsigned long long low)
{
    if (high == 0x7CBA0A9CA4DDB544ULL && low == 0x8C5AD4926EB17B11ULL) return &g_Graphics;
    if (high == 0xAAB37EF87A87D748ULL && low == 0xBF76967F07EFB177ULL) return &g_D3D11;
    return nullptr;
}
static void* __stdcall GetIface(UnityInterfaceGUID g) { return GetSplit(g.high, g.low); }
static void __stdcall RegIface(UnityInterfaceGUID, void*) {}
static void __stdcall RegSplit(unsigned long long, unsigned long long, void*) {}
static IUnityInterfaces g_Interfaces = { GetIface, RegIface, GetSplit, RegSplit };

static const char* kShader =
    "struct V { float4 p : SV_Position; float2 uv : TEXCOORD0; };\n"
    "V VS(uint id : SV_VertexID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.p = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); o.uv = uv * float2(256, 128); return o; }\n"
    "float4 PS(V i) : SV_Target { uint2 q = (uint2)i.uv; return float4((q.x % 256) / 255.0, (q.y % 256) / 255.0, ((q.x * 7 + q.y * 13) % 256) / 255.0, 1); }\n";

int main(int argc, char** argv)
{
    if (argc < 3) { printf("usage: harness plugin.dll nvidia|warp|basic\n"); return 2; }
    const char* mode = argv[2];
    IDXGIFactory1* factory = nullptr;
    CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory);
    IDXGIAdapter1* chosen = nullptr;
    D3D_DRIVER_TYPE type = D3D_DRIVER_TYPE_UNKNOWN;
    if (strcmp(mode, "warp") == 0) { type = D3D_DRIVER_TYPE_WARP; }
    else
    {
        UINT want = strcmp(mode, "nvidia") == 0 ? 0x10DE : strcmp(mode, "amd") == 0 ? 0x1002 : 0x1414;
        IDXGIAdapter1* a = nullptr;
        for (UINT i = 0; factory->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; i++)
        {
            DXGI_ADAPTER_DESC1 d; a->GetDesc1(&d);
            if (!chosen && d.VendorId == want) { chosen = a; continue; }
            a->Release();
        }
        if (!chosen) { printf("no adapter for %s\n", mode); return 3; }
    }
    ID3D11DeviceContext* ctx = nullptr;
    HRESULT hr = D3D11CreateDevice(chosen, type, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &g_Device, nullptr, &ctx);
    if (FAILED(hr)) { printf("device failed %08lx\n", (unsigned long)hr); return 4; }
    {
        IDXGIDevice* dd; IDXGIAdapter* ad; DXGI_ADAPTER_DESC desc;
        g_Device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dd); dd->GetAdapter(&ad); ad->GetDesc(&desc);
        printf("adapter: %ls (vendor %04x)\n", desc.Description, desc.VendorId);
        ad->Release(); dd->Release();
    }

    HMODULE plugin = LoadLibraryA(argv[1]);
    if (!plugin) { printf("plugin load failed\n"); return 5; }
    auto load = (void (__stdcall*)(IUnityInterfaces*))GetProcAddress(plugin, "UnityPluginLoad");
    auto status = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_Status");
    auto enables = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_EnableCount");
    auto lastError = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_LastError");
    auto eventFunc = (void* (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_GetRenderEventFunc");
    load(&g_Interfaces);
    int st = status();
    printf("plugin status: %d\n", st);
    auto onEvent = (void (__stdcall*)(int, void*))eventFunc();

    const UINT W = 256, H = 128;
    D3D11_TEXTURE2D_DESC td = {}; td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.BindFlags = D3D11_BIND_RENDER_TARGET;
    ID3D11Texture2D* rt; g_Device->CreateTexture2D(&td, nullptr, &rt);
    ID3D11RenderTargetView* rtv; g_Device->CreateRenderTargetView(rt, nullptr, &rtv);
    td.BindFlags = 0; td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* staging; g_Device->CreateTexture2D(&td, nullptr, &staging);
    std::vector<unsigned char> rates((W / 16) * (H / 16), 10);
    D3D11_TEXTURE2D_DESC sd = {}; sd.Width = W / 16; sd.Height = H / 16; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = DXGI_FORMAT_R8_UINT; sd.SampleDesc.Count = 1; sd.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA init = { rates.data(), W / 16, 0 };
    ID3D11Texture2D* sri; hr = g_Device->CreateTexture2D(&sd, &init, &sri);
    if (FAILED(hr)) { printf("sri failed %08lx\n", (unsigned long)hr); return 6; }
    if (getenv("UAVCLEAR")) { ID3D11UnorderedAccessView* uav; g_Device->CreateUnorderedAccessView(sri, nullptr, &uav); UINT v[4] = { 10, 10, 10, 10 }; ctx->ClearUnorderedAccessViewUint(uav, v); uav->Release(); }

    ID3DBlob *vsb, *psb, *err;
    D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vsb, &err);
    D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &psb, &err);
    ID3D11VertexShader* vs; ID3D11PixelShader* ps;
    g_Device->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs);
    g_Device->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps);

    auto drawAndCount = [&](bool vrs) -> int
    {
        float clear[4] = { 0, 0, 0, 0 };
        ctx->ClearRenderTargetView(rtv, clear);
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        D3D11_VIEWPORT vp = { 0, 0, (float)W, (float)H, 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0); ctx->PSSetShader(ps, nullptr, 0);
        if (vrs) onEvent(1, sri);
        ctx->Draw(3, 0);
        if (vrs) onEvent(2, nullptr);
        ctx->CopyResource(staging, rt);
        D3D11_MAPPED_SUBRESOURCE m; ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m);
        for (UINT y = 0; y < 2; y++) { printf("%s row %u:", vrs ? "vrs" : "plain", y); for (UINT x = 0; x < 8; x++) printf(" %08x", *(unsigned int*)((unsigned char*)m.pData + y * m.RowPitch + x * 4)); printf("\n"); }
        int uniformBlocks = 0;
        for (UINT by = 0; by < H; by += 4)
            for (UINT bx = 0; bx < W; bx += 4)
            {
                unsigned int first = *(unsigned int*)((unsigned char*)m.pData + by * m.RowPitch + bx * 4);
                bool same = true;
                for (UINT y = 0; y < 4 && same; y++) for (UINT x = 0; x < 4; x++) if (*(unsigned int*)((unsigned char*)m.pData + (by + y) * m.RowPitch + (bx + x) * 4) != first) { same = false; break; }
                uniformBlocks += same;
            }
        ctx->Unmap(staging, 0);
        return uniformBlocks;
    };

    int total = (W / 4) * (H / 4);
    int baseline = drawAndCount(false);
    int withVrs = drawAndCount(true);
    int after = drawAndCount(false);
    printf("uniform 4x4 blocks of %d: plain %d, vrs events %d, after disable %d\n", total, baseline, withVrs, after);
    printf("enable count %d, last error %d\n", enables(), lastError());
    return 0;
}
