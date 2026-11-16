using System.Drawing;
using Timer = System.Threading.Timer;
using PrismOS.Common;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PrismOS.Widgets;

/// <summary>Whatever the system says is playing — any app with a media session, including a
/// browser tab. Reads the WinRT session manager already available through the referenced SDK
/// projection, so there is no new package and no network call.</summary>
/// <summary>What is playing, kept in parts. The record draws the title and the artist at different
/// sizes and weights, so a single joined string is the wrong shape for it.
///
/// TrackId is what the album-art cache keys on. It must change when the track changes and hold
/// still across a progress or playback tick, or the art is either stale or refetched constantly.</summary>
public readonly record struct NowPlayingReading(
    string? Title, string? Artist, bool IsPlaying, string TrackId, double Progress = 0d);

public sealed partial class NowPlayingSource : IWidgetSource
{
    /// <summary>Pure, so the parts and the cache key are testable without a live session.
    ///
    /// Deliberately does not include IsPlaying in TrackId: pausing dims the record, and throwing
    /// the artwork away and refetching it on every pause would be a visible stutter for nothing.</summary>
    public static NowPlayingReading Read(string? title, string? artist, bool isPlaying,
                                        TimeSpan position = default, TimeSpan duration = default)
    {
        // Zero when the session gives no usable duration — a live stream, or a player that does
        // not report one. The ring then shows its track and no arc, rather than a wrong arc.
        double progress = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0d, 1d)
            : 0d;

        string? cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        string? cleanArtist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();

        // Title and artist survive a pause. The record stays on screen dimmed, and it is still
        // showing what you were listening to — dropping the text on pause would empty the widget
        // at the exact moment you look at it to see what stopped.
        // Progress is deliberately outside TrackId: it moves constantly, and keying the artwork
        // cache on it would refetch the album art every second.
        return new NowPlayingReading(cleanTitle, cleanArtist, isPlaying,
                                     $"{cleanTitle}␟{cleanArtist}", progress);
    }

    /// <summary>Pure, so the display rules are tested without a live session.
    ///
    /// A paused track is not "now playing" and shows nothing: the line is meant to say what you
    /// are listening to, and a stale title left on the wallpaper after the music stops is worse
    /// than an empty space.</summary>
    public static string? Format(string? title, string? artist, bool isPlaying, int maxCharacters)
    {
        if (!isPlaying || string.IsNullOrWhiteSpace(title)) return null;

        string text = string.IsNullOrWhiteSpace(artist)
            ? $"♪ {title.Trim()}"
            : $"♪ {title.Trim()} — {artist.Trim()}";

        // The budget is honoured exactly, including the small values the settings panel does not
        // offer but the JSON accepts. Raising it to a floor of four meant maxCharacters: 1
        // produced four characters — a limit that quietly did not apply is worse than none.
        if (maxCharacters <= 0) return null;
        if (text.Length <= maxCharacters) return text;
        return maxCharacters == 1 ? "…" : text[..(maxCharacters - 1)] + "…";
    }

    private readonly Func<int> _maxCharacters;
    private readonly Action<Action> _toMainThread;
    private Timer? _poll;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _onSessionChanged;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _value;
    private int _generation;
    private bool _disposed;

    private NowPlayingReading _current;
    private double _lastProgress, _lastDuration;
    private long _lastProgressAt;
    private Bitmap? _art;
    private string? _artTrackId;
    private string _artKey = "";
    private string? _sourceApp;

    /// <summary>The media app's AUMID as Windows reports it, e.g. "Spotify.exe" or
    /// "Chrome_..!AppTab". Null when there is no session.</summary>
    public string? SourceApp =>
        _sourceApp ??= _session?.SourceAppUserModelId;

    /// <summary>The current track. When the session reports a duration but updates its timeline
    /// rarely — browsers and video players often do — the progress is carried forward with the
    /// wall clock between reads, so the line moves continuously instead of sitting at zero and
    /// jumping on the next event. Record identity never changes, so artwork is not refetched.</summary>
    public NowPlayingReading Current
    {
        get
        {
            if (!_current.IsPlaying || _lastDuration <= 0d) return _current;
            return _current with { Progress = Estimated() };
        }
    }

    /// <summary>The running estimate, also used when deciding whether a new session reading is
    /// newer than the clock we are already keeping.</summary>
    private double Estimated()
    {
        double elapsed = (Environment.TickCount64 - _lastProgressAt) / 1000.0;
        return Math.Clamp(_lastProgress + elapsed / _lastDuration, 0d, 1d);
    }

    public Bitmap? Art => _art;

    /// <summary>Media transport the buttons drive. Fire-and-forget: the buttons are paint, and
    /// a player that does not answer (dead session, PMP window) must not block the click.</summary>
    public enum MediaCommand { Previous, PlayPause, Next }

    public void SendCommand(MediaCommand command) => _ = SendCommandAsync(command);

    private async Task SendCommandAsync(MediaCommand command)
    {
        var session = _session;
        if (session is null) return;
        try
        {
            switch (command)
            {
                case MediaCommand.Previous: await session.TrySkipPreviousAsync(); break;
                case MediaCommand.PlayPause: await session.TryTogglePlayPauseAsync(); break;
                case MediaCommand.Next: await session.TrySkipNextAsync(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Media command {command} failed: {ex.Message}");
        }
    }

    /// <summary>Poor-man's FriendlyName for the common media apps; anything else shows the raw
    /// token lowercased, or NOW PLAYING when that is nothing either.</summary>
    public static string FriendlySourceName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Now playing";
        string t = aumid.Trim().ToLowerInvariant();
        static bool Has(string s, string needle) => s.Contains(needle, StringComparison.Ordinal);
        if (Has(t, "spotify")) return "Spotify";
        if (Has(t, "applemusic") || Has(t, "itunes")) return "Apple Music";
        if (Has(t, "zune") || Has(t, "groove")) return "Groove";
        if (Has(t, "youtube")) return "YouTube";
        if (Has(t, "chrome")) return "Chrome";
        if (Has(t, "msedge") || Has(t, "edge")) return "Edge";
        if (Has(t, "firefox")) return "Firefox";
        if (Has(t, "vlc")) return "VLC";
        if (Has(t, "mpv")) return "mpv";
        if (Has(t, "foobar")) return "foobar2000";
        if (Has(t, "windowsplayer")) return "Media Player";
        if (Has(t, "deezer")) return "Deezer";
        if (Has(t, "tidal")) return "Tidal";
        if (Has(t, "soundcloud")) return "SoundCloud";
        if (Has(t, "plex")) return "Plex";
        // Fall back to the raw app id: the part after the last '\' or '.', minus .exe.
        int cut = t.LastIndexOfAny(['\\', '!', '.', '/']);
        string token = cut >= 0 ? t[(cut + 1)..] : t;
        if (token.EndsWith(".exe", StringComparison.Ordinal)) token = token[..^4];
        return string.IsNullOrWhiteSpace(token) ? "Now playing" : char.ToUpperInvariant(token[0]) + token[1..];
    }

    /// <summary>Serialises the end of InitialiseAsync against Dispose. RequestAsync resumes on a
    /// pool thread, so checking _disposed and then subscribing is two steps a main-thread Dispose
    /// can run between — leaving a handler attached to a source nobody will unsubscribe.</summary>
    private readonly Lock _lifetime = new();

    public string? Value => _value;
    public event Action? Changed;

    /// <summary><paramref name="toMainThread"/> is the engine's queue. WinRT raises these events
    /// on a pool thread, and everything downstream — the overlay, the composition host — belongs
    /// to the main thread.
    ///
    /// <paramref name="maxCharacters"/> is read on each format rather than captured, so this
    /// source outlives a settings change and the media session is not rebuilt for one.</summary>
    public NowPlayingSource(Func<int> maxCharacters, Action<Action> toMainThread)
    {
        _maxCharacters = maxCharacters;
        _toMainThread = toMainThread;
        _ = InitialiseAsync();
    }

    /// <summary>Re-reads the session. Used when the character budget changes — the value needs
    /// re-formatting without the subscription being torn down and rebuilt.</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task InitialiseAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            // RequestAsync can outlive this source — the engine disposes and rebuilds widgets on
            // any display change. Subscribing after that would leave a handler on a manager
            // nobody unsubscribes from, and queue an AttachSession onto a disposed source.
            lock (_lifetime)
            {
                if (_disposed) return;

                _manager = manager;
                _onSessionChanged = (_, _) => _toMainThread(AttachSession);
                _manager.CurrentSessionChanged += _onSessionChanged;

                // TimelinePropertiesChanged is not a metronome: many players raise it only at
                // track boundaries, so the progress line would sit still for a whole song. Poll
                // the session as well; RefreshAsync ignores readings that did not change.
                _poll = new Timer(_ => Poll(), null, 1500, 1500);
            }
            _toMainThread(AttachSession);
        }
        catch (Exception ex)
        {
            // No media session support is not an error worth stopping for — the line just never
            // appears, which is the same as nothing playing.
            Log.Warn($"Now-playing unavailable: {ex.Message}");
        }
    }

    private void AttachSession()
    {
        if (_disposed) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();
        _sourceApp = null;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void Poll()
    {
        if (_disposed || _session is null) return;
        _toMainThread(() =>
        {
            if (_disposed || _session is null) return;
            _ = RefreshAsync();
        });
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // Each event starts its own task, so two reads can be in flight at once and the older one
        // can finish last. Without this an outdated title overwrites the current one, and
        // comparing against _value does not catch it because the values genuinely differ.
        int generation = Interlocked.Increment(ref _generation);

        string? next = null;
        NowPlayingReading reading = default;
        TimeSpan timelineDuration = default;
        Bitmap? fetchedArt = null;
        string artKey = "", artSource = "";
        try
        {
            var session = _session;
            if (session is not null)
            {
                var info = session.GetPlaybackInfo();
                bool playing = info?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string? aumid = _sourceApp ?? session.SourceAppUserModelId;
                var props = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                TimeSpan position = timeline is null ? default : timeline.Position - timeline.StartTime;
                timelineDuration = timeline is null ? default : timeline.EndTime - timeline.StartTime;
                next = Format(props?.Title, props?.Artist, playing, _maxCharacters());
                reading = Read(props?.Title, props?.Artist, playing, position, timelineDuration);

                // Fetched once per track, off the render path. The cache key deliberately ignores
                // playing state, so pausing does not throw the artwork away and refetch it.
                artKey = $"{aumid}|{reading.TrackId}";
                if (artKey != _artKey)
                {
                    fetchedArt = await LoadArtAsync(props?.Thumbnail);
                    artSource = fetchedArt is not null ? "media session" : "";

                    // A browser with no session thumbnail can still have a real cover online. Look
                    // the track up once by artist and title before settling for the app icon.
                    if (fetchedArt is null && !string.IsNullOrWhiteSpace(props?.Artist))
                    {
                        fetchedArt = await OnlineArt.Find(props.Title, props.Artist);
                        artSource = fetchedArt is not null ? "online search" : "";
                    }

                    // A browser playing a short video often publishes no Thumbnail at all. Use the
                    // source app's icon rather than leaving a generic note glyph in the art box.
                    if (fetchedArt is null)
                    {
                        fetchedArt = MusicArt.ForSource(aumid, FriendlySourceName(aumid));
                        artSource = "source app";
                    }
                    if (fetchedArt is not null)
                        Log.Info($"Now playing: art from the {artSource}");
                }
            }
        }
        catch (Exception ex)
        {
            // A session can vanish between the null check and the read. Show nothing rather
            // than leaving the last track on the wallpaper.
            // The type matters: these arrive from another process's session and the message is
            // very often empty, which on its own says nothing at all.
            Log.Warn($"Now-playing read failed: {ex.GetType().Name} {ex.Message}");
        }

        _toMainThread(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _generation))
            {
                fetchedArt?.Dispose();       // a superseded read must not leak its bitmap
                return;
            }

            bool trackChanged = reading.TrackId != _artTrackId;
            bool artChanged = artKey.Length > 0 && artKey != _artKey;
            if (trackChanged || artChanged)
            {
                _art?.Dispose();
                _art = fetchedArt;
                _artTrackId = reading.TrackId;
                _artKey = artKey;
            }
            else
            {
                fetchedArt?.Dispose();
            }

            // A browser can report a duration and still report Position = 0 on every poll. Resetting
            // our clock to that stale zero made the bar fill for a second and snap back. Accept the
            // session's position only when it is at or ahead of our estimate (a real timeline event
            // or a seek forward), when the track or duration changed, or when a full bar followed by
            // zero means the video looped. Otherwise keep the clock we are already running.
            double reported = reading.Progress;
            double estimated = _lastDuration > 0d && _current.IsPlaying ? Estimated() : 0d;
            double duration = timelineDuration.TotalSeconds;
            long now = Environment.TickCount64;
            bool looped = estimated >= 0.98d && reported <= 0.02d;
            if (trackChanged)
            {
                _lastProgress = reported;
                _lastDuration = duration;
                _lastProgressAt = now;
            }
            else if (duration > 0d && (Math.Abs(duration - _lastDuration) > 0.5d
                                      || reported >= estimated - 0.02d || looped))
            {
                _lastProgress = reported;
                _lastDuration = duration;
                _lastProgressAt = now;
            }
            // Otherwise keep the clock we are running: a stale zero from the session is not a seek.
            bool progressStarted = reported > 0.001d && _current.Progress <= 0.001d;
            if (!trackChanged && !artChanged && next == _value && reading == _current) return;
            _value = next;
            _current = reading;
            if (progressStarted)
                Log.Info($"Now playing: progress {reading.Progress:0.00} of {timelineDuration.TotalSeconds:0.0}s");
            Changed?.Invoke();
        });
    }

    /// <summary>Decodes the session's thumbnail into a bitmap we own.
    ///
    /// The bytes come from whatever media player happens to be running, so a malformed or
    /// unexpected stream must fall back to no artwork rather than propagate — a bad thumbnail from
    /// someone else's app should not take the wallpaper down.</summary>
    private static async Task<Bitmap?> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var buffered = new MemoryStream();
            await net.CopyToAsync(buffered);
            buffered.Position = 0;

            // Copied out of the source bitmap: Image.FromStream keeps the stream alive for the
            // lifetime of the image, and this one is disposed on the way out of the method.
            using var decoded = Image.FromStream(buffered);
            return new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Warn($"Album art unavailable: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            _disposed = true;

            if (_manager is not null && _onSessionChanged is not null)
            {
                _manager.CurrentSessionChanged -= _onSessionChanged;
                _onSessionChanged = null;
            }
            _manager = null;
            _poll?.Dispose();
            _poll = null;
        }

        _art?.Dispose();
        _art = null;
        _artTrackId = null;

        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }
}
