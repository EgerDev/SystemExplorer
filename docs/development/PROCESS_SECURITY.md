# Read-only process security inspection

`WindowsProcessSecurityProvider` implements the Core process-security contract without process control, elevation, drivers, privilege enabling, or registry/service changes. Collection runs off the UI thread through `Task.Run`. These are observations, not a security verdict or a guarantee that the process cannot change during collection.

## Identity and lifetime

The caller supplies PID and creation FILETIME. Unknown creation time returns `IdentityMismatch` without opening anything: PID alone cannot attribute observations to a lifetime. The limited-query handle includes `SYNCHRONIZE`; `WaitForSingleObject(0)` must return 258 (alive), and `GetProcessTimes` must match creation time before and after collection. An exited lifetime returns `Exited`; changed creation time returns `IdentityMismatch`. Either discards **every section**, including command text, paths, token values and lists. Unverifiable lifetime validation also discards observations.

## Sections and access

| Section | API and access |
| --- | --- |
| Command line | `NtQueryInformationProcess`, information class 60, on `PROCESS_QUERY_LIMITED_INFORMATION` (`0x1000`). No PEB or WMI fallback. |
| Token | `OpenProcessToken(TOKEN_QUERY)` (`0x8`), `GetTokenInformation`: user SID/account, integrity, elevation/type, virtualization, AppContainer, session, privileges, group count only. SID conversion uses `ConvertSidToStringSidW`; account and privilege names use `LookupAccountSidW` and `LookupPrivilegeNameW` with null system name. `GetPackageFamilyName` observes package identity on the process handle. |
| Protection | `GetProcessInformation(ProcessProtectionLevelInfo=7)` on the limited-query handle. Raw level is retained, including unknown values. |
| Mitigations | Separate `OpenProcess(PROCESS_QUERY_INFORMATION)` (`0x400`), then independent `GetProcessMitigationPolicy` calls. No privilege enabling. Named flags and raw DWORDs are retained; DEP also reports its separate Permanent BOOLEAN. Enum and bitfields follow Windows SDK `winnt.h`, `PROCESS_MITIGATION_POLICY`. |
| Job | `IsProcessInJob` with null job handle. No jobs are opened. |
| Runtime | Existing `IProcessInspectionProvider` module inventory (Toolhelp), looking only for coreclr, clr, mscorwks, hostfxr and hostpolicy DLL names. File/product versions come from `FileVersionInfo`. Source is "detected from loaded modules". No assembly lists. |
| Image | Bounded `QueryFullProcessImageNameW`, followed by `ImageSignatureVerifier`: file size/last-write time, version metadata and local signature provenance. |

The main handle requests `0x1000 | SYNCHRONIZE` (`0x100000`). Handles and native allocations are released in `finally`/`using`, including error and cancellation paths. Nothing terminates, suspends or controls a process, reads/writes process memory, duplicates handles, enables privileges, loads drivers, starts ETW, or modifies services/registry.

## Status semantics

Each section has its own status and reason. A denied token does not suppress independent image/job observations. `Available` means the documented observation completed, not that the process is secure. `Unsupported` is not an observed false value. Unsupported mitigation policies do not make an otherwise complete mitigation section partial. Initial process-open error 87 may mean the PID is gone; error 87 on an already-open-handle operation is not proof of exit (it is failed or explicitly unsupported).

Command-line `STATUS_NOT_FOUND` means `Unsupported`: the kernel reports no command line for this process (e.g. System/minimal processes). A successful empty Unicode string is `Available` with empty text and zero length. Access denied is reported as such. Truncation never publishes a cut command string or guesses its character count from an unfilled buffer.

Runtime inventory failure with no matching runtime rows propagates the inventory status. An incomplete inventory with retained runtime rows is `Partial`; absence is not established. Complete empty inventory reports "no .NET runtime modules loaded". Unreadable module version metadata remains null and makes the section partial.

Overall status is `Available` only when all sections are available, otherwise `Partial`, except for lifetime/open failures. Cancellation throws `OperationCanceledException`; pre-cancellation makes no native calls. Unreached sections are `TimedOut` after budget exhaustion. No inaccessible fields are guessed.

## Bounds

- Command-line buffers start at 4 KiB, cap at **32 KiB**, and use at most **three** query attempts. Unicode pointers/lengths must fit the returned allocation; malformed strings are rejected.
- Token size-query/retry buffers cap at **64 KiB**, with at most **three** retrieval attempts per field. Privileges cap at **128** entries; excess entries produce `Truncated` privilege status. Account/privilege names are bounded to 1024 characters. Groups expose only their count.
- Image paths cap at **32768 characters**. Runtime module processing caps at **4096** entries (the existing module provider has its own bounds).
- Provider budget: **10 seconds**. Image-verifier budget: **5 seconds**. Both are cooperative: blocking native calls/file operations cannot be forcibly interrupted, but cancellation/budget checks occur between operations.
- Catalog hashing input caps at **512 MiB** and the hash buffer at 128 bytes. Catalog discovery examines one matching local catalog; no unbounded catalog enumeration.
- The verifier has a thread-safe **256-entry LRU**, keyed by normalized, case-insensitive full path, file size and last-write UTC ticks. A hit returns the cached immutable provenance record, including its original verification timestamp. This key is not a cryptographic file identity and cannot detect changes preserving size and timestamp.

## Command-line secrets

Command text can contain passwords or tokens. It is returned only in `CommandLineSection.Text`: never log, persist, cache, format or include it in exceptions/details. Native query buffers and temporary managed byte buffers are zeroed before release. `CommandLineSection.ToString()` redacts text, including when nested in `ProcessSecurityInspection.ToString()`. The returned managed string cannot be reliably erased; callers must limit its lifetime and avoid serialization. Lifetime invalidation drops it along with all other collected data. Smoke tests print statuses and lengths only, never text.

## Signature semantics

**Revocation not checked online; trusted means local chain validation at capture time.** More precisely, cached results describe their original `VerifiedAt`, not the later cache-hit time. Trust is not a malware verdict, and offline validation cannot establish current revocation status.

`WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)` tries embedded signatures first. The file and catalog checks use `WTD_UI_NONE`, `WTD_REVOKE_NONE`, `WTD_CACHE_ONLY_URL_RETRIEVAL` and `WTD_REVOCATION_CHECK_NONE`; there is no online retrieval, revocation or symbol-server lookup. State action VERIFY is always paired with CLOSE. Signer display name is read from the trust state's signer certificate before CLOSE, and is null when unavailable.

Embedded success is `EmbeddedTrusted`. No-signature/unknown-subject/unknown-provider results trigger SHA256 local catalog hashing/discovery through `CryptCATAdminAcquireContext2`, `CryptCATAdminCalcHashFromFileHandle2`, `CryptCATAdminEnumCatalogFromHash` and `CryptCATCatalogInfoFromContext`. Catalog trust uses the uppercase hex hash member tag and open file handle; success is `CatalogTrusted`, with catalog path. No catalog means `Unsigned`. Other failing trust HRESULTs are `NotTrusted` with raw HRESULT. Unreadable files are `Unreadable`; verification not attempted is `NotChecked`. Operational failures or budget/input limits do not imply successful trust. Catalog and admin contexts are released even on failures.

## Verification and exclusions

Run `dotnet run --project tests/SystemExplorer.ProcessSecurity.Smoke -c Release` on Windows. Deterministic fixtures/fakes test parsing, error mapping, discard/redaction, independent sections, signature outcomes and LRU behavior. Live checks observe the current process, PID 4, fabricated identity, Windows-signed files and an unsigned fixture. They make no changes to target processes and never print command text.

Assembly lists, memory contents, PEB/WMI fallback, opening jobs, privilege enabling (including debug privileges), elevation, drivers and network-based trust enrichment are out of scope. Elevated Windows Server evidence is **not standard-user acceptance**; denied/unsupported states on other Windows versions or security contexts are valid observations, not permission to bypass safeguards.
