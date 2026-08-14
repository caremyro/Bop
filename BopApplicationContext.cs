using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;
using NHotkey;
using NHotkey.WindowsForms;
using YoutubeDLSharp;
using Bop.Services;
using System.Reflection;

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

    public static Icon? GetEmbeddedIcon(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Bop.{resourceName}");
        return stream != null ? new Icon(stream) : null;
    }

    public BopApplicationContext()
    {
        _ytDl = new YoutubeDL
        {
            YoutubeDLPath = "yt-dlp.exe",
            FFmpegPath = "ffmpeg.exe"
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

        // Thème sombre / clair
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

        // Construction du menu
        _contextMenu.Items.Add(_titleMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(pastePlayMenuItem);
        _contextMenu.Items.Add(togglePlayerMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_autoStartMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitMenuItem);

        _playerForm = new PlayerForm(
            onVolumeChanged: vol => SetVolume(vol),
            onSeekRequested: targetPercent => SeekToPercent(targetPercent),
            onPlayPauseToggled: () => TogglePlayPause(),
            onStopRequested: () => StopAudio()
        );

        try
        {
            HotkeyManager.Current.AddOrReplace("PlayPause", Keys.P | Keys.Control | Keys.Shift, OnGlobalHotkey);
        }
        catch { }
    }

    // --- GESTION DES ÉVÉNEMENTS DU MENU CONTEXTUEL ---
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

            await PlayUrlAsync(clipboardText);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error clipboard read: {ex.Message}", "BOP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PlayUrlAsync(string url)
    {
        StopAudio();

        if (_playerForm == null || _playerForm.IsDisposed)
        {
            _playerForm = new PlayerForm(
                onVolumeChanged: vol => SetVolume(vol),
                onSeekRequested: targetPercent => SeekToPercent(targetPercent),
                onPlayPauseToggled: () => TogglePlayPause(),
                onStopRequested: () => StopAudio()
            );
        }

        _playerForm.Show();
        _playerForm.BringToFront();
        _playerForm.SetLoadingState("Fetching media info...");

        try
        {
            var mediaInfo = await _ytDl.RunVideoDataFetch(url);

            if (mediaInfo != null && mediaInfo.Success && mediaInfo.Data != null && mediaInfo.Data.Formats != null)
            {
                // Cherche le meilleur flux audio disponible
                _urlResolver = new YoutubeUrlResolver();
                string? bestAudioUrl = _urlResolver.GetBestAudioUrl(mediaInfo.Data.Formats);

                if (string.IsNullOrEmpty(bestAudioUrl))
                {
                    _playerForm.SetLoadingState("No audio stream found.");
                    return;
                }

                // Décode l'URL pour éviter les problèmes d'encodage
                string decodedUrl = System.Net.WebUtility.UrlDecode(bestAudioUrl);

                // Lit l'audio à partir de l'URL décodée
                _audioFile = new MediaFoundationReader(decodedUrl);
                _outputDevice = new WaveOutEvent();
                _outputDevice.Init(_audioFile);
                _outputDevice.Volume = _currentVolume;
                _outputDevice.Play();

                string channelName = mediaInfo.Data.Uploader ?? mediaInfo.Data.Channel ?? "YouTube";
                _titleMenuItem.Text = $"BOP - [{channelName}]";

                string? thumbnailUrl = mediaInfo.Data.Thumbnail;

                if (string.IsNullOrEmpty(thumbnailUrl) && !string.IsNullOrEmpty(mediaInfo.Data.ID))
                {
                    thumbnailUrl = $"https://img.youtube.com/vi/{mediaInfo.Data.ID}/hqdefault.jpg";
                }

                // Met à jour l'interface du lecteur avec les informations récupérées
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
        catch (Exception ex)
        {
            _playerForm.SetLoadingState($"Error: {ex.Message}");
        }
    }

    // --- GESTION DES RACCOURCIS CLAVIER ---
    private void OnGlobalHotkey(object? sender, HotkeyEventArgs e)
    {
        TogglePlayPause();
        e.Handled = true;
    }

    // --- MÉTHODES DE CONTRÔLE DE LA LECTURE AUDIO ---
    private void TogglePlayPause()
    {
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
            key.SetValue(AppName, Application.ExecutablePath);
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

    // --- GESTION DU THÈME SOMBRE / CLAIR ---
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