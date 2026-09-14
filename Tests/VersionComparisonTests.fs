module VersionComparisonTests

// UpdateChecker.compareVersions decides whether an update is offered at all.
// A version it misreads means users are silently never told about a release,
// or are offered one they already have.

open Xunit
open DLSS_5_MANAGER.Services

let private newer a b = UpdateChecker.compareVersions a b > 0
let private same a b = UpdateChecker.compareVersions a b = 0

[<Fact>]
let ``a later suite number is newer even when the base version is the same`` () =
    // Reading only the leading numbers made every suite build of 1.2.1 equal,
    // so .5 would never offer .6.
    Assert.True(newer "1.2.1-suite.6" "1.2.1-suite.5")
    Assert.False(newer "1.2.1-suite.5" "1.2.1-suite.6")

[<Fact>]
let ``suite numbers compare numerically, not as text`` () =
    Assert.True(newer "1.2.1-suite.10" "1.2.1-suite.9")

[<Fact>]
let ``a higher base version beats any suite number below it`` () =
    Assert.True(newer "1.2.2-suite.1" "1.2.1-suite.6")
    Assert.True(newer "1.3.0-suite.1" "1.2.9-suite.99")

[<Fact>]
let ``a release tag with a leading v matches the running version`` () =
    // GitHub tags are "v1.2.1-suite.5"; the app reports "1.2.1-suite.5".
    Assert.True(same "v1.2.1-suite.5" "1.2.1-suite.5")
    Assert.True(same "V1.2.1-suite.5" "1.2.1-suite.5")

[<Fact>]
let ``a suite build is newer than the plain base version`` () =
    Assert.True(newer "1.2.1-suite.1" "1.2.1")

[<Fact>]
let ``the 0.0.0 fallback for a broken build is older than every release`` () =
    Assert.True(newer "1.2.0" "0.0.0")
