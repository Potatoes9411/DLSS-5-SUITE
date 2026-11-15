// suite_fg.inl - DLSS 5 SUITE: "SUITE FG", the Screen Engine's own frame generation as a generator of Motion
// Forge's (generator 3).
//
// The Screen Engine (Engine/, 5da34df) shows N pictures for each one it makes: the ones between are drawn by its
// blit from the processed picture before (the history) and the current one, each fetched from where the content at
// that pixel sits in it along the motion that joins them (Engine/shaders/Blit.hlsl, Interpolated) - the current
// picture a (1 - t) share of the motion back, the one before a t share of it forward, then blended at t. Here the
// same drawing makes each frame in between of a pair, from the two real frames the shared presenter keeps
// (lf_dlssg.inl, FgPresent) and the engine's motion field of the pair (NVIDIA Optical Flow or the GPU estimate -
// what the Screen Engine calls nvof and builtin), into the pictures MSM FG's frames go into; the presenter shows
// them at their moments as it shows SMFG's. Its motion field is current -> previous, in the field's own texels, as
// the Screen Engine's is. Nothing else is taken from the Screen Engine: its plan / executor layers are its own.

static const char kSuiteFgHlsl[] = R"hlsl(
// Blit.hlsl's Interpolated, as a compute pass over one frame in between.
Texture2D<float4> gHistory   : register(t0); // the real frame before
Texture2D<float4> gProcessed : register(t1); // the real frame after
Texture2D<float2> gMotion    : register(t2); // per pixel, current -> previous, in the motion texture's pixels
RWTexture2D<float4> gOut     : register(u0);
SamplerState gLinearClamp    : register(s0);
cbuffer Constants : register(b0) { uint gOutW; uint gOutH; float gBlend; uint gUnused; };

[numthreads(8, 8, 1)]
void SuiteFgMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gOutW || id.y >= gOutH) return;
    float2 uv = (float2(id.xy) + 0.5) / float2(gOutW, gOutH);
    float width, height;
    gMotion.GetDimensions(width, height);
    float2 mv = gMotion.SampleLevel(gLinearClamp, uv, 0).xy / float2(width, height);
    float3 current = gProcessed.SampleLevel(gLinearClamp, uv - mv * (1.0 - gBlend), 0).rgb;
    float3 previous = gHistory.SampleLevel(gLinearClamp, uv + mv * gBlend, 0).rgb;
    gOut[id.xy] = float4(lerp(previous, current, gBlend), 1.0);
}
)hlsl";

namespace suitefg
{
    // Descriptor sets of four (three pictures read, one written), used in turn: a pair has five frames at most and
    // the presenter holds a few pairs, so a set is never rewritten while a list that reads it is on the card.
    static constexpr UINT kSets = 64;
    static struct State
    {
        winrt::com_ptr<ID3D12RootSignature> root;
        winrt::com_ptr<ID3D12PipelineState> pso;
        winrt::com_ptr<ID3D12DescriptorHeap> heap;
        UINT stride = 0, next = 0;
        bool failed = false;
    } g;

    static bool Ready()
    {
        if (g.pso) return true;
        if (g.failed) return false;
        g.failed = true;
        winrt::com_ptr<ID3DBlob> code, errors, signature;
        if (FAILED(CachedCompile(kSuiteFgHlsl, sizeof(kSuiteFgHlsl) - 1, "suite-fg.hlsl", nullptr, nullptr, "SuiteFgMain", "cs_5_0",
                                 D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, code.put(), errors.put())))
        {
            Trace("[suitefg] its pass did not compile: %s", errors ? static_cast<const char *>(errors->GetBufferPointer()) : "(no log)");
            return false;
        }
        D3D12_DESCRIPTOR_RANGE ranges[2] = { { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 3, 0, 0, 0 }, { D3D12_DESCRIPTOR_RANGE_TYPE_UAV, 1, 0, 0, 3 } };
        D3D12_ROOT_PARAMETER params[2] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[0].Constants.Num32BitValues = 4;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[1].DescriptorTable = { 2, ranges };
        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.MaxAnisotropy = 1;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        const D3D12_ROOT_SIGNATURE_DESC desc = { 2, params, 1, &sampler, D3D12_ROOT_SIGNATURE_FLAG_NONE };
        if (FAILED(D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, signature.put(), errors.put())) ||
            FAILED(lfCore.device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(g.root.put()))))
        { Trace("[suitefg] its root signature was refused"); return false; }
        D3D12_COMPUTE_PIPELINE_STATE_DESC pso = {};
        pso.pRootSignature = g.root.get();
        pso.CS = { code->GetBufferPointer(), code->GetBufferSize() };
        if (FAILED(lfCore.device->CreateComputePipelineState(&pso, IID_PPV_ARGS(g.pso.put()))))
        { Trace("[suitefg] its pipeline was refused"); g.root = nullptr; return false; }
        D3D12_DESCRIPTOR_HEAP_DESC heap = {};
        heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heap.NumDescriptors = kSets * 4;
        heap.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        if (FAILED(lfCore.device->CreateDescriptorHeap(&heap, IID_PPV_ARGS(g.heap.put()))))
        { Trace("[suitefg] no descriptor heap"); g.pso = nullptr; g.root = nullptr; return false; }
        g.stride = lfCore.device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        g.failed = false;
        Trace("[suitefg] SUITE FG ready: the Screen Engine's interpolation along the engine's motion field");
        return true;
    }

    static void Close()
    {
        g.pso = nullptr; g.root = nullptr; g.heap = nullptr;
        g.next = 0; g.failed = false;
    }

    // The frames in between of one pair, each at its moment (0 the frame before, 1 the frame after): `before` and
    // `after` in before_state (the presenter's COPY_SOURCE), `motion` in NON_PIXEL_SHADER_RESOURCE, each of `outputs`
    // in out_state; all are left in the states they came in.
    static bool Frames(ID3D12GraphicsCommandList *list, ID3D12Resource *before, ID3D12Resource *after, D3D12_RESOURCE_STATES before_state,
                       ID3D12Resource *motion, ID3D12Resource *const *outputs, D3D12_RESOURCE_STATES out_state, const float *moments, unsigned count)
    {
        if (!Ready() || before == nullptr || after == nullptr || motion == nullptr || count == 0) return false;
        const D3D12_RESOURCE_STATES read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        if (before_state != read)
        {
            D3D12_RESOURCE_BARRIER in[] = { Barrier(before, before_state, read), Barrier(after, before_state, read) };
            list->ResourceBarrier(2, in);
        }
        ID3D12DescriptorHeap *heaps[] = { g.heap.get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(g.root.get());
        list->SetPipelineState(g.pso.get());
        ID3D12Resource *const reads[3] = { before, after, motion };
        for (unsigned i = 0; i < count; ++i)
        {
            ID3D12Resource *const out = outputs[i];
            if (out == nullptr) continue;
            const UINT set = g.next;
            g.next = (g.next + 1) % kSets;
            D3D12_CPU_DESCRIPTOR_HANDLE cpu = g.heap->GetCPUDescriptorHandleForHeapStart();
            D3D12_GPU_DESCRIPTOR_HANDLE gpu = g.heap->GetGPUDescriptorHandleForHeapStart();
            cpu.ptr += SIZE_T(set) * 4 * g.stride;
            gpu.ptr += UINT64(set) * 4 * g.stride;
            for (ID3D12Resource *r : reads)
            {
                D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
                srv.Format = r->GetDesc().Format;
                srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                srv.Texture2D.MipLevels = 1;
                lfCore.device->CreateShaderResourceView(r, &srv, cpu);
                cpu.ptr += g.stride;
            }
            const D3D12_RESOURCE_DESC od = out->GetDesc();
            D3D12_UNORDERED_ACCESS_VIEW_DESC uav = {};
            uav.Format = od.Format;
            uav.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            lfCore.device->CreateUnorderedAccessView(out, nullptr, &uav, cpu);
            auto to_write = Barrier(out, out_state, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            list->ResourceBarrier(1, &to_write);
            struct { UINT w, h; float t; UINT unused; } constants = { static_cast<UINT>(od.Width), od.Height, std::clamp(moments[i], 0.0f, 1.0f), 0 };
            list->SetComputeRoot32BitConstants(0, 4, &constants, 0);
            list->SetComputeRootDescriptorTable(1, gpu);
            list->Dispatch((constants.w + 7) / 8, (constants.h + 7) / 8, 1);
            auto written = Barrier(out, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, out_state);
            list->ResourceBarrier(1, &written);
        }
        if (before_state != read)
        {
            D3D12_RESOURCE_BARRIER out[] = { Barrier(before, read, before_state), Barrier(after, read, before_state) };
            list->ResourceBarrier(2, out);
        }
        return true;
    }
}
