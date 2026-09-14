# Rules for AI-Written Code

## Premise

This code is written and read by AI coding tools. No human reads it for understanding. Rules that exist for human readability are absent. Rules that prevent bugs, or failing that make failures explicit and locatable, are present even when they require more code. An objection that a rule would be excessive for a human programmer does not apply.

AI-written code fails in a consistent direction: it adds instead of edits, wraps instead of calls, hedges instead of fails, pads with ceremony, narrates what it wrote, and leaves options at their defaults. These rules make each of those expensive.

These rules constrain implementation. They do not implicitly authorize building enforcement infrastructure. The routine gate is limited to ordinary compilation, linking, and selected built-in compiler diagnostics. No feature task builds a linter, custom analyzer, test framework, simulator, tracing engine, replay engine, or verification platform merely to satisfy this document.

Narrow adapters, domain types, and production checks needed to implement the requested behavior are ordinary product code, not prohibited infrastructure.

The *Check* line under each rule identifies available compiler enforcement or identifies the rule as a source/design constraint. A source/design constraint remains an implementation obligation; it does not require a custom checker, a separate review pass for every rule, or a generated compliance report.

Test authoring, test execution, noncompiler analysis, and advanced verification are separate task scope. They are not implied by the routine gate. Optional activities are defined in [VERIFICATION_PROFILES.md](VERIFICATION_PROFILES.md); linking that document does not activate its profiles.

**Deviations are permitted but never silent.** A deviation carries an inline waiver naming the rule and the reason: `// WAIVER(R15): reason`, using the language's comment syntax. Supporting evidence may be cited when necessary. Waivers are machine-readable, but no waiver parser or inventory generator is required. A waiver does not turn an unperformed check into a passed check or an unsupported guarantee into an established fact.

---

## Stances

**S1. Make illegal states unrepresentable.** A compile-time guarantee beats a runtime check, which beats a test, which beats a comment when they establish the same relevant property. Use the strongest applicable mechanism available in the chosen language and existing facilities. This does not authorize building verification infrastructure or launching additional verification campaigns.

**S2. Checkability is compulsory.** Every obligation identifies its enforcement mechanism and that mechanism's limits. Prefer constructs with stronger machine-checked guarantees about required behavior, not merely more lintable syntax. Passing a check establishes only the property that check actually verifies. A result type the compiler forces you to handle beats an unchecked error path; neither establishes that the implemented business rule is correct.

**S3. Fail loud and early.** There are two ordinary outcomes: succeed, or stop the affected operation visibly. An uncertain external outcome is represented explicitly, never guessed into success or failure. A quietly wrong answer is worse than an explicit failure. A process crash is recoverable only when an implemented recovery protocol makes it so.

**S4. Logic is a liability; structure is not a defect.** Duplicated authoritative rules, speculative branches, unused parameters, and unreachable code are defects. Decomposition into named functions, domain types, and contracts is not a defect merely because it adds code. Do not confuse useful structure with permission to add frameworks, redundant checks, or repeated whole-repository processing.

**S5. The gate enforces the configured compiler checks.** Nothing is declared compiler-verified until the gate in Section IX passes. Source and architectural obligations remain rules even when the compiler cannot enforce them. Missing automation is not permission to build it, and passing compilation is not a claim of complete correctness.

---

## I. Structure

**R1. One operation per function.** A function performs one operation: one comparison, one call, one construction, one arithmetic step, or one composition of named calls. Its name identifies that operation. If the name lists independent operations joined by "and", split the function; an indivisible domain or platform operation is not split merely because its conventional name contains that word.

Every condition that changes behavior is its own predicate function returning a boolean; branch on the name, never on an inline expression. Composition functions contain only calls to named functions, bindings of their results, nested function definitions (R34), and a return. The floor is "has a name in the domain": `isOverDailyLimit` is a function, `greaterThan` is not.

The source limits are cyclomatic complexity ≤ 2, ≤ 5 statements, no conditional inside a conditional, and no inline compound branch conditions. Statements are counted, never lines: blank lines, bracket-only lines, and wrapped argument lists do not count. A nested function's body counts toward its own budget, not its parent's.
*Check:* Source constraint. Do not build a complexity analyzer or statement counter.

**R2. Single assignment, no mutation.** Every binding is assigned once and never changed. Project-owned values are not mutated after construction. No reassignment and no loop statements. Iteration uses map, filter, and fold over finite, bounded collections. This forces carried state into parameters and return values rather than mutation of an enclosing scope.

A loop statement is permitted only at the effect boundary, in one of these forms, each with an explicit iteration cap and mutating only a local it owns: draining a foreign iterator, cursor, or stream that offers no collection form; a bounded retry (R20); and the program's top-level event or dispatch loop. Each carries a waiver naming its cap.

Types are deeply immutable where the language can enforce this. Otherwise use immutable representations, encapsulation, and existing runtime freezing facilities where appropriate; do not invent a general freezing framework. Foreign mutable APIs remain behind the effect boundary.

A necessary owned-buffer or local-mutation exception requires a waiver identifying its ownership, bounds, and externally preserved invariants. It does not permit shared mutable state.
*Check:* Native immutability and assignment checks where available, plus the project linter's loop-statement and reassignment rules where configured. Remaining restrictions are source constraints, not a requirement for a custom purity checker.

**R3. Call targets are explicit.** Prefer statically identifiable call targets. No reflection, runtime code loading, or monkey-patching unless the task explicitly requires it and the affected boundary is identified.

Dispatch through declared interfaces and registered callbacks is permitted. The possible targets must be bounded or identifiable at the relevant project boundary; ordinary type-safe dispatch does not require a single compile-time callee.

No recursion in interior code, direct or mutual. Tree- and graph-shaped data is traversed by a fold over an explicit frontier accumulator with a stated maximum iteration count. Where a language construct is recursive by nature and no fold form exists, the exception carries a waiver naming the type traversed, the depth cap, and the production contract (R16) that enforces it. An acyclic call graph alone is not a practical execution-time or memory bound. Library internals are governed by their contracts rather than a requirement to reconstruct their entire call graph.
*Check:* Compiler resolution and type checks where available, plus the project linter's recursion diagnostic where one exists, which covers direct and mutual recursion within a translation unit. Target identification and termination arguments are design constraints. Do not build a whole-program call-graph extractor.

**R4. Names are specifications, and there is one implementation.** A function's name must match what it does; if a behavior change makes the name inaccurate, rename it and repoint every caller. A behavior change that leaves the name accurate keeps the name. Either way the change happens in place: never add `processV2`, `handleNew`, or a parallel implementation beside the original, and never leave the original behind after a replacement. Deliberate versioning is permitted only where an external contract requires a compatibility window — a published API, wire protocol, or persisted format — and the retained version names its end condition.
*Check:* Source constraint. The project linter may flag version-suffixed name pairs and near-clone functions without callers; name-to-behavior agreement is not machine-checkable.

**R5. No pass-through functions.** A function whose body forwards its arguments unchanged to one other function adds a name and nothing else. Delete it. A function that binds a constant, narrows a type, enforces a boundary, or names a domain concept is not a pass-through.
*Check:* Source constraint. No custom AST checker is required.

**R6. No unreferenced code.** Every shipped function has a concrete caller or belongs to an explicit entry surface: program entry points, exported library APIs, registered callbacks, framework entry points, or required protocol implementations. Do not preserve an obsolete implementation merely because something might call it later.
*Check:* Built-in unused-code and dead-code diagnostics where available. The compiler may not establish whole-program reachability; no custom reachability analyzer is required.

**R7. No duplicated authoritative logic.** If two places depend on the same rule, one implementation owns that rule and both use it. Similar syntax alone does not establish shared responsibility: independent policies may currently have identical formulas.

Do not merge independent policies merely to eliminate a clone-detector finding. Shared implementation follows shared semantics, not visual similarity.
*Check:* Source constraint supported by ordinary repository search. Clone detection, when separately requested, supplies evidence for a decision rather than proof of a defect.

---

## II. Types

**R8. No primitive types in interior signatures.** Untrusted input is converted to a validated domain type at the system edge. Interior signatures do not expose raw strings, integers, bytes, maps, or collections of primitives where a domain meaning exists.

Intrinsic properties of immutable values are validated once. Constructors are confined to the owning module and include parsers and invariant-preserving checked transformations; constructing a valid derived value does not require reparsing it.

Validation does not freeze external reality. Authorization, leases, resource existence, pathname identity, and other changing conditions are re-established at the operation that depends on them, preferably through capabilities or transactional mechanisms.
*Check:* Nominal types, private constructors, and signature type checking where supported. Primitive-signature coverage and externally changing conditions remain source/design obligations.

**R9. Distinct meanings get distinct types.** Frames are not seconds. IDs are not strings. Paths are not strings. Parameters with different meanings must not be interchangeable merely because their representations match.

Where the language has call-site argument labels, calls with more than one argument use them. Where labels are unavailable and argument order carries a material transposition risk, use distinct domain types or an explicitly named input structure.
*Check:* Compiler nominal-type and argument checks. No custom signature auditor is required.

**R10. Effects are explicit.** A function's type declares its effects where the language supports this: clock, randomness, filesystem, network, database, process, or application logging. Performing an undeclared effect must fail compilation when native effect checking is available.

Otherwise, keep pure domain computation separate from effectful code and provide effect capabilities through explicit parameters or declared interfaces. A module convention is not described as equivalent to a complete effect system.

Build-provided diagnostic instrumentation is infrastructure, not permission for nominally pure domain code to perform undeclared application I/O.
*Check:* Native effect checking where available. Otherwise a source-level boundary requirement. Do not build an effect checker or import linter.

**R11. Dependencies arrive through a declared channel.** Configuration, services, and shared inputs are provided at the program edge. Functions receive the dependencies they need through ordinary parameters, narrow immutable environment values, or explicit lexical captures. No globals, singletons, or hidden ambient state.

Use the selected language's ordinary mechanisms. Do not build a dependency-injection framework merely to eliminate parameter passing. Exact per-function environment-subset checking is required only when the chosen mechanism actually supports it.

Environment bindings and project-owned data are immutable. An immutable handle to an external resource does not imply that the external resource itself cannot change.
*Check:* Compiler type, ownership, and capture checks where available. Dependency scope is otherwise a source constraint.

**R12. Exhaustive matching.** Every match over a closed set handles every variant. No wildcard arms on internal types, so adding a variant breaks the build wherever a new decision is required.
*Check:* Compiler exhaustiveness checks and applicable warnings. The wildcard prohibition remains a source constraint where the compiler does not enforce it.

**R13. Checked arithmetic.** Silent integer wrapping is forbidden unless modular arithmetic is an explicit part of the operation's contract. Arithmetic uses checked operations that return a result, or bounded types whose ranges establish safety.

A conversion warning is resolved by changing types or logic, never by an unchecked cast or warning suppression. An integer conversion in interior code goes through a named checked conversion or a compiler-established safe conversion.
*Check:* Supported compiler overflow and conversion diagnostics, plus checked arithmetic facilities. Compiler options are not assumed to detect every possible runtime overflow.

---

## III. Failure

**R14. Errors are values.** Expected failures in interior code return a result type whose failure variants are enumerated. Every caller handles the relevant variants or propagates them explicitly. Exceptions are not ordinary interior control flow. Foreign exceptions are translated at the effect boundary when the external API requires it.

Use monadic chaining (`and_then`, `transform`, `transform_error`, or equivalent) or compiler-checked result propagation. These fixed short-circuits do not count as authored branches toward R1's complexity. Every supplied function remains subject to R1 and R34.

Invariant termination under R16 and unavoidable runtime failures are not misrepresented as ordinary result propagation.
*Check:* Compiler result types, exhaustiveness, and must-use facilities where available. Exception-boundary restrictions are source constraints.

**R15. Two outcomes, no silent fallbacks.** Error handling chooses between succeeding and stopping visibly. It does not silently substitute a default value, guess, cached copy, or alternate algorithm for a failed operation.

Retrying the same operation is not a fallback when R20's bounds and retry-safety requirements are met. Reconciling the status of that same operation is not an alternate implementation of it.

A designed recovery or degraded mode must be an explicit requirement with defined activation conditions and a visible state change. It is not an improvised error-branch substitute. Its independent verification belongs to the selected verification task, not an implied harness-building obligation.
*Check:* Source/design constraint. Distinct result and state types should expose recovery, degradation, and uncertain outcomes to compiler checking where practical.

**R16. Invariants are contracts, and required contracts run in production.** Use types and invariant-preserving construction first. Cheap local invariants not enforced by types are checked by named precondition or postcondition predicates in the shipping build. A debug-only assertion does not satisfy a required production contract.

Do not turn every invariant into a repeated whole-state scan. Prefer local or incremental checks. Expensive auditing is a separately selected activity unless the product requirement specifically needs that check in production; in that case its cost must be addressed explicitly, not silently omitted.

An assumption that cannot be established by an executable predicate is recorded with its basis and limitation, not represented by a fabricated assertion.

A violated internal contract means the affected execution cannot be trusted. Stop according to the defined failure policy. Do not automatically restart unless the recovery protocol makes restart safe.
*Check:* Existing compiler or runtime contract facilities, or ordinary explicit production checks. No contract framework, assertion generator, or invariant-testing harness is required.

**R17. Every result is handled.** No ignored meaningful return value, no discarded result, no empty handler. Deliberately discarding a value requires the language's explicit-discard marker and a waiver.
*Check:* Native must-use and unused-result diagnostics at error level where supported. Remaining discard restrictions are source constraints.

**R18. Writers establish the required evidence of success.** A writer reports success only after obtaining the evidence required by its operation's contract. Depending on the operation, that may be a transaction commit, durable acknowledgment, receipt, integrity check, or appropriate readback.

Readback is not universally available or sufficient. A failed verification does not establish that the write failed. Distinguish rejected, committed, committed-but-unverified, and unknown outcomes where relevant; do not blindly retry an irreversible effect.

Full-content rereading is required only when the operation's requirements or selected artifact-verification profile call for it.
*Check:* Result and state types where supported. Required production evidence is part of the implementation; no generalized writer-verification harness is required.

**R19. Nothing unfinished reports success.** A stub is a typed hole or other unfinished definition that fails compilation where the language supports it. No unfinished implementation returns plausible placeholder data or pretends to have completed its effects.

If the language cannot reject an unfinished definition at compilation, it must fail visibly before that definition performs effects. This is not permission to ship a required feature unfinished.

A mention of future work is not itself an executable stub. Do not build a keyword scanner or treat every occurrence of `TODO`, `FIXME`, or `placeholder` as proof of missing functionality.
*Check:* Native compiler rejection of unfinished definitions where available. Completeness is otherwise a source obligation.

---

## IV. Bounds and external completion

These rules apply to every layer. Pure and immutable computation can still allocate memory, expand data, and consume unbounded practical execution time. Effects add waits and concurrency; they are not the only source of resource growth.

**R20. Bound every operation wait.** Every wait for an I/O operation, subprocess, lock, or channel result has a timeout or propagated deadline. Long-lived subscriptions have an explicit cancellation lifecycle and appropriate health or idle policies; silence is not automatically an error.

Timeouts are not arbitrary. Their basis is a caller requirement, usefulness window, resource policy, applicable documented limit, or relevant measured behavior. Distinguish server processing limits, connection limits, idle limits, and client end-to-end deadlines; they are not interchangeable.

Use the shortest applicable bound for the same operation and time interval. Propagate the remaining deadline through suboperations and retries rather than resetting a full timeout at every step. Use monotonic time for elapsed deadlines.

Every retry has a maximum attempt count, a bounded backoff policy, and a total deadline. Honor applicable rate limits and retry instructions. Retry only when the operation is safely repeatable or a supported idempotency/reconciliation mechanism makes it so.

A timeout does not establish that an external effect did not occur. Cancellation also does not establish this unless the external contract guarantees it.
*Check:* Typed deadlines and built-in API requirements where available. Bound selection and retry safety are implementation obligations; no timeout analyzer is required.

**R21. Bound every resource.** Any collection, queue, cache, buffer, or output that grows with input has a cap and defined overflow behavior: reject, evict, stop expansion, or apply backpressure. Unbounded input is streamed rather than loaded whole.

Account for interior expansion and computation as well as effects: decompression, Cartesian products, repeated copying, and similar transformations require relevant limits. Immutability does not establish these bounds.

Use existing bounded types or straightforward checks. No custom allocator, growth-site analyzer, or inventory generator is required.
*Check:* Compiler-enforced bounded types where available, plus required production limit checks.

**R22. No shared mutable state across threads or tasks.** Share by immutability or message passing. R2 makes this nearly automatic. Where locks are unavoidable in the effect layer, keep their scope minimal and establish an acquisition order if multiple locks are involved. Never hold a lock across an await or blocking call.
*Check:* Native ownership and concurrency checks where available. Sanitizers and concurrency campaigns are separately selected verification, not part of the routine gate.

**R35. Detect completion; do not guess it from elapsed time.** Do not sleep for an estimated duration and then assume an external operation completed.

Use a supported completion notification—callback, event, awaitable, future, or stream—when it provides the required semantics. Investigate relevant documented notification mechanisms before considering polling. The existence of an event API alone is insufficient: it must signal the required completion, support the deployment, and have delivery or recovery semantics adequate for the operation.

Polling requires an inline waiver identifying:
- The relevant documentation or other authoritative information examined.
- The notification mechanisms considered and why they cannot meet the requirements.
- The authoritative state or predicate being polled.
- The deadline, polling interval or backoff, cancellation, and rate-limit policy.

Valid grounds include documented polling-only support, unsuitable or unavailable notification semantics, and documentation that remains unavailable after the request process in R36. Lack of effort to investigate notifications is not grounds for a waiver.

When notification delivery can be lost, an explicitly designed reconciliation strategy may be necessary. Do not improvise one silently after an error.

Even under a waiver, elapsed time is not evidence of completion. If completion cannot be established, return an appropriate timeout or unknown outcome.

Timers used for deadlines, retry backoff, rate limiting, or actual time-based scheduling are permitted. They are not completion guesses.
*Check:* Source/design constraint. No callback-discovery or polling-detection tool is required.

---

## V. Determinism and observability

**R23. Consequential nondeterminism is explicit.** Current time, random values, generated IDs, environment configuration, external observations, and consequential ordering enter through declared boundaries.

Pure computation produces the same output for the same inputs. Entire-run replay additionally requires the relevant external observations, scheduling decisions, and compatible code and dependencies. A numeric seed alone is not claimed to reproduce an arbitrary production execution.
*Check:* Native effect and dependency checks where available. Otherwise a design constraint. No replay engine or deterministic scheduler is required.

**R24. Design explicitly for interruption and recovery.** For operations required to survive interruption, implement the smallest sufficient recovery mechanism. Distinguish incomplete, committed, and uncertain outcomes where relevant. Prefer idempotent operations; use supported idempotency mechanisms for repeatable external requests. Do not blindly repeat an external effect whose outcome is unknown.

Use atomic replacement or transactional mechanisms where appropriate. Add checkpoints, journals, or run manifests only when the operation's recovery requirements need them. Purely transient operations do not acquire persistence machinery merely to satisfy this rule.

State the supported interruption model and its limits. Process termination and power loss are different failures. Atomic rename alone does not establish power-loss durability or a transaction across multiple artifacts.

Keep consequential external effects behind narrow boundaries that permit independent testing. A feature may add the adapter needed for its actual operation; it does not acquire a simulated implementation or generalized shim framework merely because independent testing could later use one. Testability can be retrofitted, though appropriate boundaries reduce that work.

This rule does not require a simulator, replay engine, fault-injection harness, kill-at-instruction system, or generalized checkpoint framework. Randomized failure testing is evidence, not a proof covering every instruction or failure combination.
*Check:* Architectural obligation. Comprehensive crash and fault testing belongs to a separately selected verification profile.

**R25. Preserve traceability without compulsory tracing infrastructure.** Use the project's existing instrumentation consistently. Preserve useful operation and function identities, invocation relationships, and explicit result states. Do not add a handwritten print statement to every function or build a tracing engine during an unrelated task.

When function tracing is selected, use existing compiler/build instrumentation where suitable. Record enough identity and context to distinguish invocations, including nested functions. A function name alone is insufficient. The detailed full-tracing requirements are in the optional verification profiles.

Disabled function-trace instrumentation and its argument evaluation must be compiled out when zero disabled-runtime overhead is required. A runtime log-level check is not claimed to have zero cost.

Ordinary logging and tracing use bounded, safe representations. Legitimate secrets—including secrets nested inside otherwise ordinary values—are never printed or logged in release or ordinary development/test builds.

Secret diagnostics require explicit diagnostic scope and a dedicated build mode selected at project build configuration level. Secret-emitting statements are separate and physically excluded from other build variants through preprocessing or build-time source exclusion, not merely suppressed by a runtime log level. The secret-diagnostic mode must not be compatible with a release build. Its data sources, destinations, and retention are controlled.

Tracing infrastructure is exempt from tracing itself. Application logging remains an explicit effect under R10.

A trace is diagnostic evidence, not an oracle identifying the defective function. Redacted or incomplete traces do not promise self-contained exact replay.
*Check:* Existing compiler/build configuration and source constraints. No tracing engine, logger generator, or trace-completeness checker is required.

**R26. Tests provide independent evidence when verification is requested.** Test authoring and execution are separate task scope, not an implied prerequisite of every code change under this document.

When tests are requested, use the existing test setup. State properties where useful and include independent specification examples, conformance vectors, boundary cases, and regressions. Example-based tests do not require a previous bug. A regression records its reproducer; a seed is required only when a seeded generator produced it.

Do not derive every expected result from the implementation under test. Round-trips and internally consistent properties alone can accept mutually consistent wrong implementations.

Mutation testing, extended fuzzing, coverage campaigns, and broad failure-variant exploration are optional verification profiles. No test framework or generalized test harness is built merely to satisfy this rule.
*Check:* None in the routine gate. Selected verification runs are reported separately.

---

## VI. Economy

**R27. No speculative generality.** No parameter without an actual decision to represent. No configuration option without an identified owner and purpose. No branch for a case outside the requirements. Generality follows concrete contracts and use cases, not imagined future needs.

A single-implementation interface may be justified by a capability boundary, external contract, or required test seam. An approved preset may own decisions centrally. Neither is automatically speculative.

Do not remove required third-party parameters, contract boundaries, or invariant-preserving constructors merely because current callers happen to use one value.
*Check:* Built-in unused-code and unused-parameter diagnostics where available. Semantic necessity is a source/design constraint.

**R28. Comments are the last resort, not forbidden rationale.** A comment merely explaining what code does usually means the name or type should improve. Interior functions do not carry redundant docstrings or restated parameter lists.

Comments and linked records may preserve:
- Waivers.
- Citations and external contract evidence.
- Assumptions or obligations that cannot be typed or asserted.
- Consequential rationale that cannot be recovered from the implementation.
- The reproducer or source of a regression.

Use only the detail needed to preserve that information. Do not force its loss through a two-line limit or comment-ratio cap. A linked record is permitted, not a requirement to create a documentation framework.

No narration, decorative section banners, restated code, or commented-out implementations.
*Check:* Source constraint. No comment counter or documentation linter is required.

**R29. Standard library first, vetted dependency second, hand-rolled last.** Do not reimplement the standard library. Do not add a dependency for trivial functionality already adequately provided by existing facilities. Never hand-roll parsing, escaping, quoting, cryptography, or time handling that an appropriate maintained library provides.

Use the project's supported dependency-locking mechanism. Dependency additions and updates must be deliberate. Independent dependency audits are separate verification scope.
*Check:* Ordinary compiler/build dependency resolution and existing lockfile behavior. No audit service or dependency-review tool is required by the routine gate.

**R30. Search for reuse before writing.** Before writing a function, search the relevant existing code by domain concept, name, and type where supported. Use an available index, language server, or ordinary repository search.

Do not reread the entire repository index before every function. Reuse previously examined information and inspect relevant changes incrementally. Do not build an indexing platform or semantic search tool to satisfy this rule.
*Check:* Source/workflow constraint. No generated index is required by the gate.

**R31. Metaprogramming is controlled infrastructure.** Macros, decorators, and code generation may implement tracing, contracts, effect interfaces, serializers, protocol bindings, parsers, and other justified transformations.

Use approved, reproducible mechanisms with authoritative inputs and pinned versions where applicable. Domain declarations may use those mechanisms; do not introduce an unverified metaprogramming system during an unrelated feature task.

Generated code is checked in unless the project deliberately uses reproducible build-time generation. In either case its inputs and generation method are available, and shipped generated code passes the same compiler gate. Regeneration is not a separate mandatory verification campaign on every commit.

Feature flags represent concrete supported configurations. Their ownership and supported combinations are explicit. Compile and independently test the combinations selected for the task or verification profile; do not imply exhaustive testing of every theoretical combination.
*Check:* Ordinary compilation of selected configurations. No custom generator auditor, flag-inventory tool, or configuration-matrix harness is required.

**R32. A change does what it says.** A diff touches only what the stated task requires. Necessary changes can include affected callers, contracts, representations, and production adapters. They do not include unrelated renames, reformatting, or refactors.

A behavior change does not require renaming a function whose name remains accurate. Compatibility migrations may retain explicitly required versions during their defined transition.

Do not expand a feature task into enforcement-infrastructure development or an unrequested verification campaign. If formatting work is requested, use the existing formatter separately so unrelated formatting does not obscure logic changes.
*Check:* Task-scope constraint. No custom diff gate is required.

**R36. Request inaccessible documentation; do not invent contracts.** If documentation is required and known to exist, but access requires human intervention, ask the user for access or the relevant material. If an API or function is known but its needed documentation cannot be accessed or searched, and there is reason to believe the user can obtain it, ask for it.

Accessible version-matched documentation, source, headers, and types may establish relevant facts. Controlled observations establish observed behavior, not an undocumented universal guarantee. Never present a guessed external contract as established.

Complete independent work while collecting requests. Batch related requests when a genuine blocker is reached instead of repeatedly interrupting for each detail. Do not implement dependent behavior on guessed assumptions merely to avoid reporting a blocker.

If the user cannot provide the material, explicitly identify what remains unknown. Any permitted alternative must satisfy the relevant rule's waiver and safety conditions.
*Check:* Source/workflow constraint. No documentation-discovery system is required.

---

## VII. Completeness and layout

**R33. Every option is decided at its owning boundary.** An AI must not leave a decision at its default merely because a default exists. Domain construction must establish the type's invariants. No implicit, unreviewed default construction or member initialization.

Direct construction explicitly supplies each independently selected field. An approved named preset, factory, or invariant-preserving constructor may own shared decisions centrally. Derived fields are established by that constructor rather than redundantly supplied by every caller.

Invariant-preserving immutable record updates are permitted. Unchanged fields need not be manually copied when the language's update mechanism guarantees their preservation.

Functions have no unreviewed default arguments or template arguments. Alternate construction forms must represent deliberate contracts, not a collection of convenience overloads hiding undecided options.

No inherited data members that hide construction obligations. Where the language has initializer lists or equivalent construction syntax, use them rather than piecemeal mutation in constructor bodies.

The raw value of a domain type is accessed only inside its owning module and the effect boundary. An operation on a domain type belongs with that type or its explicitly designated domain module.

Effect interfaces do not supply silent no-op behavior. Implementations explicitly provide required operations and mark overrides where the language supports them. Visitors over project variants name every alternative; no generic catch-all.

Third-party option objects are initialized only at the effect boundary. Use the vendor's supported initialization mechanism, explicitly select relevant project policy, and identify any deliberately adopted vendor-default policy. Do not manually reconstruct opaque or extensible vendor initialization merely to list every internal field.

New project-owned policy fields must force a decision at their owner. Callers using an approved preset do not each repeat that decision.
*Check:* Applicable compiler initialization, override, access-control, and exhaustiveness diagnostics. Completeness beyond those diagnostics is a source obligation; no custom AST matcher suite is required.

**R34. Nested functions are functions.** A function may define named functions inside itself, to any depth, and each one is subject to the same applicable rules: one operation and its own statement budget. Every closure is bound to a name; no anonymous function is passed inline.

Where explicit capture lists exist, name the captures rather than using a default capture. Otherwise use explicit bindings, a limited lexical scope, or helper functions with explicit parameters so dependency scope is deliberate. Do not claim compiler-enforced capture restrictions that the language does not provide.

A nested function's body does not count toward its parent's statement budget. The definition counts as zero statements and a call as one. A nested function stays nested while local ownership is appropriate and moves to shared scope when it genuinely serves another caller.

Depth of definition nesting is not limited; control-flow nesting is bounded by R1. When full tracing is separately selected, nested functions in the selected scope receive the same instrumentation treatment as top-level functions.
*Check:* Native closure and capture diagnostics where available. Remaining restrictions are source constraints; no custom closure analyzer is required.

---

## VIII. Language requirements

Choose a language and existing toolchain adequate for the task. Prefer native support for:

- Nominal or branded types and controlled construction (R8, R9).
- Sum types and exhaustive matching (R12, R14).
- Immutability or ownership guarantees (R2, R11).
- Explicit effects or practical capability boundaries (R10).
- Compile-time rejection of unfinished definitions (R19).
- Checked arithmetic and conversions (R13).
- Must-use results (R17).
- Complete named initialization and invariant-preserving updates (R33).
- Named local functions and deliberate capture control (R34).

Among suitable languages, choose the one the coding tool is most fluent in.

Do not build compiler extensions, custom linters, or a substitute type/effect system to make a language appear eligible. Use ordinary language mechanisms and existing libraries. If a required guarantee cannot be provided, identify the limitation or choose a suitable toolchain; do not describe a convention as compiler enforcement.

---

## IX. The routine gate

Every commit uses the project's ordinary compiler/build invocation for the configurations selected by the task, together with any linter or formatter configuration the project provides.

The gate is limited to:

1. Successful compilation and ordinary linking.
2. Native type, access-control, ownership, effect, and exhaustiveness checks that the selected language actually provides.
3. Applicable built-in compiler diagnostics, configured as errors for project code.
4. Existing compiler configuration needed for required shipping behavior, such as supported arithmetic checks and production-contract settings.
5. A provided linter and formatter, run from their checked-in configuration, at their configured severity.

Use supported, relevant warning options. Do not enable every possible diagnostic indiscriminately or create suppression machinery for irrelevant warnings.

Running a provided tool from its configuration is part of the gate. Building or extending one is not. A feature task does not write a linter, compiler plugin, custom analyzer, matcher suite, formatter, gate aggregator, or verification platform, and does not add or modify lint rules to accommodate the code it just wrote. Where no such tool is provided, the corresponding rules remain source constraints; report the gap rather than filling it.

The routine gate does not require:

- Custom compiler plugins, analyzers, or matcher suites written for a task.
- Lint or format configuration where the project provides none.
- Tests written to demonstrate compliance with this document.
- Mutation testing, fuzzing, coverage campaigns, or sanitizers.
- Simulators, fault injection, replay engines, or tracing systems.
- Clone detection or whole-program call-graph analysis.
- Dependency audits.
- Generated function, waiver, growth-site, or feature-flag inventories.
- A verification script created merely to aggregate these activities.

Tests for the behavior a task implements are ordinary work, written when the task calls for them and run through the project's existing test setup (R26, P1). What this document does not create is a test obligation of its own: no rule here is satisfied by writing a test that demonstrates the rule was followed, and no rule here requires building test infrastructure.

Compiler-generated runtime checks may be part of the shipping implementation; that does not add an automatic test-execution stage to the gate.

Do not disable required compiler checks, relax a configured lint rule, or suppress a finding merely to make a change pass. A waiver records a deliberate deviation at its site (see Premise); it is not a way to clear a finding the change should have fixed. Do not expand the task into repairing unrelated pre-existing diagnostics. Report a genuine build blocker.

If compilation or the provided tools cannot be run, report which were not run and why. If they pass, report what passed — not universal correctness, crash-safety proof, complete replayability, or verification that was never performed. Passing the configured rules establishes only the properties those rules check.

Advanced checks are selected independently from VERIFICATION_PROFILES.md. Their absence is not an instruction to build them.
---

## Project amendments

These amend the rules above for this project and take precedence where they conflict.

**A1. Dependency provenance (amends R29).** Use the standard library, including the platform's standard framework libraries — for C#, the built-in .NET libraries, not only the base language. A non-standard library is justified only when the standard library lacks the capability, the platform's own documentation directs you to the package, or the alternative is hand-rolling something R29 forbids.

Where a library is justified, prefer first-party publishers: the vendor of the platform being targeted, and the vendor of any external service being called. A Windows tool may use Microsoft-published libraries by default; a Windows tool calling a Google API may use Microsoft- and Google-published libraries by default.

All other third-party libraries and packages require explicit user approval before use. When proposing one, rank candidates by trust: first, packages published by Microsoft, Google, Intel, Nvidia, or Apple (see https://github.com/ThioJoe/Big-Tech-Verified-Open-Source); then a community project that is the reference implementation of its specification or the package the platform's own documentation points to — *the* solution, not *a* popular one.

Publisher identity comes from the package registry's verified owner, never from the package name. Publisher reputation is not maintenance status: confirm the package is actively maintained and version-matched to the target platform. A first-party package that is archived or deprecated is not preferred over a maintained alternative.

This policy applies to the resolved dependency graph, not the direct dependency alone. A proposal names the transitive packages the addition brings in and their publishers.

If approval is unavailable, stop and report the blocker (R36). Hand-rolling parsing, escaping, quoting, cryptography, or time handling is never the fallback, and neither is vendoring third-party source to avoid the approval.

An approval is recorded where the dependency is declared: the package, the version, who approved it, and when. Dependencies already present in the project are not re-litigated during an unrelated task (R32).
*Check:* Ordinary build dependency resolution and the existing lockfile. Provenance and approval are source/workflow constraints.

**A2. Scope of these rules (amends all rules above).** The rules in this document apply to shipped product code. They do not apply to tests, test fixtures and doubles, benchmarks, or internal tooling, with the exceptions below. Exempt code needs no waivers; the exemption is not a deviation.

Internal tooling means code that does not ship and that nothing the user runs depends on at runtime: build scripts, developer utilities, one-off analysis scripts, and the linter and gate configuration itself. Code that produces a shipped artifact, modifies product source, or reads or writes real user data is product code no matter where it lives. A test helper that product code calls is product code.

These rules apply in exempt code as written:

- A1, dependency provenance. A package added for tests or tooling is still a package in the project.
- R19, nothing unfinished reports success. A test that fabricates a pass, asserts nothing, or skips without reporting the skip is a defect. An unfinished tool fails visibly rather than producing plausible output.
- R26's independence requirement. Expected results are not derived from the implementation under test.
- R36, do not invent contracts. A test does not assert a guessed external behavior as though it were specified.
- R33's prohibition on silent no-op implementations, for any double that implements a product interface. A double that quietly does nothing verifies nothing.

Everything else — one operation per function, single assignment, no loops, no recursion, domain types in signatures, effects in types, comment limits, and the rest — is not required in exempt code. Write it the ordinary way.
