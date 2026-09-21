# OllamaNetGB

Avalonia desktop app that lets either you or a local Ollama agent play Game Boy Advance games through mGBA. The architecture is game-neutral: profiles and memory can teach the agent about a particular game without hard-coding the main loop to Pokemon.

## Revision: Windows + perception/planner loop

This revision splits each AI turn into two stages:

1. **Perception** receives a short chronological burst of screenshots and converts the visual state into a compact text packet: screen type, visible text, summary, confidence, temporal changes, and whether dialogue appears complete.
2. **Planner** receives only that perception packet plus the current goal/task, recent inputs, profile knowledge, and trusted Neo4j memories. It chooses the next controller action or `WAIT`.

The final screenshot is authoritative. Earlier screenshots are temporal context only. If dialogue is still typing, the perception stage can mark it incomplete and the planner can choose `WAIT` instead of guessing what the rest of the sentence says.

Static screens are cached: when the final frame has not changed since the previous decision, the previous perception result is reused instead of running another vision inference.

## Loop

```text
mGBA
  -> sample 1-4 chronological frames
  -> wait briefly for a stable final frame
  -> perception model (images -> compact state)
  -> planner model (text state + goals/memory -> action)
  -> A/B/D-pad/etc OR WAIT
  -> observe again
```

The stability sampler defaults to three perception frames, 120 ms capture spacing, three matching frames, and a 2.5 second timeout. Animated screens can time out safely; animation alone does not require the agent to stop.

## Models

Settings now provide three model fields:

- **Fallback model**: used when either stage-specific model is blank. Default: `gemma3:4b`.
- **Vision model**: optional dedicated image-capable model for perception.
- **Planner model**: optional text model for decisions. It does not need vision capability.

For a simple first run, leave the Vision and Planner fields blank and put `gemma3:4b` in Fallback model. Later you can mix a fast vision model with a faster/smarter text planner.

## What is wired up

- Windows and Linux desktop support through Avalonia.
- Starts the selected mGBA executable with the selected ROM.
- Starts the mGBA-http companion process when it is not already running.
- Uses `mGBASocketServer.lua` for the mGBA-http connection.
- Captures temporal frame bursts and waits briefly for the screen to settle.
- Separate Ollama perception and planning passes.
- `WAIT` action for unfinished dialogue/transitions; it sends no controller input.
- Reuses perception on unchanged final frames.
- Short atomic multi-action combos are still supported when no intermediate observation is needed.
- Game-specific profiles remain optional and separate from the generic agent loop.
- Neo4j candidate/confirmed/trusted/pinned/rejected memory workflow.
- SQLite run/inference metrics.
- Live observation, perception-loop status, goal, task queue, rationale, action history, and memory UI.

## Prerequisites

1. .NET 10 SDK.
2. mGBA.
3. `mGBA-http` and `mGBASocketServer.lua` from the mGBA-http project.
4. Ollama and at least one installed model. The perception stage requires an image-capable model.
5. A legally obtained supported ROM.

## Windows

Install the Windows builds of mGBA and mGBA-http, then run:

```powershell
dotnet restore
dotnet run
```

In Settings select:

- `mGBA.exe`
- the Windows mGBA-http executable
- `mGBASocketServer.lua`
- your ROM
- Ollama URL, normally `http://localhost:11434`
- `gemma3:4b` as the fallback model for the initial test

Paths containing spaces are passed to child processes with `ProcessStartInfo.ArgumentList`, so normal locations such as `Program Files` are supported.

mGBA 0.10.x does not provide a command-line switch for loading the Lua script. After mGBA opens, use **Tools -> Scripting -> File -> Load script** and select `mGBASocketServer.lua`. Load it once for that mGBA instance.

## Linux

```bash
dotnet restore
dotnet run
```

Linux executable permission checks remain Linux-only; saving Settings attempts `chmod +x` on selected emulator/helper binaries when needed.

## Suggested first settings for Pokemon Emerald

```text
Fallback model: gemma3:4b
Vision model:    (blank)
Planner model:   (blank)
Frames/perception: 3
Capture spacing:   120 ms
Stable matches:    3
Stability timeout: 2500 ms
Frame interval:    750 ms
Max actions/turn:  1 initially
Game ID:           pokemon-emerald
```

Once movement/menu behavior is reliable, try two or three actions per turn for deterministic sequences.

## Memory and profiles

The application owns the authoritative goal/task state. Models propose changes; `AgentCoordinator` validates and applies them. Game profiles supply optional searchable hints, while Neo4j stores durable learned facts and progress by Game ID.

The bundled `profiles/pokemon-emerald.json` remains an example rather than special-case engine logic. Add additional JSON profiles for other games without changing the main agent loop.

## Metrics

SQLite metrics are stored beside `settings.json` in the platform application-data directory. A turn records aggregate perception + planner inference metrics and keeps the raw structured responses for debugging. Screenshots are not stored in the metrics database.

## Troubleshooting

- **mGBA did not connect:** load `mGBASocketServer.lua` from mGBA's scripting window and ensure the configured mGBA-http URL/ports are free.
- **No screenshot:** confirm the ROM is running and the helper can write to the system temp directory.
- **Ollama model not found:** compare the app's Ollama URL with the daemon used by `ollama list`.
- **Vision request fails:** the configured Vision/Fallback model must support images.
- **Agent waits repeatedly:** inspect the Perception Loop and Last Observation fields; temporarily lower stable matches or increase the stability timeout if the game animates heavily.
- **Windows executable will not launch:** use the actual `.exe`, not a shortcut (`.lnk`), and test the path from Settings.

Settings are stored under the operating system's application-data directory in `OllamaNetGB/settings.json`.

## Merge of Windows and diagnostic fixes (September 21)

Based on OllamaNetGB15.zip. The perception/planner stages, frame sampling,
Wait actions, action sequences, memory controls, model compatibility, and SQLite
metrics remain in this revision.

- Screenshots use unique temporary paths, retry reads briefly for file locks,
  and clean up captures instead of reusing a potentially stale frame.
- The local mGBA-http URL must use HTTP/HTTPS and a loopback address.
  Keep the player, mGBA and mGBA-http on the same machine. Ollama and Neo4j may
  be remote.
- Startup retries reuse the owned bridge process and report early process exits.
- Windows executable paths must end in .exe; process paths are made absolute.
- Profiles are explicitly copied on publish.
- Failed Ollama HTTP responses are logged in full; the status display wraps,
  while its error summary remains limited to 400 response characters.
- Caught gameplay exceptions are logged with their stack traces.
- Startup and error logs are saved under the platform's LocalApplicationData
  directory, in OllamaNetGB/logs. On Windows this is
  %LOCALAPPDATA%\OllamaNetGB\logs. The exact path is printed at startup.
  Console output is also kept visible when the host provides a console.
- The optional Windows launch script was removed; use dotnet restore / dotnet run.

Validation: patch application, archive integrity, XML/JSON syntax and preservation
of the newer core feature files were checked. No .NET SDK or Windows desktop was
available here, so compilation and live integration remain untested.
