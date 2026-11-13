// Stereo resize stress for BasisVrsD3D11.dll: two slice eye arrays drawn in one pass (single pass
// instanced, slice from SV_RenderTargetArrayIndex), eye size flipping 2644 <-> 3136 like the OpenVR
// stage swap, the shading rate image recreated one frame late the way the C# pass does on a resize,
// and IDXGIDevice3::Trim forced every frame (the driver path the editor crashed in).
// Also covers misuse (nothing bound, null image, depth only passes, release while enabled, trim
// mid enable) and times every frame to the GPU so stalls show as stutters. Prints one RESULT line.
// Usage: harness_stereo <plugin.dll> [frames]. Exit 0 = every check passed.
#define NOMINMAX
#include <windows.h>
#include <d3d11_3.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <vector>
#include <algorithm>
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

// Every creation is checked: on a GPU still releasing another process's work (or a reset device) a
// failed call must report itself, not surface later as an access violation with no output.
static void Check(HRESULT hr, const char* what)
{
    if (SUCCEEDED(hr)) return;
    printf("FAIL %s: hr %08lx, device removed reason %08lx
", what, (unsigned long)hr, g_Device ? (unsigned long)g_Device->GetDeviceRemovedReason() : 0ul);
    fflush(stdout);
    exit(20);
}

struct Eye { ID3D11Texture2D* rt = nullptr; ID3D11RenderTargetView* rtv = nullptr; UINT size = 0; };
struct Image { ID3D11Texture2D* tex = nullptr; UINT tiles = 0; };

static void MakeEye(Eye& e, UINT size)
{
    if (e.rtv) e.rtv->Release();
    if (e.rt) e.rt->Release();
    D3D11_TEXTURE2D_DESC td = {}; td.Width = size; td.Height = size; td.MipLevels = 1; td.ArraySize = 2; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.BindFlags = D3D11_BIND_RENDER_TARGET;
    Check(g_Device->CreateTexture2D(&td, nullptr, &e.rt), "eye texture");
    Check(g_Device->CreateRenderTargetView(e.rt, nullptr, &e.rtv), "eye rtv");
    e.size = size;
}

static void MakeImage(ID3D11DeviceContext* ctx, Image& img, UINT tiles)
{
    if (img.tex) img.tex->Release();
    D3D11_TEXTURE2D_DESC sd = {}; sd.Width = tiles; sd.Height = tiles; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = DXGI_FORMAT_R8_UINT; sd.SampleDesc.Count = 1; sd.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    Check(g_Device->CreateTexture2D(&sd, nullptr, &img.tex), "shading rate image");
    ID3D11UnorderedAccessView* uav = nullptr; Check(g_Device->CreateUnorderedAccessView(img.tex, nullptr, &uav), "image uav");
    UINT v[4] = { 10, 10, 10, 10 }; ctx->ClearUnorderedAccessViewUint(uav, v); uav->Release();
    img.tiles = tiles;
}

// Fraction of aligned 4x4 blocks that are one colour in slice `slice`, sampled from the top-left 256x256.
static float UniformFraction(ID3D11DeviceContext* ctx, Eye& e, UINT slice)
{
    D3D11_TEXTURE2D_DESC td = {}; td.Width = 256; td.Height = 256; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* staging = nullptr; Check(g_Device->CreateTexture2D(&td, nullptr, &staging), "staging");
    D3D11_BOX box = { 0, 0, 0, 256, 256, 1 };
    ctx->CopySubresourceRegion(staging, 0, 0, 0, 0, e.rt, D3D11CalcSubresource(0, slice, 1), &box);
    D3D11_MAPPED_SUBRESOURCE m; Check(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m), "map staging");
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
    setvbuf(stdout, nullptr, _IONBF, 0);
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
    Check(g_Device->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs), "vertex shader");
    Check(g_Device->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &ps), "pixel shader");

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

    // Waits for the GPU so a frame's cost (and any stall the plugin causes) is measured, not queued.
    ID3D11Query* idle = nullptr; { D3D11_QUERY_DESC qd = { D3D11_QUERY_EVENT, 0 }; Check(g_Device->CreateQuery(&qd, &idle), "event query"); }
    auto finish = [&]() { ctx->End(idle); BOOL done = FALSE; while (ctx->GetData(idle, &done, sizeof(done), 0) == S_FALSE) { SwitchToThread(); } };
    int failures = 0;
    auto check = [&](bool ok, const char* name) { printf("%s %s\n", ok ? "ok  " : "FAIL", name); if (!ok) failures++; };
    auto alive = [&]() { return g_Device->GetDeviceRemovedReason() == S_OK; };

    // 1) Covered stereo: both eye slices must come out in uniform 4x4 blocks, and nothing without VRS.
    MakeEye(eye, 2644); MakeImage(ctx, image, (2644 + 15) / 16);
    drawEyes(true);
    float left = UniformFraction(ctx, eye, 0), right = UniformFraction(ctx, eye, 1);
    drawEyes(false);
    float plainLeft = UniformFraction(ctx, eye, 0);
    printf("stereo covered: uniform 4x4 left %.3f right %.3f, plain %.3f\n", left, right, plainLeft);
    check(left > 0.99f && right > 0.99f, "vrs applies to both eye slices");
    check(plainLeft < 0.01f, "no vrs leaks into a pass that did not enable it");

    // 2) Stale image after a grow: 166 tiles over a 3136 eye must leave VRS off (no out of bounds read).
    MakeEye(eye, 3136);
    int before = uncovered ? uncovered() : 0;
    drawEyes(true);
    float stale = UniformFraction(ctx, eye, 0);
    int staleSkipped = (uncovered ? uncovered() : 0) - before;
    printf("stale image 166 tiles on 3136 eye: uniform 4x4 %.3f, skipped %d\n", stale, staleSkipped);
    check(uncovered != nullptr && stale < 0.01f && staleSkipped == 1, "stale shading rate image is refused");

    // 3) Misuse a plugin user (or a pass ordering bug) could produce. None may crash, error or leak VRS.
    MakeImage(ctx, image, (3136 + 15) / 16);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
    onEvent(1, image.tex); onEvent(2, nullptr);
    check(alive() && lastError() == 0, "enable with nothing bound");
    onEvent(1, nullptr); onEvent(2, nullptr);
    check(alive() && lastError() == 0, "enable with a null image");
    {
        D3D11_TEXTURE2D_DESC dd = {}; dd.Width = 3136; dd.Height = 3136; dd.MipLevels = 1; dd.ArraySize = 2; dd.Format = DXGI_FORMAT_D32_FLOAT; dd.SampleDesc.Count = 1; dd.BindFlags = D3D11_BIND_DEPTH_STENCIL;
        ID3D11Texture2D* depth = nullptr; Check(g_Device->CreateTexture2D(&dd, nullptr, &depth), "depth texture");
        ID3D11DepthStencilView* dsv = nullptr; Check(g_Device->CreateDepthStencilView(depth, nullptr, &dsv), "dsv");
        ctx->OMSetRenderTargets(0, nullptr, dsv);
        int e0 = enables();
        onEvent(1, image.tex);
        ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH, 1.0f, 0);
        onEvent(2, nullptr);
        check(alive() && enables() == e0 + 1, "depth only pass with a covering image enables");
        MakeImage(ctx, image, (2644 + 15) / 16);
        int u0 = uncovered ? uncovered() : 0;
        onEvent(1, image.tex); onEvent(2, nullptr);
        check(uncovered && uncovered() == u0 + 1, "depth only pass with a small image is refused");
        dsv->Release(); depth->Release();
    }
    MakeImage(ctx, image, (3136 + 15) / 16);
    ctx->OMSetRenderTargets(1, &eye.rtv, nullptr);
    onEvent(1, image.tex);
    onEvent(3, nullptr);
    drawEyes(false);
    check(alive() && UniformFraction(ctx, eye, 0) < 0.01f, "release while enabled leaves vrs off");
    onEvent(1, image.tex);
    if (dxgi3) dxgi3->Trim();
    ctx->DrawInstanced(3, 2, 0, 0);
    onEvent(2, nullptr);
    finish();
    check(alive(), "driver trim between enable and draw");

    // 4) Stress: eye size flipping every 7 frames like the OpenVR stage swap, image recreated one frame
    // late, driver trim every frame. Every frame is waited on so a stall shows as a stutter.
    UINT sizes[2] = { 2644, 3136 };
    UINT pendingTiles = image.tiles;
    int resizes = 0;
    std::vector<double> frameMs;
    LARGE_INTEGER freq, t0, t1; QueryPerformanceFrequency(&freq);
    for (int f = 0; f < frames && alive(); f++)
    {
        QueryPerformanceCounter(&t0);
        if (f % 7 == 0) { MakeEye(eye, sizes[(f / 7) & 1]); resizes++; }
        UINT want = (eye.size + 15) / 16;
        if (image.tiles != pendingTiles) { onEvent(3, nullptr); MakeImage(ctx, image, pendingTiles); }
        pendingTiles = want;
        drawEyes(true);
        if (dxgi3) dxgi3->Trim();
        finish();
        QueryPerformanceCounter(&t1);
        frameMs.push_back(1000.0 * (t1.QuadPart - t0.QuadPart) / freq.QuadPart);
    }
    bool device = alive();
    check(device, "device alive after resize and trim stress");
    check(lastError() == 0, "no nvapi errors");
    std::sort(frameMs.begin(), frameMs.end());
    auto pick = [&](double q) { return frameMs.empty() ? 0.0 : frameMs[(size_t)((frameMs.size() - 1) * q + 0.5)]; };
    double med = pick(0.5);
    int stutters = 0; for (double t : frameMs) if (t > std::max(med * 2.0, med + 8.0)) stutters++;
    printf("RESULT frames=%d resizes=%d enables=%d uncovered=%d last_error=%d device=%s covered_left=%.3f covered_right=%.3f plain=%.3f stale=%.3f frame_med=%.3f frame_p99=%.3f frame_max=%.3f frame_stutters=%d failures=%d\n",
        (int)frameMs.size(), resizes, enables(), uncovered ? uncovered() : -1, lastError(), device ? "alive" : "removed", left, right, plainLeft, stale, med, pick(0.99), pick(1.0), stutters, failures);
    printf(failures == 0 ? "PASS\n" : "FAIL\n");
    return failures == 0 ? 0 : 1;
}
