//! The conversion pass: one D3D11 pixel-shader draw turning the
//! decoder's NV12 frame into the shared BGRA texture, with matrix/range
//! taken from the frame's stated [`ColorInfo`].
//!
//! Chroma is point-sampled (top-left co-sited replication), matching the
//! reference converter the validation tests compare against.

use std::ffi::c_void;

use media_decode::ColorInfo;
use windows::Win32::Graphics::Direct3D::Fxc::D3DCompile;
use windows::Win32::Graphics::Direct3D::{D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST, ID3DBlob};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_CONSTANT_BUFFER, D3D11_BIND_SHADER_RESOURCE, D3D11_BUFFER_DESC,
    D3D11_CPU_ACCESS_WRITE, D3D11_MAP_WRITE, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING, D3D11_VIEWPORT, ID3D11Buffer, ID3D11Device,
    ID3D11DeviceContext, ID3D11PixelShader, ID3D11RenderTargetView, ID3D11ShaderResourceView,
    ID3D11Texture2D, ID3D11VertexShader,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_NV12, DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R8G8_UNORM, DXGI_SAMPLE_DESC,
};
use windows::core::{IUnknown, Interface, PCSTR};

use crate::PresentError;
use crate::reference::coefficients;

const SHADER_SOURCE: &str = r#"
struct VSOut { float4 pos : SV_Position; };

VSOut vs_main(uint id : SV_VertexID) {
    VSOut o;
    float2 xy = float2((id << 1) & 2, id & 2);
    o.pos = float4(xy * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

Texture2D<float>  y_tex  : register(t0);
Texture2D<float2> uv_tex : register(t1);

cbuffer Convert : register(b0) {
    float4 coef_r;
    float4 coef_g;
    float4 coef_b;
};

float4 ps_main(float4 pos : SV_Position) : SV_Target {
    int2 p = int2(pos.xy);
    float4 yuv1 = float4(y_tex.Load(int3(p, 0)), uv_tex.Load(int3(p >> 1, 0)), 1.0);
    return float4(saturate(dot(yuv1, coef_r)),
                  saturate(dot(yuv1, coef_g)),
                  saturate(dot(yuv1, coef_b)), 1.0);
}
"#;

fn d3d<T>(r: windows::core::Result<T>, what: &str) -> Result<T, PresentError> {
    r.map_err(|e| PresentError(format!("{what}: {e}")))
}

fn compile(entry: &str, target: &str) -> Result<ID3DBlob, PresentError> {
    let entry_z = format!("{entry}\0");
    let target_z = format!("{target}\0");
    let mut code: Option<ID3DBlob> = None;
    let mut errors: Option<ID3DBlob> = None;
    // SAFETY: source pointer + length describe the live SHADER_SOURCE bytes;
    // entry/target are NUL-terminated locals outliving the call; out-params
    // are checked before use.
    let result = unsafe {
        D3DCompile(
            SHADER_SOURCE.as_ptr() as *const c_void,
            SHADER_SOURCE.len(),
            PCSTR::null(),
            None,
            None,
            PCSTR(entry_z.as_ptr()),
            PCSTR(target_z.as_ptr()),
            0,
            0,
            &mut code,
            Some(&mut errors),
        )
    };
    if let Err(e) = result {
        // SAFETY: a returned error blob is a live ID3DBlob whose buffer is
        // valid for its stated size.
        let detail = errors
            .map(|blob| unsafe {
                let ptr = blob.GetBufferPointer() as *const u8;
                let len = blob.GetBufferSize();
                String::from_utf8_lossy(std::slice::from_raw_parts(ptr, len)).into_owned()
            })
            .unwrap_or_default();
        return Err(PresentError(format!("D3DCompile({entry}): {e} {detail}")));
    }
    code.ok_or_else(|| PresentError(format!("D3DCompile({entry}): no code")))
}

/// NV12 conversion input: a default-usage texture the pass samples,
/// filled either from a CPU-writable staging texture (software frames)
/// or by a GPU subresource copy from a decoder-owned slice (the DXVA
/// path). Recreated when the coded size changes (a mid-stream
/// renegotiation).
struct Nv12Textures {
    /// Created on first CPU upload; the slice path never needs it.
    staging: Option<ID3D11Texture2D>,
    sampled: ID3D11Texture2D,
    y_srv: ID3D11ShaderResourceView,
    uv_srv: ID3D11ShaderResourceView,
    width: u32,
    height: u32,
}

impl Nv12Textures {
    fn new(device: &ID3D11Device, width: u32, height: u32) -> Result<Self, PresentError> {
        let base = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_NV12,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        // SAFETY: D3D11 object creation through owned wrappers; descriptors
        // are plain structs and every out-param is checked before use. The
        // plane SRV descs use the D3D11-defined NV12 plane formats.
        unsafe {
            let mut sampled = None;
            d3d(
                device.CreateTexture2D(&base, None, Some(&mut sampled)),
                "CreateTexture2D (NV12 sampled)",
            )?;
            let sampled = sampled.ok_or_else(|| PresentError("no NV12 texture".into()))?;

            let mut y_srv = None;
            let y_desc = plane_srv_desc(DXGI_FORMAT_R8_UNORM);
            d3d(
                device.CreateShaderResourceView(&sampled, Some(&y_desc), Some(&mut y_srv)),
                "CreateShaderResourceView (Y)",
            )?;
            let mut uv_srv = None;
            let uv_desc = plane_srv_desc(DXGI_FORMAT_R8G8_UNORM);
            d3d(
                device.CreateShaderResourceView(&sampled, Some(&uv_desc), Some(&mut uv_srv)),
                "CreateShaderResourceView (UV)",
            )?;
            Ok(Self {
                staging: None,
                sampled,
                y_srv: y_srv.ok_or_else(|| PresentError("no Y SRV".into()))?,
                uv_srv: uv_srv.ok_or_else(|| PresentError("no UV SRV".into()))?,
                width,
                height,
            })
        }
    }

    fn ensure_staging(&mut self, device: &ID3D11Device) -> Result<&ID3D11Texture2D, PresentError> {
        if self.staging.is_none() {
            let desc = D3D11_TEXTURE2D_DESC {
                Width: self.width,
                Height: self.height,
                MipLevels: 1,
                ArraySize: 1,
                Format: DXGI_FORMAT_NV12,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                Usage: D3D11_USAGE_STAGING,
                BindFlags: 0,
                CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
                MiscFlags: 0,
            };
            // SAFETY: texture creation through owned wrappers with a
            // checked out-param.
            unsafe {
                let mut staging = None;
                d3d(
                    device.CreateTexture2D(&desc, None, Some(&mut staging)),
                    "CreateTexture2D (NV12 staging)",
                )?;
                self.staging = Some(staging.ok_or_else(|| PresentError("no NV12 staging".into()))?);
            }
        }
        Ok(self.staging.as_ref().expect("staging just ensured"))
    }
}

fn plane_srv_desc(
    format: windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT,
) -> windows::Win32::Graphics::Direct3D11::D3D11_SHADER_RESOURCE_VIEW_DESC {
    use windows::Win32::Graphics::Direct3D::D3D11_SRV_DIMENSION_TEXTURE2D;
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_SHADER_RESOURCE_VIEW_DESC, D3D11_SHADER_RESOURCE_VIEW_DESC_0, D3D11_TEX2D_SRV,
    };
    D3D11_SHADER_RESOURCE_VIEW_DESC {
        Format: format,
        ViewDimension: D3D11_SRV_DIMENSION_TEXTURE2D,
        Anonymous: D3D11_SHADER_RESOURCE_VIEW_DESC_0 {
            Texture2D: D3D11_TEX2D_SRV {
                MostDetailedMip: 0,
                MipLevels: 1,
            },
        },
    }
}

/// The compiled pass plus its per-frame state.
pub(crate) struct ConvertPass {
    vs: ID3D11VertexShader,
    ps: ID3D11PixelShader,
    cbuffer: ID3D11Buffer,
    rtv: ID3D11RenderTargetView,
    textures: Option<Nv12Textures>,
    current_color: Option<ColorInfo>,
    out_width: u32,
    out_height: u32,
}

impl ConvertPass {
    pub(crate) fn new(
        device: &ID3D11Device,
        target: &ID3D11Texture2D,
        out_width: u32,
        out_height: u32,
    ) -> Result<Self, PresentError> {
        let vs_blob = compile("vs_main", "vs_4_0")?;
        let ps_blob = compile("ps_main", "ps_4_0")?;
        // SAFETY: shader blobs expose buffers valid for their stated sizes;
        // all creation goes through owned wrappers with checked out-params.
        unsafe {
            let vs_bytes = std::slice::from_raw_parts(
                vs_blob.GetBufferPointer() as *const u8,
                vs_blob.GetBufferSize(),
            );
            let ps_bytes = std::slice::from_raw_parts(
                ps_blob.GetBufferPointer() as *const u8,
                ps_blob.GetBufferSize(),
            );
            let mut vs = None;
            d3d(
                device.CreateVertexShader(vs_bytes, None, Some(&mut vs)),
                "CreateVertexShader",
            )?;
            let mut ps = None;
            d3d(
                device.CreatePixelShader(ps_bytes, None, Some(&mut ps)),
                "CreatePixelShader",
            )?;

            let cb_desc = D3D11_BUFFER_DESC {
                ByteWidth: 48,
                Usage: D3D11_USAGE_DEFAULT,
                BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
                CPUAccessFlags: 0,
                MiscFlags: 0,
                StructureByteStride: 0,
            };
            let initial = coefficients(ColorInfo::default());
            let init_data = D3D11_SUBRESOURCE_DATA {
                pSysMem: initial.as_ptr() as *const c_void,
                SysMemPitch: 0,
                SysMemSlicePitch: 0,
            };
            let mut cbuffer = None;
            d3d(
                device.CreateBuffer(&cb_desc, Some(&init_data), Some(&mut cbuffer)),
                "CreateBuffer (convert cb)",
            )?;

            let mut rtv = None;
            d3d(
                device.CreateRenderTargetView(target, None, Some(&mut rtv)),
                "CreateRenderTargetView (shared)",
            )?;

            Ok(Self {
                vs: vs.ok_or_else(|| PresentError("no vertex shader".into()))?,
                ps: ps.ok_or_else(|| PresentError("no pixel shader".into()))?,
                cbuffer: cbuffer.ok_or_else(|| PresentError("no constant buffer".into()))?,
                rtv: rtv.ok_or_else(|| PresentError("no render target view".into()))?,
                textures: None,
                current_color: None,
                out_width,
                out_height,
            })
        }
    }

    /// Upload the packed NV12 bytes for this frame. Runs outside the keyed
    /// mutex: it only touches the pass's private textures.
    pub(crate) fn upload(
        &mut self,
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
        frame_width: u32,
        frame_height: u32,
        nv12: &[u8],
    ) -> Result<(), PresentError> {
        if frame_width == 0
            || frame_height == 0
            || !frame_width.is_multiple_of(2)
            || !frame_height.is_multiple_of(2)
        {
            return Err(PresentError(format!(
                "NV12 frame dimensions must be even and non-zero, got {frame_width}x{frame_height}"
            )));
        }
        let needed = frame_width as usize * frame_height as usize * 3 / 2;
        if nv12.len() < needed {
            return Err(PresentError(format!(
                "short NV12 buffer: {} < {needed}",
                nv12.len()
            )));
        }
        if self
            .textures
            .as_ref()
            .is_none_or(|t| t.width != frame_width || t.height != frame_height)
        {
            self.textures = Some(Nv12Textures::new(device, frame_width, frame_height)?);
        }
        let textures = self.textures.as_mut().expect("textures just ensured");
        let staging = textures.ensure_staging(device)?.clone();

        // SAFETY: Map(WRITE) on the staging texture yields a mapping whose Y
        // plane spans RowPitch*height bytes and whose UV plane follows at
        // RowPitch*height for another RowPitch*height/2 (the D3D11 planar
        // staging layout); every row copy writes `frame_width <= RowPitch`
        // bytes inside those bounds, reading from `nv12` which was length-
        // checked above.
        unsafe {
            let mut mapped = Default::default();
            d3d(
                context.Map(&staging, 0, D3D11_MAP_WRITE, 0, Some(&mut mapped)),
                "Map (NV12 staging)",
            )?;
            let row_pitch = mapped.RowPitch as usize;
            let base = mapped.pData as *mut u8;
            let (w, h) = (frame_width as usize, frame_height as usize);
            for row in 0..h {
                std::ptr::copy_nonoverlapping(
                    nv12.as_ptr().add(row * w),
                    base.add(row * row_pitch),
                    w,
                );
            }
            let uv_src = nv12.as_ptr().add(w * h);
            let uv_base = base.add(row_pitch * h);
            for row in 0..h / 2 {
                std::ptr::copy_nonoverlapping(uv_src.add(row * w), uv_base.add(row * row_pitch), w);
            }
            context.Unmap(&staging, 0);
            context.CopyResource(&textures.sampled, &staging);
        }
        Ok(())
    }

    /// Fill the sampled texture from a decoder-owned NV12 texture-array
    /// slice with one GPU subresource copy (the DXVA input path). The
    /// subresource index comes from `IMFDXGIBuffer::GetSubresourceIndex`
    /// and is honoured as given (never assume slice 0). Returns `false`,
    /// copying nothing, for a slice on a device other than `device`: a
    /// decoder rebuilt mid-stream brings a new device, and frames the old
    /// one decoded can still be waiting to present.
    ///
    /// # Safety
    /// `texture_ptr` must be a live `ID3D11Texture2D*` and `subresource` a
    /// valid subresource index of it.
    pub(crate) unsafe fn upload_slice(
        &mut self,
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
        texture_ptr: *mut c_void,
        subresource: u32,
    ) -> Result<bool, PresentError> {
        // SAFETY: caller guarantees a live texture; from_raw_borrowed does
        // not consume the caller's reference. GetDevice and GetDesc only
        // read it. The whole-subresource copy requires both resources on
        // the context's device, matching formats and a destination at
        // least the source's mip size, all ensured below.
        unsafe {
            let source = ID3D11Texture2D::from_raw_borrowed(&texture_ptr)
                .ok_or_else(|| PresentError("null decoder texture".into()))?;
            // COM identity is defined on IUnknown only.
            let identity = |d: &ID3D11Device| d.cast::<IUnknown>().map(|u| u.as_raw());
            let source_device = source
                .GetDevice()
                .map_err(|e| PresentError(format!("GetDevice (decoder slice): {e}")))?;
            let same_device = identity(&source_device)
                .and_then(|s| identity(device).map(|d| s == d))
                .map_err(|e| PresentError(format!("cast IUnknown: {e}")))?;
            if !same_device {
                return Ok(false);
            }
            let mut desc = Default::default();
            source.GetDesc(&mut desc);
            if desc.Format != DXGI_FORMAT_NV12 {
                return Err(PresentError(format!(
                    "decoder slice is not NV12 (format {})",
                    desc.Format.0
                )));
            }
            if self
                .textures
                .as_ref()
                .is_none_or(|t| t.width != desc.Width || t.height != desc.Height)
            {
                self.textures = Some(Nv12Textures::new(device, desc.Width, desc.Height)?);
            }
            let textures = self.textures.as_ref().expect("textures just ensured");
            // A whole-subresource NV12 copy moves both planes.
            context.CopySubresourceRegion(&textures.sampled, 0, 0, 0, 0, source, subresource, None);
        }
        Ok(true)
    }

    /// Record the conversion draw into the shared texture. The caller holds
    /// the keyed mutex across this call.
    pub(crate) fn draw(&mut self, context: &ID3D11DeviceContext, color: ColorInfo) {
        let Some(textures) = self.textures.as_ref() else {
            return;
        };
        // SAFETY: pipeline-state COM calls on live, owned objects; the
        // constant-buffer update writes 48 bytes from a live [[f32; 4]; 3].
        unsafe {
            if self.current_color != Some(color) {
                let coef = coefficients(color);
                context.UpdateSubresource(
                    &self.cbuffer,
                    0,
                    None,
                    coef.as_ptr() as *const c_void,
                    0,
                    0,
                );
                self.current_color = Some(color);
            }

            // A frame smaller than the target leaves stale pixels outside
            // the viewport: clear first so they read as black instead.
            if textures.width < self.out_width || textures.height < self.out_height {
                context.ClearRenderTargetView(&self.rtv, &[0.0, 0.0, 0.0, 1.0]);
            }
            let viewport = D3D11_VIEWPORT {
                TopLeftX: 0.0,
                TopLeftY: 0.0,
                Width: textures.width.min(self.out_width) as f32,
                Height: textures.height.min(self.out_height) as f32,
                MinDepth: 0.0,
                MaxDepth: 1.0,
            };

            context.IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context.IASetInputLayout(None);
            context.VSSetShader(&self.vs, None);
            context.PSSetShader(&self.ps, None);
            context.PSSetShaderResources(
                0,
                Some(&[Some(textures.y_srv.clone()), Some(textures.uv_srv.clone())]),
            );
            context.PSSetConstantBuffers(0, Some(&[Some(self.cbuffer.clone())]));
            context.RSSetViewports(Some(&[viewport]));
            context.OMSetRenderTargets(Some(&[Some(self.rtv.clone())]), None);
            context.Draw(3, 0);
            // Unbind so the next frame's CopyResource into the sampled
            // texture never races a lingering SRV binding.
            context.PSSetShaderResources(0, Some(&[None, None]));
            context.OMSetRenderTargets(Some(&[None]), None);
        }
    }
}
