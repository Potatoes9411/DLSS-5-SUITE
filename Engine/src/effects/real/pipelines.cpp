#include "effects/real/pipelines.h"

#include "shaders/BlitPS.h"
#include "shaders/BlitVS.h"
#include "shaders/ConvertCS.h"
#include "shaders/DownsampleCS.h"
#include "shaders/FinalizeCS.h"
#include "shaders/FlowToMvCS.h"
#include "shaders/MatchCS.h"

#include <array>
#include <span>

namespace real {
namespace {

using infra::Fail;
using infra::Result;
using infra::Status;

[[nodiscard]] D3D12_DESCRIPTOR_RANGE Range(D3D12_DESCRIPTOR_RANGE_TYPE type, UINT count) noexcept
{
    return D3D12_DESCRIPTOR_RANGE{ type, count, 0, 0, D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND };
}

struct Roots
{
    Com<ID3D12RootSignature> compute;
    Com<ID3D12RootSignature> blit;
};

struct ComputePsos
{
    Com<ID3D12PipelineState> convert;
    Com<ID3D12PipelineState> downsample;
    Com<ID3D12PipelineState> match;
    Com<ID3D12PipelineState> finalize;
    Com<ID3D12PipelineState> flowToMv;
};

} // namespace

Result<Pipelines, Error> CreatePipelines(const GpuDevice& gpu, DXGI_FORMAT swapChainFormat) noexcept
{
    static constexpr auto CreateBlit = [] [[nodiscard]] (const GpuDevice& gpu, ID3D12RootSignature* root, DXGI_FORMAT format) noexcept -> Result<Com<ID3D12PipelineState>, Error> {
        static constexpr auto BlitDescription = [] [[nodiscard]] (ID3D12RootSignature * root, DXGI_FORMAT format) noexcept -> D3D12_GRAPHICS_PIPELINE_STATE_DESC {
            static constexpr auto WithBlitState = [] [[nodiscard]] (D3D12_GRAPHICS_PIPELINE_STATE_DESC desc, DXGI_FORMAT format) noexcept -> D3D12_GRAPHICS_PIPELINE_STATE_DESC {
                static constexpr auto WithTarget = [] [[nodiscard]] (D3D12_GRAPHICS_PIPELINE_STATE_DESC desc, DXGI_FORMAT format) noexcept -> D3D12_GRAPHICS_PIPELINE_STATE_DESC {
                    desc.NumRenderTargets = 1;
                    desc.RTVFormats[0] = format;
                    desc.SampleDesc = DXGI_SAMPLE_DESC{ 1, 0 };
                    return desc;
                };
                desc.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
                desc.SampleMask = UINT_MAX;
                desc.RasterizerState = D3D12_RASTERIZER_DESC{ D3D12_FILL_MODE_SOLID, D3D12_CULL_MODE_NONE, FALSE, 0, 0.0f, 0.0f, TRUE, FALSE, FALSE, 0, D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF };
                desc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
                return WithTarget(desc, format);
            };
            D3D12_GRAPHICS_PIPELINE_STATE_DESC desc{};
            desc.pRootSignature = root;
            desc.VS = D3D12_SHADER_BYTECODE{ kBlitVS, sizeof(kBlitVS) };
            desc.PS = D3D12_SHADER_BYTECODE{ kBlitPS, sizeof(kBlitPS) };
            return WithBlitState(desc, format);
        };
        const D3D12_GRAPHICS_PIPELINE_STATE_DESC desc = BlitDescription(root, format);
        Com<ID3D12PipelineState> pso;
        return Check(gpu.device->CreateGraphicsPipelineState(&desc, IID_PPV_ARGS(&pso)), ApiCall::CreateGraphicsPipelineState).transform([&pso] { return pso; });
    };

    static constexpr auto CreateRoots = [] [[nodiscard]] (const GpuDevice& gpu) noexcept -> Result<Roots, Error> {
        static constexpr auto Sampler = [] [[nodiscard]] (UINT shaderRegister, D3D12_FILTER filter, D3D12_SHADER_VISIBILITY visibility) noexcept -> D3D12_STATIC_SAMPLER_DESC {
            static constexpr auto WithRegister = [] [[nodiscard]] (D3D12_STATIC_SAMPLER_DESC s, UINT shaderRegister, D3D12_SHADER_VISIBILITY visibility) noexcept -> D3D12_STATIC_SAMPLER_DESC {
                s.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                s.MaxLOD = D3D12_FLOAT32_MAX;
                s.ShaderRegister = shaderRegister;
                s.ShaderVisibility = visibility;
                return s;
            };
            D3D12_STATIC_SAMPLER_DESC s{};
            s.Filter = filter;
            s.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            s.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            return WithRegister(s, shaderRegister, visibility);
        };

        static constexpr auto ConstantsParameter = [] [[nodiscard]] () noexcept -> D3D12_ROOT_PARAMETER {
            D3D12_ROOT_PARAMETER p{};
            p.ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
            p.Constants = D3D12_ROOT_CONSTANTS{ 0, 0, kRootConstantCount };
            p.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
            return p;
        };

        static constexpr auto TableParameter = [] [[nodiscard]] (const D3D12_DESCRIPTOR_RANGE* range, D3D12_SHADER_VISIBILITY visibility) noexcept -> D3D12_ROOT_PARAMETER {
            D3D12_ROOT_PARAMETER p{};
            p.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            p.DescriptorTable = D3D12_ROOT_DESCRIPTOR_TABLE{ 1, range };
            p.ShaderVisibility = visibility;
            return p;
        };

        static constexpr auto Serialize = [] [[nodiscard]] (const GpuDevice& gpu, const D3D12_ROOT_SIGNATURE_DESC& desc) noexcept -> Result<Com<ID3D12RootSignature>, Error> {
            Com<ID3DBlob> blob;
            Com<ID3DBlob> errors;
            return Check(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1_0, &blob, &errors), ApiCall::SerializeRootSignature)
                .and_then([&]() -> Result<Com<ID3D12RootSignature>, Error> {
                    Com<ID3D12RootSignature> root;
                    const HRESULT hr = gpu.device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&root));
                    return Check(hr, ApiCall::CreateRootSignature).transform([&root] { return root; });
                });
        };

        static constexpr auto CreateComputeRoot = [] [[nodiscard]] (const GpuDevice& gpu) noexcept -> Result<Com<ID3D12RootSignature>, Error> {
            const D3D12_DESCRIPTOR_RANGE srvRange = Range(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, kComputeSrvCount);
            const D3D12_DESCRIPTOR_RANGE uavRange = Range(D3D12_DESCRIPTOR_RANGE_TYPE_UAV, kComputeUavCount);
            const std::array<D3D12_ROOT_PARAMETER, 3> params{ ConstantsParameter(), TableParameter(&srvRange, D3D12_SHADER_VISIBILITY_ALL), TableParameter(&uavRange, D3D12_SHADER_VISIBILITY_ALL) };
            const std::array<D3D12_STATIC_SAMPLER_DESC, 2> samplers{ Sampler(0, D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_SHADER_VISIBILITY_ALL),
                                                                     Sampler(1, D3D12_FILTER_MIN_MAG_MIP_POINT, D3D12_SHADER_VISIBILITY_ALL) };
            return Serialize(gpu, D3D12_ROOT_SIGNATURE_DESC{ 3, params.data(), 2, samplers.data(), D3D12_ROOT_SIGNATURE_FLAG_NONE });
        };

        static constexpr auto CreateBlitRoot = [] [[nodiscard]] (const GpuDevice& gpu) noexcept -> Result<Com<ID3D12RootSignature>, Error> {
            static constexpr auto BlitFlags = [] [[nodiscard]] () noexcept -> D3D12_ROOT_SIGNATURE_FLAGS {
                return D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS | D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS | D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS;
            };
            const D3D12_DESCRIPTOR_RANGE srvRange = Range(D3D12_DESCRIPTOR_RANGE_TYPE_SRV, kBlitSrvCount);
            const std::array<D3D12_ROOT_PARAMETER, 2> params{ ConstantsParameter(), TableParameter(&srvRange, D3D12_SHADER_VISIBILITY_PIXEL) };
            const std::array<D3D12_STATIC_SAMPLER_DESC, 2> samplers{ Sampler(0, D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_SHADER_VISIBILITY_PIXEL),
                                                                     Sampler(1, D3D12_FILTER_MIN_MAG_MIP_POINT, D3D12_SHADER_VISIBILITY_PIXEL) };
            return Serialize(gpu, D3D12_ROOT_SIGNATURE_DESC{ 2, params.data(), 2, samplers.data(), BlitFlags() });
        };
        return CreateComputeRoot(gpu).and_then(
            [&gpu](const Com<ID3D12RootSignature>& compute) { return CreateBlitRoot(gpu).transform([&compute](const Com<ID3D12RootSignature>& blit) { return Roots{ compute, blit }; }); });
    };

    static constexpr auto CreateComputePsos = [] [[nodiscard]] (const GpuDevice& gpu, ID3D12RootSignature* root) noexcept -> Result<ComputePsos, Error> {
        static constexpr auto CreateCompute = [] [[nodiscard]] (const GpuDevice& gpu, ID3D12RootSignature* root,
                                                                std::span<const unsigned char> bytecode) noexcept -> Result<Com<ID3D12PipelineState>, Error> {
            D3D12_COMPUTE_PIPELINE_STATE_DESC desc{};
            desc.pRootSignature = root;
            desc.CS = D3D12_SHADER_BYTECODE{ bytecode.data(), bytecode.size() };
            Com<ID3D12PipelineState> pso;
            return Check(gpu.device->CreateComputePipelineState(&desc, IID_PPV_ARGS(&pso)), ApiCall::CreateComputePipelineState).transform([&pso] { return pso; });
        };
        return CreateCompute(gpu, root, kConvertCS).and_then([&](const Com<ID3D12PipelineState>& convert) {
            return CreateCompute(gpu, root, kDownsampleCS).and_then([&](const Com<ID3D12PipelineState>& downsample) {
                return CreateCompute(gpu, root, kMatchCS).and_then([&](const Com<ID3D12PipelineState>& match) {
                    return CreateCompute(gpu, root, kFinalizeCS).and_then([&](const Com<ID3D12PipelineState>& finalize) {
                        return CreateCompute(gpu, root, kFlowToMvCS).transform([&](const Com<ID3D12PipelineState>& flowToMv) { return ComputePsos{ convert, downsample, match, finalize, flowToMv }; });
                    });
                });
            });
        });
    };
    return CreateRoots(gpu).and_then([&](const Roots& roots) {
        return CreateComputePsos(gpu, roots.compute.Get()).and_then([&](const ComputePsos& c) {
            return CreateBlit(gpu, roots.blit.Get(), swapChainFormat).transform([&](const Com<ID3D12PipelineState>& blit) {
                return Pipelines{ roots.compute, roots.blit, c.convert, c.downsample, c.match, c.finalize, c.flowToMv, blit };
            });
        });
    });
}

ID3D12PipelineState* PsoFor(const Pipelines& p, interior::PassId pass) noexcept
{
    switch (pass)
    {
    case interior::PassId::Convert: return p.convert.Get();
    case interior::PassId::Downsample: return p.downsample.Get();
    case interior::PassId::Match: return p.match.Get();
    case interior::PassId::Finalize: return p.finalize.Get();
    case interior::PassId::FlowToMv: return p.flowToMv.Get();
    }
    return nullptr;
}

} // namespace real
