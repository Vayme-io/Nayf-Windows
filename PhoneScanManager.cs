using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

public enum PhoneScanState { Idle, Generating, AwaitingPhoto, PhotoReceived, Expired, Failed }

/// <summary>
/// Drives the "Scan with phone" flow against the same Cloudflare Worker the Mac
/// app uses: POST /scan/start for a session, show a QR to {worker}/scan/{id},
/// then await the photo over wss:/scan/ws/{id}. Mirrors PhoneScanManager.swift.
/// </summary>
public sealed class PhoneScanManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Fires with the JPEG/PNG bytes once the phone uploads a photo.</summary>
    public event Action<byte[]>? PhotoReceived;

    private readonly Func<Task<string?>> _authTokenProvider;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    public PhoneScanManager(Func<Task<string?>> authTokenProvider)
    {
        _authTokenProvider = authTokenProvider;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    private PhoneScanState _state = PhoneScanState.Idle;
    public PhoneScanState State { get => _state; private set { _state = value; OnChanged(); } }

    private byte[]? _qrPng;
    public byte[]? QrPng { get => _qrPng; private set { _qrPng = value; OnChanged(); } }

    public void Start()
    {
        Cancel();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Set(() => { QrPng = null; State = PhoneScanState.Generating; });
        try
        {
            var token = await _authTokenProvider();
            if (token == null) { Set(() => State = PhoneScanState.Failed); return; }

            // POST /scan/start → { "session_id": "<uuid>" }
            string sessionId;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/scan/start"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                sessionId = doc.RootElement.GetProperty("session_id").GetString()
                            ?? throw new Exception("missing session_id");
            }

            // QR encodes the page the phone opens.
            var qr = GenerateQrPng($"{NayfConfig.WorkerBaseURL}/scan/{sessionId}");
            Set(() => { QrPng = qr; State = PhoneScanState.AwaitingPhoto; });

            await AwaitPhotoAsync(sessionId, token, ct);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex)
        {
            Logger.Log("PhoneScan", $"Failed: {ex.Message}");
            Set(() => State = PhoneScanState.Failed);
        }
    }

    private async Task AwaitPhotoAsync(string sessionId, string token, CancellationToken ct)
    {
        var wsUrl = NayfConfig.WorkerBaseURL.Replace("https://", "wss://") + $"/scan/ws/{sessionId}";
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        // 5-minute session window.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), linked.Token);

            var buffer = new byte[1 << 16];
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Set(() => State = PhoneScanState.Expired);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            // Payload: { "imageBase64": "...", "mimeType": "image/jpeg" }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            var b64 = doc.RootElement.GetProperty("imageBase64").GetString();
            if (string.IsNullOrEmpty(b64)) { Set(() => State = PhoneScanState.Failed); return; }

            var bytes = Convert.FromBase64String(b64);
            Set(() =>
            {
                State = PhoneScanState.PhotoReceived;
                PhotoReceived?.Invoke(bytes);
            });
        }
        catch (OperationCanceledException)
        {
            Set(() => State = PhoneScanState.Expired);
        }
    }

    private static byte[] GenerateQrPng(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data);
        return png.GetGraphic(10);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        Set(() => { State = PhoneScanState.Idle; QrPng = null; });
    }

    private void Set(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
