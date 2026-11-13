// Stereo resize stress for BasisVrsD3D11.dll: two slice eye arrays drawn in one pass (single pass
// instanced, slice from SV_RenderTargetArrayIndex), eye size flipping 2644 <-> 3136 like the OpenVR
// stage swap, the shading rate image recreated one frame late the way the C# pass does on a resize,
// and IDXGIDevice3::Trim forced every frame (the driver path the editor crashed in).
// Usage: harness_stereo <plugin.dll> [frames]. Exit 0 = device alive, VRS checks passed.
#include <windows.h>
#include <d3d11_3.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <stdio.h>
#include <string.h>
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

// Instance id picks the eye slice; the colour depends on every pixel so a 4x4 rate shows as uniform blocks.
static const char* kShader =
    "struct V { float4 p : SV_Position; uint slice : SV_RenderTargetArrayIndex; };\n"
    "V VS(uint id : SV_VertexID, uint inst : SV_InstanceID) { V o; float2 uv = float2((id << 1) & 2, id & 2); o.p = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1); o.slice = inst; return o; }\n"
    "float4 PS(V i) : SV_Target { uint2 q = (uint2)i.p.xy; return float4((q.x % 251) / 255.0, (q.y % 241) / 255.0, ((q.x * 7 + q.y * 13 + i.slice * 97) % 256) / 255.0, 1); }\n";

struct Eye { ID3D11Texture2D* rt = nullptr; ID3D11RenderTargetView* rtv = nullptr; UINT size = 0; };
struct Image { ID3D11Texture2D* tex = nullptr; UINT tiles = 0; };

static void MakeEye(Eye& e, UINT size)
{
    if (e.rtv) e.rtv->Release();
    if (e.rt) e.rt->Release();
    D3D11_TEXTURE2D_DESC td = {}; td.Width = size; td.Height = size; td.MipLevels = 1; td.ArraySize = 2; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.BindFlags = D3D11_BIND_RENDER_TARGET;
    g_Device->CreateTexture2D(&td, nullptr, &e.rt);
    g_Device->CreateRenderTargetView(e.rt, nullptr, &e.rtv);
    e.size = size;
}

static void MakeImage(ID3D11DeviceContext* ctx, Image& img, UINT tiles)
{
    if (img.tex) img.tex->Release();
    D3D11_TEXTURE2D_DESC sd = {}; sd.Width = tiles; sd.Height = tiles; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = DXGI_FORMAT_R8_UINT; sd.SampleDesc.Count = 1; sd.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    g_Device->CreateTexture2D(&sd, nullptr, &img.tex);
    ID3D11UnorderedAccessView* uav; g_Device->CreateUnorderedAccessView(img.tex, nullptr, &uav);
    UINT v[4] = { 10, 10, 10, 10 }; ctx->ClearUnorderedAccessViewUint(uav, v); uav->Release();
    img.tiles = tiles;
}

// Fraction of aligned 4x4 blocks that are one colour in slice `slice`, sampled from the top-left 256x256.
static float UniformFraction(ID3D11DeviceContext* ctx, Eye& e, UINT slice)
{
    D3D11_TEXTURE2D_DESC td = {}; td.Width = 256; td.Height = 256; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* staging; g_Device->CreateTexture2D(&td, nullptr, &staging);
    D3D11_BOX box = { 0, 0, 0, 256, 256, 1 };
    ctx->CopySubresourceRegion(staging, 0, 0, 0, 0, e.rt, D3D11CalcSubresource(0, slice, 1), &box);
    D3D11_MAPPED_SUBRESOURCE m; ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m);
    int uniform = 0, total = 0;
    for (UINT by = 0; by < 256; by += 4)
        for (UINT bx = 0; bx < 256; bx += 4)
        {
            unsigned int first = *(unsigned int*)((unsigned char*)m.pData + by * m.RowPitch + bx * 4);
            bool same = true;
            for (UINT y = 0; y < 4 && same; y++) for (UINT x = 0; x < 4; x++) if (*(unsigned int*)((unsigned char*)m.pData + (by + y) * m.RowPitch + (bx + x) * 4) != first) { same = false; break; }
            uniform += same; total++;
        }
    ctx->Unmap(staging, 0);
    staging->Release();
    return (float)uniform / total;
}

int main(int argc, char** argv)
{
    if (argc < 2) { printf("usage: harness_stereo plugin.dll [frames]\n"); return 2; }
    int frames = argc > 2 ? atoi(argv[2]) : 600;
    IDXGIFactory1* factory = nullptr;
    CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory);
    IDXGIAdapter1* chosen = nullptr, *a = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; i++) { DXGI_ADAPTER_DESC1 d; a->GetDesc1(&d); if (!chosen && d.VendorId == 0x10DE) { chosen = a; continue; } a->Release(); }
    if (!chosen) { printf("no NVIDIA adapter\n"); return 3; }
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_1;
    HRESULT hr = D3D11CreateDevice(chosen, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &g_Device, nullptr, &ctx);
    if (FAILED(hr)) { printf("device failed %08lx\n", (unsigned long)hr); return 4; }
    D3D11_FEATURE_DATA_D3D11_OPTIONS3 o3 = {};
    g_Device->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS3, &o3, sizeof(o3));
    if (!o3.VPAndRTArrayIndexFromAnyShaderFeedingRasterizer) { printf("no RT array index from VS\n"); return 4; }
    IDXGIDevice3* dxgi3 = nullptr; g_Device->QueryInterface(__uuidof(IDXGIDevice3), (void**)&dxgi3);

    HMODULE plugin = LoadLibraryA(argv[1]);
    if (!plugin) { printf("plugin load failed\n"); return 5; }
    auto load = (void (__stdcall*)(IUnityInterfaces*))GetProcAddress(plugin, "UnityPluginLoad");
    auto status = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_Status");
    auto enables = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_EnableCount");
    auto uncovered = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_UncoveredCount");
    auto lastError = (int (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_LastError");
    auto eventFunc = (void* (__stdcall*)())GetProcAddress(plugin, "BasisVrsD3D11_GetRenderEventFunc");
    load(&g_Interfaces);
    printf("plugin status %d, uncovered export %s\n", status(), uncovered ? "yes" : "no (old plugin)");
    if (status() != 1) return 6;
    auto onEvent = (void (__stdcall*)(int, void*))eventFunc();

    ID3DBlob *vsb, *psb, *err = nullptr;
    if (FAILED(D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vsb, &err))) { printf("vs: %s\n", (char*)err->GetBufferPointer()); return 7; }
    if (FAILED(D3DCompile(kShader, strlen(kShader), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &psb, &err))) { printf("ps: %s\n", (char*)err->GetBufferPointer()); return 7; }
    ID3D11VertexShader* vs; ID3D11PixelShader* ps;
    g_Device->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs);
    g_Device->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps);

    Eye eye; Image image;
    auto drawEyes = [&](bool vrs)
    {
        float clear[4] = { 0, 0, 0, 0 };
        ctx->ClearRenderTargetView(eye.rtv, clear);
        ctx->OMSetRenderTargets(1, &eye.rtv, nullptr);
        D3D11_VIEWPORT vp = { 0, 0, (float)eye.size, (float)eye.size, 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0); ctx->PSSetShader(ps, nullptr, 0);
        if (vrs) onEvent(1, image.tex);
        ctx->DrawInstanced(3, 2, 0, 0);
        if (vrs) onEvent(2, nullptr);
    };

    // 1) Covered stereo: both eye slices must come out in uniform 4x4 blocks.
    MakeEye(eye, 2644); MakeImage(ctx, image, (2644 + 15) / 16);
    drawEyes(true);
    float left = UniformFraction(ctx, eye, 0), right = UniformFraction(ctx, eye, 1);
    printf("stereo covered: uniform 4x4 left %.3f right %.3f\n", left, right);
    drawEyes(false);
    float plainLeft = UniformFraction(ctx, eye, 0);
    printf("stereo plain: uniform 4x4 left %.3f\n", plainLeft);

    // 2) Stale image after a grow: 166 tiles over a 3136 eye. Fixed plugin leaves VRS off.
    MakeEye(eye, 3136);
    int before = uncovered ? uncovered() : 0;
    drawEyes(true);
    float stale = UniformFraction(ctx, eye, 0);
    printf("stale image 166 tiles on 3136 eye: uniform 4x4 %.3f, skipped %d\n", stale, (uncovered ? uncovered() : 0) - before);

    // 3) Stress: flip the eye size every 7 frames, recreate the image one frame late, trim every frame.
    UINT sizes[2] = { 2644, 3136 };
    UINT pendingTiles = image.tiles;
    int resizes = 0;
    for (int f = 0; f < frames; f++)
    {
        if (f % 7 == 0) { MakeEye(eye, sizes[(f / 7) & 1]); resizes++; }
        UINT want = (eye.size + 15) / 16;
        if (image.tiles != pendingTiles) { onEvent(3, nullptr); MakeImage(ctx, image, pendingTiles); }
        pendingTiles = want;
        drawEyes(true);
        ctx->Flush();
        if (dxgi3) dxgi3->Trim();
        hr = g_Device->GetDeviceRemovedReason();
        if (hr != S_OK) { printf("DEVICE REMOVED at frame %d: %08lx\n", f, (unsigned long)hr); return 10; }
    }
    ctx->Flush();
    ID3D11Query* q; D3D11_QUERY_DESC qd = { D3D11_QUERY_EVENT, 0 }; g_Device->CreateQuery(&qd, &q); ctx->End(q);
    BOOL done = FALSE; while (ctx->GetData(q, &done, sizeof(done), 0) == S_FALSE) Sleep(1);
    hr = g_Device->GetDeviceRemovedReason();
    printf("stress: %d frames, %d resizes, device %s, enables %d, skipped %d, last error %d\n", frames, resizes, hr == S_OK ? "alive" : "REMOVED", enables(), uncovered ? uncovered() : -1, lastError());
    bool pass = hr == S_OK && left > 0.99f && right > 0.99f && plainLeft < 0.01f && (!uncovered || stale < 0.01f);
    printf(pass ? "PASS\n" : "FAIL\n");
    return pass ? 0 : 1;
}
