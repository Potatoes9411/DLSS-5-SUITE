#pragma once
#include "effects/real/com.h"
#include "infrastructure/bounded_string.h"
#include "interior/units.h"

namespace real {

// What a file's version resource calls its product, which is the one thing about a file its name does not say.
using ProductName = infra::BoundedString<wchar_t, 64>;

// A file Windows says is signed, held open so it stays the file that was checked: the handle is shared for
// reading only, so nothing may write to it, delete it or rename it while the session holds it.
struct TrustedFile
{
    UniqueHandle handle;
    ProductName product; // what the file calls its product, or nothing when it says: read once the file is held, so it is this file's
};

// Which of NVIDIA's files is being checked, which is what a refusal names: the two models, the driver's
// optical flow library, and the NGX runtime when a copy sits beside the program.
enum class ModelKind : std::uint8_t { NeuralRendering, SuperResolution, OpticalFlow, Runtime };

// Verifies every Authenticode signature the file carries, the first and each one after it, and fails
// unless each is trusted and one of their signers is NVIDIA Corporation by name, with a chain that, built
// again from Microsoft's own trusted root list and the certificates the signature carries, is clean: a root
// anyone put into the ordinary Windows stores does not count. Only then is the product name read, which is
// for the caller to judge. Revocation is not chased, which would mean a network call on a path that has to
// work offline; a root on Microsoft's list that the machine does not hold yet is fetched for that chain,
// which is the one call over the network this check can make, and it is bounded.
[[nodiscard]] infra::Result<TrustedFile, Error> OpenTrusted(const interior::FilePath& path, ModelKind kind) noexcept;

// Asks Windows, for every library the process loads by name from here on, its own and those loaded inside
// the libraries it uses, to take the one in the system folder whenever one of that name is there, so a
// file put beside the program cannot stand in for it. The executable's own imports are resolved before
// main runs; the linker's dependent load flag confines those to the system folder.
[[nodiscard]] infra::Status<Error> PreferSystemLibraries() noexcept;

} // namespace real
