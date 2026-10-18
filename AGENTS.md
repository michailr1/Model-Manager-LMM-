# AGENTS.md

## Project: Local Model Manager

Local Model Manager is a Windows utility that automates the workflow:

GGUF → LM Studio → memory estimate → model load → OpenAI-compatible API → Hermes.

Current target stack:
- Windows 11
- .NET 8
- C#
- WPF
- x64

Current hardware reference:
- NVIDIA RTX 4090
- 24 GB VRAM

Current LM Studio CLI reference path:
- `C:\Users\admin\.lmstudio\bin\lms.exe`

Typical local model source directory:
- `E:\models`

Hermes uses an OpenAI-compatible endpoint and currently requires a context window of at least 64K.

---

## Development principles

These rules apply to all work in this repository.

### 1. Think before coding

Do not start implementation immediately.

First:
- inspect the repository and current state;
- identify the exact requested scope;
- state important assumptions;
- identify ambiguity that could materially change the implementation;
- prefer a simpler valid approach when one exists.

Before making changes, give a short plan in this format:

1. action → verify: how it will be checked
2. action → verify: how it will be checked
3. action → verify: how it will be checked

Do not ask questions merely to avoid making a reasonable decision. If the safe and obvious choice is clear, proceed.

### 2. Simplicity first

Implement the minimum code required for the current milestone.

Avoid:
- speculative architecture;
- premature abstractions;
- generic frameworks built only for hypothetical future needs;
- unnecessary configurability;
- unrelated refactoring.

Architecture may leave room for future providers, but the MVP only needs LM Studio.

Do not implement Ollama, llama.cpp, vLLM, ComfyUI, image-model management, LoRA support, model downloading, benchmark suites, or other future features unless explicitly requested.

### 3. Make surgical changes

Change only what is necessary for the requested task.

Do not:
- refactor unrelated code;
- rename things without a concrete reason;
- reformat unrelated files;
- delete code whose purpose is not understood.

When existing code looks unusual, determine why it exists before changing it.

### 4. Work toward verifiable goals

A task is not complete because code was written or because it compiles once.

Use the loop:

implement → build → test → inspect → fix → repeat

Continue until:
- the requested acceptance criteria pass; or
- a real blocker is reached.

Do not claim something was tested unless the command or behavior was actually verified.

If blocked, report:
1. what is blocking;
2. what was already checked;
3. available options;
4. the recommended option.

---

## Scope discipline

The primary MVP path is:

GGUF
→ LM Studio
→ estimate
→ load
→ API
→ Hermes

Do not expand scope silently.

For each milestone:
- implement only that milestone;
- verify its success criteria;
- stop before the next milestone unless explicitly asked to continue.

---

## LM Studio integration rules

Known useful commands:

```powershell
lms ls
lms load <model> --context-length 65536 --gpu 0.75 --estimate-only
lms load <model> --context-length 65536 --gpu 0.75
lms unload <model>
lms server start --bind 0.0.0.0 --port 1234
lms server stop
lms server status
lms import "<path>" --user-repo "<publisher/repo>" --symbolic-link
```

### Import strategy

Prefer symbolic links for already-downloaded GGUF files.

Reason:
- models commonly live on `E:\models`;
- LM Studio may otherwise try to move them into a directory on `C:`;
- cross-volume rename can fail with `EXDEV`;
- copying 20–40 GB models unnecessarily wastes disk space and time.

Default:
- symbolic link

Fallback:
- explicit user-approved copy

Do not silently fall back to copying large model files.

---

## Memory advisor rules

Do not invent a private VRAM estimation formula for MVP.

Use LM Studio itself as the source of truth via:

```powershell
lms load <model> --context-length <N> --gpu <fraction> --estimate-only
```

Use iterative estimates to choose GPU offload.

A simple decrement step such as 0.05 is acceptable for MVP.

Reference measurements for Qwen3-32B Q4_K_M at 65,536 context:

- GPU 100% → ~27.42 GiB
- GPU 80% → ~22.99 GiB
- GPU 75% → ~21.88 GiB

On a 24 GB RTX 4090, leave reasonable VRAM headroom rather than targeting 100% utilization.

The VRAM reserve should eventually be based on detected hardware, not permanently hardcoded to one GPU.

---

## Hermes profile

The Hermes preset should use at least:

- context: 65,536
- API enabled
- bind: `0.0.0.0`
- port: `1234`

If a model cannot reasonably support the required context or memory requirements, show an explicit warning. Do not silently reduce the context below the Hermes minimum.

---

## Network selection

When displaying a LAN API endpoint:

Prefer an active IPv4 interface that:
- is up;
- has a default gateway.

Avoid:
- loopback;
- APIPA;
- VPN/tunnel interfaces;
- Cloudflare WARP-style virtual adapters.

Do not hardcode a LAN IP.

---

## Process execution

When invoking `lms.exe`:

Use `ProcessStartInfo` with:
- `UseShellExecute = false`
- redirected stdout;
- redirected stderr;
- arguments supplied via `ArgumentList`.

Do not concatenate shell command strings when structured arguments can be used.

Support:
- stdout;
- stderr;
- exit code;
- cancellation.

User-visible logs must not hide failures.

---

## CLI discovery

Search for `lms.exe` in this order:

1. PATH
2. `%USERPROFILE%\.lmstudio\bin\lms.exe`
3. user-configured path

If it cannot be found, show a clear error.

---

## UI behavior

The user should not need to understand internal flags such as:

```
--gpu 0.75
--context-length 65536
--estimate-only
--symbolic-link
```

The intended experience is closer to:

```
Model: <detected model>
Preset: Hermes
[Install & Run]
```

The application should explain decisions transparently, for example:

```
64K context is required by Hermes.
100% GPU offload would require 27.4 GiB VRAM.
Selected 75% GPU offload.
Estimated VRAM usage: 21.9 GiB.
Using a symbolic link to avoid copying 19 GB.
```

---

## Verification expectations

For meaningful changes, run the relevant checks.

At minimum where applicable:

```powershell
dotnet build
dotnet test
```

For runtime integration, also verify the actual command or API behavior rather than only unit tests.

LM Studio API health checks:

```http
GET http://127.0.0.1:1234/v1/models
```

and:

```http
POST http://127.0.0.1:1234/v1/chat/completions
```

Do not claim runtime integration works unless it was actually exercised.

---

## Final report format

At the end of a task, report concisely:

- Implemented
- Verified
- Tests
- Known limitations
- Files changed
- Next recommended milestone

Keep facts separate from assumptions and recommendations.
