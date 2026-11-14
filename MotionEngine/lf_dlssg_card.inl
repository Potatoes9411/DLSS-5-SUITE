// lf_dlssg_card.inl - which NVIDIA card DLSS MFG runs on (its CUDA compute capability).
//
// DLSS 5 SUITE (Motion Forge): this takes the place of MFG SUPER MOTION's lf_dlssg_compat.inl. That file laid out
// the dlssg_for_sm86 compatibility package (a version.dll proxy and kernels that make NVIDIA's nvngx_dlssg.dll run
// on RTX 20/30) and, with lf_dlssg_ada.h (GPL-3.0), the runtime was patched in memory for x3..x6 on RTX 40. NVIDIA's
// licence does not allow either change to its runtime, and the GPL file cannot go into this program, so both are
// gone: DLSS MFG runs NVIDIA's own file as NVIDIA ships it, which the user supplies - x2..x6 on RTX 50, x2 on RTX 40,
// not at all on older cards (SMFG, SMT and SMAI run on any card). XeSS MFG "on the tensor cores" went through the
// same package, so it is gone too: FgXessTensor() is always false and XeSS MFG uses Intel's own library.
static bool FgXessTensor() { return false; }

// The most frames in between NVIDIA's runtime makes a pair on this card, unpatched: RTX 50 (SM 10.0 and up) its
// multi frame generation, x6; RTX 40 one frame, x2. (Older cards are refused by the runtime itself.)
static unsigned FgNativeMost(int sm) { return sm >= 100 ? 5u : 1u; }

static int FgComputeCapability()
{
    static int cached = -1;
    if (cached >= 0) return cached;
    cached = 0;
    DXGI_ADAPTER_DESC1 desc = {};
    if (!lfCard.adapter || FAILED(lfCard.adapter->GetDesc1(&desc)) || desc.VendorId != 0x10de) return 0;
    const HMODULE cuda = LoadLibraryExW(L"nvcuda.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!cuda) return 0;
    auto init = reinterpret_cast<int(__stdcall *)(unsigned)>(GetProcAddress(cuda, "cuInit"));
    auto count = reinterpret_cast<int(__stdcall *)(int *)>(GetProcAddress(cuda, "cuDeviceGetCount"));
    auto device = reinterpret_cast<int(__stdcall *)(int *, int)>(GetProcAddress(cuda, "cuDeviceGet"));
    auto luid = reinterpret_cast<int(__stdcall *)(char *, unsigned *, int)>(GetProcAddress(cuda, "cuDeviceGetLuid"));
    auto attribute = reinterpret_cast<int(__stdcall *)(int *, int, int)>(GetProcAddress(cuda, "cuDeviceGetAttribute"));
    int n = 0;
    const LUID selected = lfCore.device->GetAdapterLuid();
    if (init && count && device && luid && attribute && init(0) == 0 && count(&n) == 0) {
        for (int i = 0; i < n; ++i) {
            int dev = 0, major = 0, minor = 0; unsigned mask = 0; LUID found = {};
            if (device(&dev, i) == 0 && luid(reinterpret_cast<char *>(&found), &mask, dev) == 0 &&
                memcmp(&found, &selected, sizeof(LUID)) == 0 &&
                attribute(&major, 75, dev) == 0 && attribute(&minor, 76, dev) == 0) {
                cached = major * 10 + minor; break;
            }
        }
    }
    // CUDA may have installed process state; retain its module for the worker lifetime.
    Trace("[dlssg] selected adapter %ls, CUDA SM%d", desc.Description, cached);
    return cached;
}
