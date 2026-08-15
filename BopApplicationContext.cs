using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;
using NHotkey;
using NHotkey.WindowsForms;
using YoutubeDLSharp;
using Bop.Services;

namespace Bop;

public class BopApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _autoStartMenuItem;
    private readonly YoutubeDL _ytDl;
    private readonly ToolStripMenuItem _titleMenuItem;

    private YoutubeUrlResolver? _urlResolver;
    private WaveOutEvent? _outputDevice;
    private MediaFoundationReader? _audioFile;
    private PlayerForm? _playerForm;

    private const string AppName = "Bop";
    private const string RunRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private float _currentVolume = 0.5f;

    private readonly List<QueueItem> _playlistQueue = new();
    private const int MaxQueueSize = 6;

    // Sérialise tous les appels à PlayUrlAsync : évite que deux lectures démarrées
    // en parallèle (double-clic, skip manuel + fin de piste simultanés, etc.)
    // ne se chevauchent en audio.
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    // Anti-rebond pour le toggle play/pause : évite un double-déclenchement
    // (hotkey global + WM_APPCOMMAND) qui annulerait visuellement l'action.
    private DateTime _lastToggleTime = DateTime.MinValue;

    private bool _isAddingToQueue = false;

    public static Icon? GetEmbeddedIcon(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Bop.{resourceName}");
        return stream != null ? new Icon(stream) : null;
    }

    public BopApplicationContext()
    {
        string baseDir = AppContext.BaseDirectory;
        _ytDl = new YoutubeDL
        {
            // Chemins absolus : un exécutable "yt-dlp.exe" relatif pourrait être
            // substitué par un binaire malveillant place plus tôt dans la résolution
            // de chemin (binary planting / search-order hijacking).
            YoutubeDLPath = Path.Combine(baseDir, "yt-dlp.exe"),
            FFmpegPath = Path.Combine(baseDir, "ffmpeg.exe")
        };

        _contextMenu = new ContextMenuStrip();

        _titleMenuItem = new ToolStripMenuItem("BOP - Audio Player", null) { Enabled = false };
        var pastePlayMenuItem = new ToolStripMenuItem("Play copied URL", null, OnPasteAndPlayClicked);
        var togglePlayerMenuItem = new ToolStripMenuItem("Show / Hide Mini-Player", null, OnTogglePlayerClicked);

        _autoStartMenuItem = new ToolStripMenuItem("Launch at startup", null, OnToggleAutoStartClicked)
        {
            Checked = IsAutoStartEnabled()
        };

        var exitMenuItem = new ToolStripMenuItem("Exit", null, OnExitClicked);

        ApplyContextMenuTheme();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        Icon appIcon;
        try
        {
            appIcon = new Icon("gmalalatete.ico"); 
        }
        catch
        {
            appIcon = SystemIcons.Application; 
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = appIcon,
            ContextMenuStrip = _contextMenu,
            Text = "BOP - Audio Player",
            Visible = true
        };

        _notifyIcon.Click += (s, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
            {
                OnTogglePlayerClicked(s, e);
            }
        };

        _contextMenu.Items.Add(_titleMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(pastePlayMenuItem);
        _contextMenu.Items.Add(togglePlayerMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_autoStartMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitMenuItem);

        _playerForm = CreatePlayerForm();
    }

    private PlayerForm CreatePlayerForm()
    {
        return new PlayerForm(
            onVolumeChanged: vol => SetVolume(vol),
            onSeekRequested: targetPercent => SeekToPercent(targetPercent),
            onPlayPauseToggled: () => TogglePlayPause(),
            onStopRequested: () => StopAudio(),
            onAddRequested: async () => await AddClipboardToQueueAsync(),
            onSkipRequested: async () => await SkipNextAsync(),
            onRemoveRequested: id => RemoveFromQueue(id)
        );
    }

    private void RemoveFromQueue(Guid id)
    {
        // Recherche par identifiant stable plutôt que par index : robuste même si
        // la liste a changé entre le moment où l'utilisateur a survolé l'élément
        // et celui où il a cliqué (ex. auto-skip entre-temps).
        int removed = _playlistQueue.RemoveAll(q => q.Id == id);
        if (removed > 0)
        {
            _playerForm?.UpdateQueue(_playlistQueue);
        }
    }

    private async Task SkipNextAsync()
    {
        if (_playlistQueue.Count > 0)
        {
            var nextItem = _playlistQueue[0];
            _playlistQueue.RemoveAt(0);
            _playerForm?.UpdateQueue(_playlistQueue);
            await PlayUrlAsync(nextItem.Url);
        }
        else
        {
            StopAudio();
        }
    }

    private void OnTogglePlayerClicked(object? sender, EventArgs e)
    {
        if (_playerForm == null) return;

        if (_playerForm.Visible)
        {
            _playerForm.Hide();
        }
        else
        {
            _playerForm.Show();
            _playerForm.BringToFront();
        }
    }

    private void SetVolume(float volume)
    {
        _currentVolume = Math.Clamp(volume, 0.0f, 1.0f);
        if (_outputDevice != null)
        {
            _outputDevice.Volume = _currentVolume;
        }
    }

    private void SeekToPercent(double percent)
    {
        if (_audioFile != null)
        {
            var targetTicks = (long)(_audioFile.TotalTime.Ticks * percent);
            _audioFile.CurrentTime = TimeSpan.FromTicks(targetTicks);
        }
    }

    /// <summary>
    /// Vérifie que la chaîne est une URL http(s) absolue valide avant de la transmettre
    /// à yt-dlp. Bloque au passage toute tentative d'injection d'arguments (une chaîne
    /// commençant par "-" ne peut pas être une URI absolue valide).
    /// </summary>
    private static bool IsLikelyValidMediaUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private async void OnPasteAndPlayClicked(object? sender, EventArgs e)
    {
        try
        {
            string clipboardText = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                MessageBox.Show("Clipboard is empty or contains no text.", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!IsLikelyValidMediaUrl(clipboardText))
            {
                MessageBox.Show("This does not look like a valid URL.", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            await PlayUrlAsync(clipboardText);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error clipboard read, please try again.", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlayUrlAsync(string url)
    {
        // Sérialise les appels concurrents (double-clic sur "+", skip manuel pile au
        // moment où la piste se termine naturellement, etc.) : sans ce verrou, deux
        // appels en vol peuvent chacun créer leur propre WaveOutEvent et jouer en
        // même temps (chevauchement audio) en plus de fuir l'ancien flux.
        await _playbackLock.WaitAsync();
        try
        {
            StopAudio();

            if (_playerForm == null || _playerForm.IsDisposed)
            {
                _playerForm = CreatePlayerForm();
            }

            _playerForm.Show();
            _playerForm.BringToFront();
            _playerForm.SetLoadingState("Fetching media info...");

            using var fetchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try
            {
                var mediaInfo = await _ytDl.RunVideoDataFetch(url, fetchTimeout.Token);

                if (mediaInfo != null && mediaInfo.Success && mediaInfo.Data != null && mediaInfo.Data.Formats != null)
                {
                    _urlResolver = new YoutubeUrlResolver();
                    string? bestAudioUrl = _urlResolver.GetBestAudioUrl(mediaInfo.Data.Formats);

                    if (string.IsNullOrEmpty(bestAudioUrl))
                    {
                        _playerForm.SetLoadingState("No audio stream found.");
                        return;
                    }

                    string decodedUrl = System.Net.WebUtility.UrlDecode(bestAudioUrl);

                    MediaFoundationReader? newAudioFile = null;
                    WaveOutEvent? newOutputDevice = null;
                    try
                    {
                        newAudioFile = new MediaFoundationReader(decodedUrl);
                        newOutputDevice = new WaveOutEvent();
                        newOutputDevice.Init(newAudioFile);
                        newOutputDevice.Volume = _currentVolume;
                        newOutputDevice.PlaybackStopped += OnPlaybackStopped;
                        newOutputDevice.Play();
                    }
                    catch
                    {
                        // Nettoyage si l'initialisation échoue à mi-chemin (ex. flux
                        // audio invalide) : évite de fuir un MediaFoundationReader ou
                        // un WaveOutEvent à moitié construit.
                        newOutputDevice?.Dispose();
                        newAudioFile?.Dispose();
                        throw;
                    }

                    _audioFile = newAudioFile;
                    _outputDevice = newOutputDevice;

                    _playerForm.UpdateQueue(_playlistQueue);

                    string channelName = mediaInfo.Data.Uploader ?? mediaInfo.Data.Channel ?? "YouTube";
                    _titleMenuItem.Text = $"BOP - [{channelName}]";

                    string? thumbnailUrl = mediaInfo.Data.Thumbnail;

                    if (string.IsNullOrEmpty(thumbnailUrl) && !string.IsNullOrEmpty(mediaInfo.Data.ID))
                    {
                        thumbnailUrl = $"https://img.youtube.com/vi/{mediaInfo.Data.ID}/hqdefault.jpg";
                    }

                    _playerForm.BindMedia(
                        mediaInfo.Data.Title ?? "Unknown Title",
                        channelName,
                        _outputDevice,
                        _audioFile,
                        _currentVolume,
                        thumbnailUrl
                    );
                }
                else
                {
                    _playerForm.SetLoadingState("Invalid URL or video unavailable.");
                }
            }
            catch (OperationCanceledException)
            {
                _playerForm.SetLoadingState("Timed out fetching media info.");
            }
            catch (Exception ex)
            {
                _playerForm.SetLoadingState($"Unable to read your URL, please try again.");
            }
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    private async void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            // Le flux s'est arrêté à cause d'une erreur (ex. coupure réseau) et non
            // parce que la piste est terminée : ne pas enchaîner sur la suivante
            // silencieusement, informer l'utilisateur à la place.
            _playerForm?.SetLoadingState($"Playback stopped due to a network or stream error.");
            return;
        }

        await SkipNextAsync();
    }

    public async Task AddClipboardToQueueAsync()
    {
        // Empêche un double-clic rapide sur "+" de contourner la limite MaxQueueSize
        // ou de déclencher deux PlayUrlAsync concurrents via la branche "rien ne joue".
        if (_isAddingToQueue) return;
        _isAddingToQueue = true;

        try
        {
            if (_playlistQueue.Count >= MaxQueueSize)
            {
                MessageBox.Show($"No more lil bro ! (Max {MaxQueueSize} videos)", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string url;
            try
            {
                url = Clipboard.GetText().Trim();
            }
            catch
            {
                // Le presse-papier peut être verrouillé par un autre process (ExternalException) :
                // on abandonne silencieusement plutôt que de laisser planter la tâche async.
                return;
            }

            if (string.IsNullOrWhiteSpace(url)) return;

            if (!IsLikelyValidMediaUrl(url))
            {
                MessageBox.Show("This does not look like a valid URL.", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_outputDevice == null || _outputDevice.PlaybackState == PlaybackState.Stopped)
            {
                await PlayUrlAsync(url);
                return;
            }

            var newItem = new QueueItem { Url = url };
            _playlistQueue.Add(newItem);
            _playerForm?.UpdateQueue(_playlistQueue);

            try
            {
                using var fetchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var info = await _ytDl.RunVideoDataFetch(url, fetchTimeout.Token);
                if (info?.Data != null)
                {
                    newItem.Title = info.Data.Title ?? "Unknown Title";
                    newItem.Channel = info.Data.Uploader ?? info.Data.Channel ?? "";
                    newItem.Duration = TimeSpan.FromSeconds(info.Data.Duration ?? 0);
                    _playerForm?.UpdateQueue(_playlistQueue);
                }
            }
            catch { }
        }
        finally
        {
            _isAddingToQueue = false;
        }
    }

    private void TogglePlayPause()
    {
        // PlayerForm peut invoquer ce toggle depuis deux sources pour la même pression
        // physique de touche (hotkey global RegisterHotKey + message WM_APPCOMMAND) :
        // un court anti-rebond évite un double-toggle qui annulerait l'action.
        if ((DateTime.UtcNow - _lastToggleTime).TotalMilliseconds < 200) return;
        _lastToggleTime = DateTime.UtcNow;

        if (_outputDevice == null) return;

        if (_outputDevice.PlaybackState == PlaybackState.Playing)
        {
            _outputDevice.Pause();
        }
        else if (_outputDevice.PlaybackState == PlaybackState.Paused)
        {
            _outputDevice.Play();
        }
    }

    private void StopAudio()
    {
        if (_outputDevice != null)
        {
            _outputDevice.PlaybackStopped -= OnPlaybackStopped;
            _outputDevice.Stop();
            _outputDevice.Dispose();
            _outputDevice = null;
        }

        if (_audioFile != null)
        {
            _audioFile.Dispose();
            _audioFile = null;
        }

        _titleMenuItem.Text = "BOP - Audio Player";
        _playerForm?.Hide();
    }

    private void OnToggleAutoStartClicked(object? sender, EventArgs e)
    {
        bool currentState = _autoStartMenuItem.Checked;
        SetAutoStart(!currentState);
        _autoStartMenuItem.Checked = !currentState;
    }

    private bool IsAutoStartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
        return key?.GetValue(AppName) != null;
    }

    private void SetAutoStart(bool enable)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
        if (key == null) return;

        if (enable)
        {
            // Chemin entre guillemets : un chemin contenant un espace et non quoté
            // peut être mal interprété par Windows au démarrage (ex. "C:\Program.exe"
            // exécuté à la place de "C:\Program Files\Bop\Bop.exe").
            key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    private bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var registryValue = key?.GetValue("AppsUseLightTheme");
            if (registryValue is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    private void ApplyContextMenuTheme()
    {
        bool isDarkMode = IsWindowsInDarkMode();
        _contextMenu.Renderer = new DarkModeContextMenuRenderer(isDarkMode);
        _contextMenu.Invalidate();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            ApplyContextMenuTheme();
        }
    }
}