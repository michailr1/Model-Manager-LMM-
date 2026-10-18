# M1 verification — 2026-10-18

- Repository initially contained only AGENTS.md; branch `main`, clean checkout.
- .NET SDK 8.0.426 installed in the user's LocalModelManager/dotnet directory.
- `dotnet build`: successful, zero warnings and errors.
- `dotnet test`: 17 passed, zero failed or skipped.
- WPF application launched and its window visually inspected. File selection,
  discovery controls, results and Logs are visible. Missing LMS and NVIDIA
  dependencies produce visible diagnostics; LMS buttons remain disabled.
- File metadata tested using a small synthetic `.GGUF` fixture, including
  byte count and unchanged source file. This is not a real model acceptance test.
- CLI status fixtures are representative synthetic outputs, not captured
  from an installed LM Studio on this machine.

## Acceptance blockers

This machine reports Intel Arc graphics. `nvidia-smi` is absent from PATH and
System32. `lms.exe` is absent from PATH, the current profile's standard LM Studio
directory, and `C:\Users\admin\.lmstudio\bin\lms.exe`. The example `E:\models`
location is unavailable.

Consequently, real GGUF drag-and-drop, RTX 4090 VRAM, and actual `lms ls` /
`lms server status` invoked through the app are **not verified**. M1 cannot
be declared fully accepted until these checks run on the target machine.
Recommended next action: follow the manual acceptance steps in README on
the machine with the existing GGUF, NVIDIA driver and LM Studio, or provide
their actual locations if they are installed elsewhere here.

No M2 implementation was added.

## Target-machine evidence supplied by user

The user subsequently supplied a successful build, 17 passing tests, a screenshot
of the running app, and runtime logs from the RTX 4090 PC:

- LMS discovered at `C:\Users\admin\.lmstudio\bin\lms.exe`.
- NVIDIA GeForce RTX 4090: 24564 MiB (23.99 GiB).
- Selected real file `E:\models\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf`:
  19535703264 bytes, with path and name visible. The screenshot alone does not
  distinguish drag-and-drop from the file picker or independently verify size.
- `lms ls` returned a model table with exit code 0.
- `lms server status` returned exit code 0 and reported running on port 1234
  through **stderr**, exposing an app bug: stdout-only parsing yielded Unknown.

The app now displays both streams in Results and parses their combined text,
while preserving separate stdout/stderr entries in Logs. Regression tests cover
the user's exact status message on stderr and status messages on either stream.
The user subsequently supplied a screenshot showing `[server] Running` and the
status text in Results, then explicitly confirmed drag-and-drop works. M1 accepted.

## M2 implementation and verification

- Added symbolic-link import by default, explicit Copy with per-operation confirmation,
  required namespace, GGUF signature/source-size validation, CLI capability check,
  dry-run before confirmation, source-preservation check and model-list refresh.
- No fallback to copy or move; no loading, estimation or API lifecycle changes.
- Actual import has no 60-second deadline; cancellation remains available. Diagnostics
  and preflight retain their timeout. Unexpected CLI prompts receive closed stdin.
- `dotnet build`: zero warnings/errors. `dotnet test`: 45 passed.
- WPF M2 window launched and visually inspected; size constrained to the desktop work
  area so the Logs panel remains visible on the development screen.
- Import provider tests use a fake runner and tiny synthetic GGUF headers, testing
  unsupported flags, cancellation, refusal of unapproved copy, and no fallback.

Real import is not verified locally: LM Studio and the target GGUF are on the user's
RTX 4090 PC. Follow README's M2 manual acceptance steps there. In particular, verify
the resulting symbolic link and source file, duplicate handling, and declining Copy.
M2 is implemented but target-machine acceptance remains outstanding.

## M2 acceptance attempt on the RTX 4090 PC — 2026-10-18

This section records checks actually executed in the transferred working folder
`C:\Users\admin\Documents\LMM`. Earlier development-machine evidence above is historical.

- The solution, sources, tests and documentation are present. There is no `.git`
  directory: `git status --short` reports "not a git repository". The branch and
  uncommitted-change baseline cannot be determined. No clone, Git initialization,
  commit or source-code replacement was performed.
- Installed SDK: 10.0.402; Windows Desktop Runtime 8.0.32 x64 is present.
- Initial sandboxed `dotnet build` failed because access to the user NuGet.Config
  was denied. Repeating with approved sandbox escalation succeeded: zero warnings,
  zero errors. `dotnet test` also succeeded: 45 passed, zero failed or skipped.
- The freshly built WPF app was launched. Its discovery reported
  `C:\Users\admin\.lmstudio\bin\lms.exe` and NVIDIA GeForce RTX 4090,
  24564 MiB / 23.99 GiB.
- Selected the real GGUF through the application's file picker:
  `E:\models\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf`, 19535703264 bytes / 18.19 GiB.
- Real `lms import --help` supports `--user-repo`, `--symbolic-link`, `--copy`,
  `--yes`, and `--dry-run`.
- Real Symbolic Link dry-run, both directly and through the app, returned exit 0
  for `local/qwen3.8-27b`. LMS planned this destination:
  `C:\Users\admin\.lmstudio\models\local\qwen3.8-27b\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf`.
  LMS also warned that it could not locate its configuration file and was using
  the default models path. Whether that path matches the GUI model directory
  remains to be checked before a successful retry.
- Clicked **No** in the app's Symbolic Link confirmation. Logs recorded
  `[import] confirmation declined`; the destination was absent afterward.
- Enabled Copy, ran its preflight, and inspected the confirmation showing the
  full 19535703264-byte / 18.19-GiB copy and destination. Clicked **No**.
  Logs recorded a second `confirmation declined`; the destination remained absent.
  No actual Copy operation was started.
- Disabled Copy and clicked **Yes** in a fresh Symbolic Link confirmation.
  The app recorded `confirmed / SymbolicLink / 19535703264 bytes` and displayed
  LMS stderr and exit code 1. Exact failure:

```text
EPERM: operation not permitted, symlink 'E:\models\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf' -> 'C:\Users\admin\.lmstudio\models\local\qwen3.8-27b\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf'
```

- After failure, PowerShell independently confirmed the source still exists with
  19535703264 bytes. `Get-Item` on the planned destination reported it does not
  exist. No SymbolicLink or Target could therefore be verified.
- Real `lms ls` returned exit 0 and three existing models; the requested
  Qwen3.8-27B model was absent. There was no automatic Copy fallback.
- No Windows security settings were changed, no elevated import was attempted,
  and no source model was moved or deleted. Only this verification document changed.

**Outcome: M2 acceptance remains blocked by Windows symlink permissions.**
Successful import exit 0, link type/Target, appearance in `lms ls`, and duplicate
rejection without replacement remain unverified. Repeating the failed import
cannot establish duplicate handling because no destination link exists.

Recommended next step: check LM Studio's configured models directory, then have
the user explicitly authorize or perform a one-time elevated launch of the built
app and repeat only the Symbolic Link acceptance checks. Enabling Developer Mode
is an alternative security-setting change requiring separate consent. Copy remains
an explicit alternative requiring separate approval and is not recommended for
this 19.5-GB file. Do not proceed to M3 until the link and duplicate checks pass.

## M2 window adjustment and administrator launch — 2026-10-18

- User explicitly authorized requesting elevation and attempting an administrator
  launch, and requested a larger or more usable window.
- Changed MainWindow.xaml: initial size 1100 x 960 (previously 960 x 680), centered
  startup, flexible form area and fixed 110/150-DIP Results/Logs rows. The existing
  work-area height limit and form scrolling remain for smaller displays.
- Requested a normal close of the previous app. The first rebuild encountered
  its still-running process locking the apphost. After that process exited,
  repeated `dotnet build` succeeded with zero warnings/errors;
  `dotnet test --no-build` passed all 45 tests.
- Launched the updated executable using PowerShell `Start-Process -Verb RunAs`.
  It returned process 27932; a subsequent process check found the expected
  `Local Model Manager — M2` window with Responding=True.
- Windows UI automation could not initialize: node_repl timed out repeatedly,
  including after a kernel reset and one final initialization retry. This began
  before the administrator launch. No alternative UI automation helper was used.
- Visual verification of the adjusted layout and a new import through the
  administrator window remain outstanding. The earlier EPERM failure is still
  the latest actual import result; a successful link must not be inferred from
  the administrator launch alone.
- Next action: use the open updated app to select the same GGUF, enter
  local/qwen3.8-27b, leave Copy unchecked, review the preview destination and
  confirm Symbolic Link. Then independently check LinkType/Target, unchanged
  source size, lms ls and duplicate rejection. No confirmed Copy was executed.
## Automatic local import category — 2026-10-18

- Selecting or dropping a GGUF now suggests local/<filename-without-extension>.
  The name is lowercased, invalid characters are replaced, edge separators are
  trimmed, Windows reserved names are prefixed and names are capped at 100 chars.
  A filename without usable Latin characters/digits falls back to local/model.
- Empty categories, the incomplete local/ prefix, and the previous automatic
  suggestion are replaced on selection. Other manual values are preserved.
- The field is labelled Category and explains that local is a local category,
  not an inferred publisher. The source file is not renamed.
- Final dotnet build: zero warnings/errors. dotnet test --no-build: 52 passed
  (seven new cases for real filename, whitespace, Unicode, reserved/long names).
- The previous elevated app held build files; normal close requests did not stop
  it. It was terminated via an elevated taskkill before the successful rebuild.
- UI behavior has been inspected in source; interactive acceptance of autofill
  remains pending because Windows UI automation was unavailable in this session.
- Changed: ImportRequest.cs, MainViewModel.cs, MainWindow.xaml, M2Tests.cs,
  README.md and VERIFICATION.md. Real link acceptance remains the next M2 step.
## Successful link, catalog mismatch and CLI configuration repair — 2026-10-18

- User supplied app logs: confirmed SymbolicLink import at 18:39:10 returned
  exit 0, with source-preservation check passing. Auto category was
  local/qwen3.8-27b-abliterated-q5_k_m. The supplied screenshot also confirms
  autofill and the larger form displaying the import controls without scrolling.
- Independently checked the C:\Users\admin\.lmstudio\models\local\qwen3.8-27b-abliterated-q5_k_m\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf
  destination: LinkType=SymbolicLink, Target=E:\models\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
  Source still exists at 19535703264 bytes. PowerShell reports Length=0 for the
  link object itself; that is not the size of the GGUF target.
- Real duplicate dry-run and actual CLI SymbolicLink import both returned exit 1
  with Target file already exists. Link Target and creation timestamp remained
  unchanged; source size remained unchanged. This was a direct CLI duplicate
  check, not a repeated click through the app.
- lms ls --json still returned only the three previous models; the requested
  Qwen3.8-27B model was absent. Thus a successful symlink operation alone did not
  complete M2 acceptance.
- Found C:\Users\admin\.lmstudio\apps\bionic\settings.json downloadsFolder=E:\models.
  CLI import had no C:\Users\admin\.lmstudio\settings.json and explicitly fell
  back to C:\Users\admin\.lmstudio\models. The two paths disagreed.
- Created the previously absent CLI settings.json with only downloadsFolder set
  to E:\models, matching Bionic. Used CreateNew to prevent overwriting any
  existing file; Bionic settings and the source GGUF were not changed.
- Repeated actual CLI dry-run: exit 0, no missing-configuration warning, correct
  planned destination:
  E:\models\local\qwen3.8-27b-abliterated-q5_k_m\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
- No real import into that corrected destination has been performed yet.
  Next: repeat import in the already-open elevated app, confirm that E: path,
  then check LinkType/Target, source size, lms ls and duplicate handling there.
  The earlier C: link remains in place; nothing was removed automatically.
- Project code unchanged in this diagnostic step; tests were not rerun.
  Last code-change verification remains 52 passed. Changed VERIFICATION.md and
  created external C:\Users\admin\.lmstudio\settings.json.
## M2 target-machine acceptance completed — 2026-10-18

This result supersedes the pending outcomes above.

- User supplied app logs of the corrected import: preview exit 0 at 18:44:33,
  confirmed SymbolicLink import exit 0 at 18:44:40, source-preservation check passed.
- Independently verified destination:
  E:\models\local\qwen3.8-27b-abliterated-q5_k_m\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
  LinkType=SymbolicLink; Target=E:\models\Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
  Source exists with unchanged 19535703264-byte size.
- The app's immediate ls at 18:44:41 still listed three models. A subsequent actual
  lms ls --json and plain lms ls both included qwen3.8-27b-abliterated as the fourth
  model, with the expected local namespace and 19535703264 sizeBytes.
  No backend restart or model load was needed; the catalog updated after a delay.
- Repeated actual CLI import into the corrected E: namespace returned exit 1:
  Target file already exists. Afterward LinkType, Target and creation timestamp
  were unchanged, and the source still had its original byte count. This duplicate
  check used the CLI directly rather than UI automation; the app uses the same
  import arguments and will encounter the same refusal during preflight.
- Earlier checks in this document cover rejecting SymbolicLink and Copy through
  the application's confirmation dialogs with no destination written. No actual
  Copy was performed, and there was no automatic fallback or source move/delete.
- Outcome: M2 accepted for this machine and real model. Administrator privileges
  were needed here. The missing CLI settings file was repaired to match Bionic's
  E:\models catalog. The older C: link remains; it was not deleted automatically.
- Only verification/documentation changed in this step. No build/test rerun was
  needed; latest code verification remains build 0 warnings/errors, 52/52 tests.
- Next recommended milestone: M3, LM Studio estimate-only memory advice at the
  Hermes-required context. M3 has not been started.
## M3 implementation and runtime verification — 2026-10-18

- Scope: estimate-only memory advice for a connected local GGUF at the fixed
  Hermes-required 65,536 context. No actual load, API lifecycle, downloads, new
  inference backend, or lower-context fallback was implemented.
- Added MemoryAdvisor: CLI capability check, exact category/filename lookup in
  ls --json, unique local modelKey, maximum-context check, sequential GPU ratios
  1.00 down to 0.00 in steps of 0.05, and strict parsing of returned model/context/
  GPU ratio and GPU/total memory. All estimates include --estimate-only and --yes.
- GPU budget uses freshly reported free VRAM minus max(1024 MiB, ceil(5% of
  detected total VRAM)). Current allocations remain outside that budget. CLI
  two-decimal rounding is handled conservatively with 0.005-GiB allowance.
- Added a separate Memory / Hermes 64K tab, estimate action and visible explanation
  of the chosen ratio, budget/reserve and memory estimates. Existing import UI is
  on its own tab. Changing the file/category or refreshing discovery invalidates
  previous displayed advice. Both CLI streams/exit codes are logged. Errors and
  cancellation leave no displayed recommendation. Overall timeout is 60 seconds.
- Final dotnet build: zero warnings/errors. dotnet test: 74 passed, zero failures
  or skipped. New tests cover real-format stderr/ANSI, units, wrong/clamped context,
  wrong model/offload, inconsistent streams/memory, context limits, missing and
  ambiguous/remote models, CLI failures/capabilities, bounded no-fit iteration,
  cancellation (including a fitting response), culture and NVIDIA free VRAM.
- Actual runtime verification executed the same MemoryAdvisor with ProcessRunner
  from a temporary console harness under ignored tests/bin/M3Acceptance.
  Full output was saved there as runtime.txt. Every command used estimate-only.
- Initial direct attempt using the full indexed file path was rejected by LMS and
  opened an interactive selector; it was cancelled without selection. Subsequent
  implementation uses the unique actual modelKey qwen3.8-27b-abliterated and --yes
  with redirected/closed stdin, never the path as a CLI model key.
- Final actual snapshot: total 24564 MiB, free 22879 MiB, reserve 1229 MiB;
  budget 21.142578125 GiB. Context 65536 throughout. All seven estimates exit 0:

| GPU offload | LMS GPU estimate, GiB | LMS total estimate, GiB |
| --- | --- | --- |
| 100% | 28.52 | 28.52 |
| 95% | 27.68 | 28.52 |
| 90% | 26.02 | 28.52 |
| 85% | 24.77 | 28.52 |
| 80% | 23.52 | 28.52 |
| 75% | 22.27 | 28.52 |
| 70% | 20.60 | 28.52 |

- Recommendation: 70% GPU at 65,536 context. Before and after the actual advisor
  run, lms ps --json returned []; no model was loaded. The runtime harness passed.
  The guardrail message saying a model may be loaded was not used as fit evidence.
- Updated M3 executable was launched through RunAs; process 9116 reported the
  expected Local Model Manager — M3 window title and Responding=True.
- Visual/click-through UI acceptance remains unverified: Windows computer-use
  initialization timed out again. No screenshot or successful UI click is claimed.
  Next manual check: select the imported GGUF, open Memory / Hermes 64K, click
  estimate, compare with the above measurements, and verify cancellation.
- Known limitations: one NVIDIA GPU only; estimates depend on current free VRAM;
  available system RAM, actual load stability/performance and API inference remain
  untested. The total estimate is displayed, without claiming RAM suitability.
- Changed: Infrastructure/LMStudio/MemoryAdvisor.cs (new),
  Infrastructure/Hardware/NvidiaHardwareInfoService.cs, App/MainViewModel.cs,
  App/MainWindow.xaml, Tests/M3Tests.cs (new), README.md and VERIFICATION.md.
- M3 advisor runtime is verified; UI acceptance is pending. After UI acceptance,
  recommended next milestone is M4: explicit confirmed model loading using the
  verified estimate settings. M4 was not started.
## M3 UI evidence supplied by user — 2026-10-18

- User supplied a screenshot of the running M3 application after clicking the
  estimate action for the real Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
- Memory / Hermes 64K tab, estimate button, selected model, hardware, recommendation,
  Results and Logs are visible. Form controls and summary fit without form scrolling.
- UI shows context 65,536; free VRAM 22.34 GiB; reserve 1.20 GiB; budget 21.14 GiB;
  full-GPU estimate 28.52 GiB; recommendation 70% GPU / 20.60 GiB; total 28.52 GiB.
  The final LMS estimate in Results agrees with the recommendation.
- This confirms successful normal-path use through the UI and supersedes the
  earlier pending visual/normal-path UI acceptance. Agent-side cancellation tests
  passed previously; cancellation through the UI is not established by this image.
- M3 normal path accepted with the previously documented single-NVIDIA-GPU,
  point-in-time VRAM, unverified RAM and actual-load limitations.
- Documentation only changed; build/tests were not rerun. Latest tests: 74 passed.
- Recommended next milestone remains M4: explicitly confirmed model load with
  the advised context/offload. No actual model loading was started in this step.
## M4 confirmed load implementation and runtime verification — 2026-10-18

- Scope: explicitly confirmed local model load using the M3 recommendation,
  fixed Hermes context 65,536, requested GPU ratio and parallelism 1. No API server
  start/stop, endpoint changes, inference calls, downloads or automatic unload.
- Added ModelLoader and Windows GlobalMemoryStatusEx physical RAM reading. Load
  preflight checks CLI capabilities, exact local catalog identity, no existing
  loaded models, a fresh LMS estimate at the recommended ratio and current VRAM/RAM.
- Exact local path selection uses installed CLI --exact/--local, first exercised
  with estimate-only on this PC. These flags are hidden from ordinary help in
  this LMS version; unsupported flags fail during preflight before a real load.
- RAM gate deliberately requires full LMS total estimate plus max(2 GiB, 5% of
  installed RAM) in free physical RAM. VRAM uses the M3 reserve. This conservative
  admission policy may reject otherwise feasible loads and is not a new model
  memory formula. The confirmation shows the estimate, budget, free RAM and reserve.
- User confirmation defaults to No. Declining performs no actual load. Plans older
  than two minutes are refused. After confirmation the source, catalog, loaded
  state and resources are checked again. Actual load has no automatic timeout;
  preflight/rechecks have 60-second deadlines. All streams and exits remain logged.
- Each load has a generated lmm-... identifier. Success requires load exit 0 and
  an independent ps --json match for identifier, modelKey, path, local device and
  contextLength=65536. CLI cancellation/failure triggers independent bounded state
  inspection and never claims success merely because the process exited.
  Backend cancellation is not guaranteed; automatic unload/retry is not performed.
- Added the Load tab, confirmation dialog, visible state and refresh-loaded button.
  Advice is invalidated when the file/category/CLI configuration/discovery changes.
  A load attempt consumes the displayed recommendation; another attempt requires
  a new estimate. The memory tab text no longer claims that the model is unloaded.
- Final dotnet build: 0 warnings/errors. dotnet test --no-build: 88 passed, zero
  failures/skips. M4 tests cover refusal, exact local arguments, invariant ratio,
  preexisting loads, estimate failure, insufficient RAM, changing resources/source/
  catalog, cancellation at confirmation and during load, wrong instance/clamped
  context and CLI failure without retry or context reduction.

### Actual runtime checks

These invoked the same production ModelLoader/ProcessRunner through a temporary
console harness under ignored tests/bin/M4Acceptance. They do not establish
MessageBox click-through behavior. The user's instruction to perform M4 authorized
this load; the harness explicitly exercised false and true confirmation callbacks.

- Before loading, lms ps --json returned []. SystemMemoryInfo read approximately
  63.81 GiB physical RAM, 47.30 GiB available. The real M3 advisor again selected
  70% GPU with context 65,536, 20.60-GiB GPU and 28.52-GiB total estimates.
- Real refusal path: all preflight commands succeeded; confirmation callback
  returned false; a subsequent ps was still []. No actual load command ran.
- Approved plan: free RAM 47.30000686645508 GiB, RAM reserve 3.190707588195801 GiB,
  GPU budget 21.126953125 GiB, expected local path
  local/qwen3.8-27b-abliterated-q5_k_m/Qwen3.8-27B-ABLITERATED-Q5_K_M.gguf.
- Actual load returned exit 0 and Model loaded successfully in 15.50s.
- Verified instance: <loaded-instance-id>;
  modelKey=qwen3.8-27b-abliterated; expected GGUF path; deviceIdentifier=null;
  contextLength=65536; status=idle; parallel=1; engineConfigFileEnabled=false.
  The ps response does not expose the actual GPU layer fraction; 70% is the
  requested/estimated setting, not independently measured offload.
- Post-load NVIDIA snapshot: 24564 MiB total, 18282 MiB used, 5861 MiB free.
  This is all GPU allocations, not a per-model measurement.
- Source GGUF still exists with 19535703264 bytes. Model remains loaded for the
  user; no automatic cleanup/unload was performed.
- Repeated actual ModelLoader preflight while that instance was loaded refused
  before another confirmation/load. Independent ps still showed exactly one
  expected instance. No duplicate or unload occurred.
- Full runtime command output is retained locally in the ignored harness folder
  as runtime.txt; refusal-only evidence is in preflight.txt.
- Updated app launched through RunAs, PID 4568; its M4 window was not visually
  inspected by automation. Manual UI confirmation/cancellation acceptance remains
  pending; prior computer-use initialization failures were not worked around.

M4 service runtime is verified. UI click-through and cancellation of a real backend
load are not claimed; cancellation edge cases are covered by unit/process tests.
Next manual check: in the open M4 window choose Load -> Check loaded models and
confirm the visible status/context. Actual API inference and Hermes connection
belong to the next milestone and have not been tested here.

Changed: Infrastructure/LMStudio/ModelLoader.cs (new),
Infrastructure/Hardware/SystemMemoryInfo.cs (new), App/MainViewModel.cs,
App/MainWindow.xaml.cs, App/MainWindow.xaml, Tests/M4Tests.cs (new), README.md and
VERIFICATION.md. The folder still has no .git baseline; no commit/push was attempted.
## M4 loaded-state refresh confirmed through app — 2026-10-18

- User supplied actual M4 app logs at 19:29:35–19:29:36: the refresh-loaded action
  invoked ps --json and returned exit 0.
- Response contains the same local instance <loaded-instance-id>,
  modelKey qwen3.8-27b-abliterated and expected GGUF path, deviceIdentifier=null,
  contextLength=65536, status=idle, queued=0, parallel=1 and no engine config file.
- This establishes the loaded-state refresh path through the application and that
  the successfully loaded instance remains present. It does not establish a new
  GUI load confirmation, GUI refusal/cancellation, inference or API health.
- Documentation only changed; no tests rerun. Latest build: zero warnings/errors;
  tests: 88 passed. Next recommended milestone: API verification and Hermes endpoint
  preparation, subject to the user's instruction to continue. It has not started.
## M5 API checks and Hermes profile preparation — 2026-10-18

- Scope: verify loaded local model context, OpenAI-compatible API inference,
  select appropriate LAN IPv4 and prepare/export Hermes configuration fragments.
  No active Hermes configuration, API authentication/CORS/firewall settings, model
  load settings, existing server bind or loaded instance was changed in this step.
- Added HermesApiService and NetworkEndpoints, API / Hermes tab, readable status,
  and local/LAN YAML exports. Export is disabled until verification succeeds;
  file/category/CLI/discovery/load changes invalidate it. Export requires a recent
  check (five minutes), revalidated after the save dialog.
- When LMS is stopped, the service checks CLI help and starts bind 0.0.0.0 / port
  1234. This branch is covered with a fake runner, not an actual stop/start here;
  the target server was already running correctly and was preserved.
- Exact selected instance/context are obtained from ps --json. A selected GGUF
  restricts the match to its category/filename; otherwise exactly one local LLM
  is required. Context below 65,536/unknown, remote or ambiguous model is rejected.
- GET /v1/models must list the exact identifier. POST /v1/chat/completions uses that
  identifier, one fixed synthetic prompt, temperature 0, max_tokens 128 and stream
  false. Expected response model, assistant content and finish_reason=stop are
  checked. Reasoning-only/empty/truncated replies are not accepted. Model/context
  identity is checked again with ps; no fallback model is selected.
- HttpClient bypasses proxies and redirects. HTTP statuses and CLI stdout/stderr/
  exit codes remain visible. Displayed response contains content only, without
  reasoning_content. Error bodies are bounded to 2000 chars for diagnostics.
- LAN requires an up Ethernet/Wi-Fi IPv4 with a default gateway; loopback, APIPA,
  tunnel/virtual and known VPN/WARP adapters are excluded. A listener at 0.0.0.0:1234
  and a successful /models check to that IP from this PC are required for LAN export.
  LAN has a 10-second deadline; local readiness survives an optional LAN failure.

### Actual checks executed

- Existing server status: running at port 1234; Windows listener 0.0.0.0:1234.
- Local GET http://127.0.0.1:1234/v1/models: HTTP 200, lists
  <loaded-instance-id>.
- Local POST http://127.0.0.1:1234/v1/chat/completions: HTTP 200, content LMM_OK,
  finish_reason stop, matching instance ID. This was real inference on the loaded
  Qwen3.8-27B model, not an HTTP fixture or estimate.
- Ran the production HermesApiService/ProcessRunner/HttpClient from an ignored
  console harness in tests/bin/M5Acceptance, including a final rerun after fixes.
  It passed all API/model checks without loading/unloading/restarting anything.
- Actual selected LAN: Intel(R) Wi-Fi 6E AX211 160MHz, interface index 12,
  address <LAN-IP>, gateway <LAN-GATEWAY>. The up sing-tun adapter tun0 with
  gateway <VPN-GATEWAY> was not selected.
- GET http://<LAN-IP>:1234/v1/models from this same PC: HTTP 200, matching ID.
  Access from a different device is not tested and is not claimed.
- The same local instance remained loaded with actual contextLength=65536.
- Saved new profiles/hermes-lmm-local.yaml and profiles/hermes-lmm-lan.yaml using
  CreateNew, without overwriting preexisting files. Native provider lmstudio,
  exact instance ID, appropriate base_url and context_length=65536; no API key.
  The LAN IP/model identifier are measured snapshots, not hardcoded application data.
- Found installed Hermes under C:\Users\admin\AppData\Local\hermes\hermes-agent.
  Both files parsed successfully using its venv Python/PyYAML. The installed
  hermes_cli.config.load_config_readonly accepted the local profile in an isolated
  tests/bin/M5Acceptance/hermes-home. No active Hermes config/.env was overwritten.
  This does not establish a full Hermes agent turn, provider/tool routing or tools.
- Final dotnet build: 0 warnings/errors. dotnet test --no-build: 110 passed,
  zero failures/skips. M5 tests cover success, start arguments, no CORS/restart,
  local-only listener, LAN failure/timeout, HTTP 401/500, missing/wrong model,
  changed instance, cancellation, response shape, context/remote/ambiguity,
  selected-file restriction, YAML quoting and physical LAN adapter selection.
- Final runtime output retained in ignored tests/bin/M5Acceptance/runtime-final.txt;
  profile validation helper is retained there as validate_profiles.py.
- Opened final M5 app via RunAs, PID 25032. Automated visual/button/save-dialog
  acceptance is not claimed; existing Windows computer-use issues remain.

### Outcome and limits

M5 API inference and profile preparation are runtime verified. Model and server
remain available. Full Hermes agent conversation/tool-use, remote-device LAN
access, and GUI export click-through remain unverified. Key-authenticated API
checks and automatic reconfiguration of already-running servers are outside this
MVP. No installation/security settings or active Hermes config changed.

Changed: Infrastructure/LMStudio/HermesApiService.cs (new),
Infrastructure/Network/NetworkEndpoints.cs (new), App/MainViewModel.cs,
App/MainWindow.xaml.cs, App/MainWindow.xaml, Tests/M5Tests.cs (new), README.md,
VERIFICATION.md, profiles/hermes-lmm-local.yaml (new), profiles/hermes-lmm-lan.yaml
(new). Next recommended milestone: an isolated real Hermes turn and tool-call
compatibility check, then deliberate connection of the intended Hermes profile.
That next milestone has not been started.

## M6 — GitHub updates (0.6.0)

- `dotnet build -c Release`: passed, 0 warnings/errors.
- `dotnet test -c Release --no-build`: 130 passed, 0 failed/skipped.
- 20 updater tests cover allowed public GitHub URLs, invalid sources, persisted/invalid settings,
  newer/same/older releases, verified downloads, missing files and checksum failure.
- Self-contained Windows x64 Setup, full nupkg and win-x64 feed built with Velopack 1.2.0.
- WPF window instantiated and rendered in an isolated harness: source/version bindings present,
  restart disabled before a verified download. This is not a full manual GUI acceptance run.
- Installed baseline 0.5.9 in a separate acceptance directory using the real Setup.exe (exit 0).
- Production ApplicationUpdater downloaded and verified the actual 0.6.0 full package.
- Native Update.exe applied it: log reports Package applied successfully; sq.version changed
  from 0.5.9 to 0.6.0. No LM Studio model was loaded/unloaded for this test.
- Public source/release workflow checks are recorded after publication below.

Limitations: first install requires Setup.exe; public stable releases only; no Authenticode
certificate configured. Package checksums establish download integrity, not publisher identity.
Models and active Hermes profiles are outside the application update package.
