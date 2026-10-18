# Local Model Manager

Windows 11 / .NET 8 / WPF / x64. Current scope: **M1 + M2 (safe GGUF import) + M3 (memory advice for Hermes 64K) + M4 (confirmed model load) + M5 (API checks and Hermes profiles)**.

## Build and run

Install the .NET 8 SDK, then from the repository root:

```powershell
dotnet build
dotnet test
dotnet run --project src/LocalModelManager.App
```

If building with a newer SDK, also install **.NET Desktop Runtime 8 x64** to run
the WPF app. `Microsoft.NETCore.App 8` alone is insufficient; check that
`dotnet --list-runtimes` includes `Microsoft.WindowsDesktop.App 8`.

If using the user-local SDK installed during development on this machine,
first select it in the current PowerShell session:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\LocalModelManager\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

The app displays metadata for one dropped or selected `.gguf` file without
moving or copying it. M1 checks the extension and filesystem metadata, not GGUF
contents. Discovery searches PATH, `%USERPROFILE%\.lmstudio\bin\lms.exe`, then
the optional path entered in the window. That override is session-only.

NVIDIA information comes from `nvidia-smi` (installed with the NVIDIA driver).
Memory is displayed in MiB and GiB; driver-reported usable total may be slightly
below the advertised 24 GB. No GPU or LMS values are hardcoded.

The model-list and server-status buttons invoke only `lms ls` and
`lms server status`. Output, stderr, and exit codes appear in Logs. CLI tables
are preserved; status recognition is best-effort and unknown text remains
visible. Diagnostic commands and import preflight have a 60-second timeout.
The actual import can be cancelled but has no automatic timeout, because a full
copy can take much longer.

## M2: connect an existing GGUF

1. Drop or select one GGUF. The app suggests `local/<filename>` automatically; you can edit it. For example, use
   `local/qwen3.8-27b`. This is the target namespace inside LM Studio's configured
   models folder; it does not claim to identify the model's original publisher.
2. Leave **Copy** unchecked for the default **Symbolic Link** mode.
3. Click **Подключить к LM Studio…**. The app checks the GGUF signature, source size,
   namespace and installed CLI's help, then calls `lms import ... --dry-run`.
4. Review the mode, source, namespace and LMS destination plan in the confirmation
   dialog. **No** is the default. **Yes** performs the chosen operation.
5. Review the result and Logs. After exit code 0 the app checks source existence
   and byte count, then refreshes `lms ls`. Indexing can lag behind import; if the new model is missing immediately, wait briefly and click **Список моделей** again. It does not load the model.

Copy is an explicit alternative: check **Copy** and confirm the full file size in
the dialog. The app never changes modes after failure. Every import includes
exactly one of `--symbolic-link` / `--copy`, plus `--user-repo` and `--yes` to avoid
hidden prompts. It never invokes the default move operation.

Keep the original GGUF in place when using a symbolic link. On Windows, insufficient
symlink privileges can cause failure; the CLI error is shown without automatic
elevation or settings changes. An existing destination is reported by LMS as an
error. Cancellation or disk-full errors during copying may leave a partial target;
inspect it manually before retrying. The app does not delete partial or existing files.
The app only checks the GGUF magic bytes, not the full model's validity. M2 handles
one file per operation; automatic assembly of split models is not implemented.

CLI behavior: [official lms import documentation](https://lmstudio.ai/docs/cli/local-models/import).

## Manual acceptance on a machine with LM Studio and NVIDIA

1. Launch the app and check detected LMS path and GPU name/VRAM.
2. Drop an existing GGUF and compare path, filename, and byte count with file properties.
3. Click **Список моделей** and compare with `lms ls` in a terminal.
4. Click **Статус сервера** and compare with `lms server status`.
5. Check errors are visible if dependencies are unavailable; use the optional LMS path and refresh.

## M2 manual acceptance on the target PC

1. Use a GGUF not already imported into the chosen namespace. Keep Copy unchecked.
2. Review the dry-run target and cancel once: no model file/link should appear there.
3. Repeat and confirm. Check `exit=0`, the refreshed model list, and that the original
   GGUF still exists with the same size.
4. At the exact target path shown by LMS, verify the link in PowerShell:
   `Get-Item -LiteralPath '<target path>' | Select-Object FullName,LinkType,Target`.
   Expect `SymbolicLink` pointing to the original GGUF.
5. Repeat the same import: LMS should report an existing target, not replace it.
6. For Copy, first cancel its confirmation and verify no copy starts. Test confirmed
   copying only with a deliberately chosen small GGUF and sufficient disk space.

Tests cover discovery precedence, CLI output, NVIDIA CSV parsing, file metadata,
process output/exit/cancellation, import flags, invalid namespaces and GGUF headers,
copy consent, CLI capability checks and failure without fallback. CLI import tests
use a fake command runner; they do not claim real LM Studio integration.
Server start/stop, network selection and Hermes API configuration
remain outside this milestone.

## M3: estimate memory for Hermes

1. Select or drop an already-connected GGUF. Its import category must match the
   one in the LM Studio catalog (editable on the import tab).
2. Open **Память · Hermes 64K**, then click **Оценить память для Hermes**.
3. Read the recommendation and Logs. No model is loaded and no server is started.

The advisor reads fresh total/free VRAM from NVIDIA, reserves 5% of total VRAM
(minimum 1 GiB) in addition to memory already in use, then asks LMS for estimates
from 100% GPU downwards in 5% steps. It stops at the first estimate within that
budget, allowing 0.005 GiB for CLI rounding. Context always remains 65,536; a model
reporting a lower maximum receives an explicit warning rather than reduced context.

The advisor resolves the exact import category/filename to one local modelKey via
`lms ls --json`, checks installed CLI capabilities, and executes only
`lms load <modelKey> --context-length 65536 --gpu <ratio> --estimate-only --yes`.
Every response is checked for the requested model, context and offload. Both streams
and exit codes remain visible. Failure, cancellation, unsupported output, ambiguous
model selection or missing GPU data produces no recommendation. The diagnostic
operation has a 60-second overall deadline and can be cancelled with **Отмена**.

M3 currently supports one NVIDIA GPU and one local GGUF language model. Multiple
NVIDIA GPUs and remote model placement are explicitly rejected. The result is a
point-in-time estimate, not a load guarantee: RAM availability and actual runtime
behavior are not validated in M3. Rerun after changing GPU workloads. The GPU
reserve is a conservative MVP policy, not a private model memory estimation formula.

For the reference Qwen3.8-27B Q5_K_M on this RTX 4090, the real advisor run selected
70% GPU (20.60 GiB estimated GPU memory) at a roughly 21.14-GiB budget. At 100% LMS
reported 28.52 GiB. These are captured measurements, not hardcoded defaults.
See VERIFICATION.md for runtime evidence and UI acceptance.
## M4: load the model with confirmation

1. Select the connected GGUF and obtain a recommendation on the memory tab.
2. Open **Загрузка** and click **Загрузить модель…**. M4 checks the catalog,
   existing loaded models, a fresh LMS estimate and available VRAM/RAM.
3. Review model, context 65,536, GPU fraction and memory checks in the confirmation.
   **No** is the default; declining never runs the actual load command.
4. On **Yes**, M4 rechecks the source, catalog, loaded models and resources before
   loading exactly the local catalog path. Plans older than two minutes are rejected.
5. Success requires both exit 0 and the expected unique instance/model/path/local
   device/context in `lms ps --json`. **Проверить загруженные модели** refreshes
   actual backend state and shows the original response in Results/Logs.

Load uses the advised GPU ratio, fixed context 65,536 and parallelism 1. The model
receives a generated lmm-... identifier. Context/offload are not silently reduced,
no existing model is unloaded, and no API server is started or reconfigured.
M4 refuses to start if any model is already loaded, preventing duplicates and
implicit resource competition in this MVP.

Before and after confirmation VRAM must cover the fresh LMS GPU estimate with
M3's reserve policy. A deliberately conservative RAM gate requires free physical
RAM to cover the **full LMS total estimate** plus max(2 GiB, 5% of installed RAM).
This can reject loads that might work with GPU allocation or paging; it is an
admission policy, not a private formula for model memory. Estimates cannot guarantee
actual load behavior. Missing resource data is a visible error.

Preflight/rechecks have 60-second deadlines. The real load has no automatic timeout
and can be cancelled. Cancelling kills the CLI process tree, but the backend may
continue or finish loading. M4 independently inspects `ps` after cancellation and
reports uncertainty; it never automatically unloads or retries. Inspect loaded
models before attempting another load. Closing the window requests cancellation.

The reference model was actually loaded at context 65,536 with GPU requested at
70%; LMS returned exit 0 in 15.50 seconds and reported the instance idle with
contextLength 65536 and parallel 1. M5 API inference checks are described below.
## M5: OpenAI-compatible API and Hermes profiles

1. Keep the model loaded and open **API · Hermes**. Selecting a GGUF restricts
   the check to its category/filename; without a selection, exactly one local
   loaded language model must be present.
2. Click **Проверить API и подготовить Hermes**. M5 checks actual loaded context
   (at least 65,536), GET /v1/models, then sends one short synthetic chat request
   to the exact instance identifier through POST /v1/chat/completions.
3. Read the verified local and LAN endpoints. Save a local or LAN YAML profile
   using the corresponding button. Existing Hermes configuration is not modified.
   Profiles contain the model section for config.yaml; merge that section into
   the intended Hermes profile/configuration, not blindly over an existing file.

Hermes local preset is bind 0.0.0.0, port 1234. If LMS reports its server stopped,
M5 starts it with those settings, after checking CLI support. An already running
server is preserved. If it is on another port or only loopback, errors/LAN status
are shown; M5 does not silently stop/reconfigure it. CORS and firewall settings
are not changed. This MVP checks the current keyless local API; a server requiring
an API key fails visibly with its HTTP status rather than generating a ready profile.

The generated profile uses Hermes's native provider lmstudio, the exact loaded
instance ID, base_url ending in /v1 and the actual loaded context_length. No API key
is inserted. Installed Hermes's config loader was checked against the generated
profile in an isolated HERMES_HOME. This is configuration validation, not a full
Hermes conversation or tool-use acceptance test.

A valid chat check requires the expected model ID, assistant text content and a
normal completed response. Reasoning-only, empty, truncated or wrong-model replies
are rejected. Loaded identity/context are checked again after the request. HTTP
redirects and proxies are disabled for these local checks; both CLI streams and
HTTP statuses are logged. No reasoning text is copied into the displayed answer.

LAN selection uses an up Ethernet/Wi-Fi IPv4 interface with a default gateway,
excluding loopback, APIPA and known VPN/tunnel/virtual adapters. No LAN IP is
hardcoded. A LAN profile is offered only if 0.0.0.0:1234 is listening and a models
request to the selected LAN IP succeeds from this PC. This **does not verify access
from another device** or its firewall/routing path. LAN probing has a 10-second
limit; its failure preserves a successfully verified local connection. Total
operation timeout remains 60 seconds and cancellation is available.

Changing selection/category/CLI path/discovery or starting a new load invalidates
the prepared profile. UI export requires a check within the previous five minutes,
including a second freshness check after the save dialog. Saved profiles remain
snapshots: after model unload/reload, its generated instance ID can change; after
a network change, the LAN IP can change. Recheck and regenerate as needed.

Reference output files in this workspace:
- profiles/hermes-lmm-local.yaml — this PC, http://127.0.0.1:1234/v1.
- profiles/hermes-lmm-lan.yaml — current LAN snapshot; check VERIFICATION.md.

Official provider/schema references:
[Hermes providers](https://hermes-agent.nousresearch.com/docs/integrations/providers),
[LM Studio OpenAI compatibility](https://lmstudio.ai/docs/developer/openai-compat).
