using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NayfWindows;

/// <summary>
/// ElevenLabs TTS client — sends text to the Cloudflare Worker proxy and
/// plays back the returned MP3 audio via NAudio. Mirrors ElevenLabsTTSClient.swift.
/// </summary>
public sealed class ElevenLabsTTSClient : IDisposable
{
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;

    private readonly Uri _proxyUrl;
    private readonly HttpClient _httpClient;
    private IWavePlayer? _waveOut;
    private Mp3FileReader? _mp3Reader;

    public ElevenLabsTTSClient(string proxyUrl)
    {
        _proxyUrl = new Uri(proxyUrl);
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Converts <paramref name="text"/> to speech and plays it immediately.
    /// Cancels any currently playing audio first.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        StopPlayback();

        try
        {
            var audioBytes = await FetchAudioAsync(text, cancellationToken);
            PlayAudio(audioBytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation — no action needed
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ElevenLabs] TTS error: {ex.Message}");
        }
    }

    public void StopPlayback()
    {
        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
        _mp3Reader?.Dispose();
        _mp3Reader = null;
    }

    private async Task<byte[]> FetchAudioAsync(string text, CancellationToken cancellationToken)
    {
        var requestBody = JsonSerializer.Serialize(new { text, model_id = "eleven_flash_v2_5" });
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _proxyUrl) { Content = content };

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private void PlayAudio(byte[] mp3Bytes, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream(mp3Bytes);
        _mp3Reader = new Mp3FileReader(ms);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_mp3Reader);

        _waveOut.PlaybackStopped += (_, _) =>
        {
            PlaybackStopped?.Invoke();
        };

        PlaybackStarted?.Invoke();
        _waveOut.Play();
    }

    public void Dispose()
    {
        StopPlayback();
        _httpClient.Dispose();
    }
}
