# Nayf for Windows

Native WinUI 3 Windows port of the Nayf AI cursor companion.

## What it does

Nayf is a smart AI companion that lives on your desktop. It:

- Sits in the **system tray** — click to open the companion panel
- Listens via **push-to-talk** (Ctrl+Alt) using your microphone
- **Sees your screen** and takes a screenshot with each interaction
- Sends your voice + screenshot to **Claude** (via the Cloudflare Worker proxy)
- Streams the response and **speaks it** via ElevenLabs TTS
- Animates a **blue cursor** that follows your mouse and flies to UI elements Claude points at
- Supports an **agentic mode** where Claude can run PowerShell commands, read/write files, and control the computer

## Architecture

This is a direct Windows port of the macOS Swift/SwiftUI app, with identical architecture:

| Mac | Windows |
|-----|---------|
| `NSStatusItem` menu bar icon | Win32 `Shell_NotifyIcon` system tray |
| `NSPanel` floating window | WinUI 3 `Window` with `OverlappedPresenter` |
| Full-screen `NSWindow` overlay | WinUI 3 transparent `WS_EX_TRANSPARENT \| WS_EX_LAYERED` window |
| `CGEvent` tap global hotkey | Win32 `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| `ScreenCaptureKit` | Win32 `BitBlt` GDI screen capture |
| `AVAudioEngine` microphone | **NAudio** `WaveInEvent` |
| `AXUIElement` selected text | UI Automation COM interop |
| `zsh -l -c` bash commands | `powershell.exe -NonInteractive` |
| Cloudflare Worker proxy | Same (shared) |
| AssemblyAI WebSocket | Same (shared) |
| ElevenLabs TTS | Same (shared) |

## Key files

| File | Purpose |
|------|---------|
| `App.xaml.cs` | Entry point — creates system tray, overlay windows, companion panel |
| `CompanionManager.cs` | Central state machine — voice state, Claude API, TTS, conversation history |
| `SystemTrayManager.cs` | Win32 tray icon with message loop on dedicated STA thread |
| `GlobalPushToTalkMonitor.cs` | Low-level keyboard hook for Ctrl+Alt PTT |
| `OverlayWindow.xaml/.cs` | Per-monitor transparent click-through overlay with blue cursor |
| `CompanionPanelWindow.xaml/.cs` | Tray dropdown panel with voice state, model picker, history |
| `ClaudeAPI.cs` | Claude streaming SSE client + agent tool-use turn executor |
| `ElevenLabsTTSClient.cs` | ElevenLabs TTS via Worker proxy, played with NAudio |
| `AssemblyAIStreamingTranscriptionProvider.cs` | AssemblyAI v3 WebSocket streaming STT |
| `BuddyDictationManager.cs` | Push-to-talk pipeline — mic capture → AssemblyAI → transcript |
| `NayfAgentManager.cs` | Agentic tool-use loop orchestrator |
| `NayfAgentToolExecutor.cs` | Tool executor: PowerShell, file I/O, computer control |
| `SelectedTextReader.cs` | UI Automation COM interop for reading selected text |
| `ScreenCaptureUtility.cs` | Multi-monitor GDI BitBlt screenshot capture → JPEG |
| `NayfConfig.cs` | Worker URL and all configuration constants |

## Prerequisites

- Windows 10 version 1903+ (19H1) or Windows 11
- [Windows App SDK 2.0 runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
- .NET 10 Runtime

## Setup

### 1. Set up the Cloudflare Worker

The Worker proxy holds your API keys. See the root `README.md` for full instructions:

```bash
cd worker
npm install
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY
npx wrangler deploy
```

### 2. Update the Worker URL

Edit `NayfConfig.cs` and update `WorkerBaseURL` to your deployed Worker URL.

### 3. Build and run

```bash
cd NayfWindows
dotnet build -c Release
dotnet run
```

Or open `NayfWindows.sln` in Visual Studio 2025.

## Keyboard shortcut

**Ctrl + Alt** — hold to record, release to send

This mirrors the macOS **Ctrl + Option** shortcut.

## Permissions

The app needs:
- **Microphone** — for push-to-talk voice capture
- **Accessibility** (optional) — for reading selected text

Windows will prompt for microphone access on first use.
